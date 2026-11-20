#!/usr/bin/env python3
"""
RyvenPanel – Wirtschaft (eco.py)

Wertet die Wirtschaft der Minecraft-Server aus und bearbeitet Shop-Preise. Wird vom Panel (Owner, per SSH) und vom
Team-Gateway (Team-Logins, mit Rechten economy.view / economy.edit) aufgerufen:

    python3 eco.py <befehl> [base64-json]

Befehle: summary {days, server}   – Auswertung eines Zeitraums
         items   {server}         – alle Shop-Items mit Preisen
         setprice {server, path, buy, sell, actor}
         additem  {server, section, material, buy, sell, name, actor}
         reload   {server, actor} – /sreload auf dem Server

Datenquellen: eco_tx / eco_supply (Plugin RyvenEco, alle Geldbewegungen ab Installation), EconomyShopGUI-Verlauf
(SQLite, rückwirkend), ExcellentCrates openings.log (rückwirkend) und Essentials-Kontostände (userdata).
"""
import base64, glob, json, os, re, shutil, sqlite3, ssl, subprocess, sys, time, urllib.request

import yaml

VERSION = "1.2"
SERVERS = os.environ.get("RP_ECO_SERVERS", "/opt/crafty/docker/servers")   # override only for tests
CHANGELOG = "/var/lib/pxmx/eco-changes.log"
GATEWAY_CONF = "/etc/pxmx/gateway.json"
MAT_RX = re.compile(r"^[A-Z0-9_]{2,64}$")
PATH_RX = re.compile(r"^([A-Za-z0-9_\-]{1,64})\.(page\d{1,3})\.items\.([A-Za-z0-9_\-]{1,32})$")
SECTION_RX = re.compile(r"^[A-Za-z0-9_\-]{1,64}$")
ECO_CMD_RX = re.compile(r"^/?(?:eco|economy|money|emoney)\s+(?:give|add)\s+\S+\s+([0-9]+(?:\.[0-9]+)?)\s*$", re.I)
OPEN_RX = re.compile(r"^\[(\d\d)/(\d\d)/(\d{4}) (\d\d):(\d\d):(\d\d)\] (\S+) won (.*?) \(ID: ([^,]+), Weight: [^)]*\) from (.*?) \(ID: ([^)]+)\)\s*$")
COLOR_RX = re.compile(r"(?:&#[0-9a-fA-F]{6}|&[0-9a-fk-orA-FK-OR]|§.|<[^>]{1,40}>)")


class Fail(Exception):
    pass


def clean(s):
    return COLOR_RX.sub("", str(s or "")).strip()


def num(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return 0.0


# ───────────────────────────── servers / database ─────────────────────────────
def props(d):
    p = {}
    try:
        for ln in open(os.path.join(d, "server.properties"), encoding="utf-8", errors="replace"):
            if "=" in ln and not ln.startswith("#"):
                k, v = ln.split("=", 1)
                p[k.strip()] = v.strip()
    except OSError:
        pass
    return p


def velocity_names():
    """port → server name from the Velocity config ([servers] survival = "127.0.0.1:25566")."""
    names = {}
    for f in glob.glob(SERVERS + "/*/velocity.toml"):
        sect = None
        for ln in open(f, encoding="utf-8", errors="replace"):
            s = ln.strip()
            if s.startswith("["):
                sect = s
                continue
            m = re.match(r'^"?([A-Za-z0-9_\-]+)"?\s*=\s*"[^":]*:(\d+)"', s)
            if sect == "[servers]" and m:
                names[m.group(2)] = m.group(1)
    return names


_backends = None


def backends():
    """Paper backends with Essentials: [{id, dir, name, eco_cfg}]"""
    global _backends
    if _backends is not None:
        return _backends
    vn = velocity_names()
    out = []
    for d in sorted(glob.glob(SERVERS + "/*/")):
        d = d.rstrip("/")
        if not (os.path.isdir(d + "/plugins/Essentials") or os.path.isfile(d + "/plugins/CityBuildCore/wirtschaft/konten.yml")
                or os.path.isfile(d + "/plugins/RyvenEco/config.yml")):
            continue
        cfg = {}
        try:
            cfg = yaml.safe_load(open(d + "/plugins/RyvenEco/config.yml", encoding="utf-8")) or {}
        except (OSError, yaml.YAMLError):
            pass
        name = cfg.get("server") or vn.get(props(d).get("server-port", "")) or os.path.basename(d)[:8]
        out.append({"id": os.path.basename(d), "dir": d, "name": str(name), "eco": cfg})
    _backends = out
    return out


def pick(server):
    bs = backends()
    if server in (None, "", "all"):
        return bs
    sel = [b for b in bs if b["name"] == server]
    if not sel:
        raise Fail("Unbekannter Server: " + str(server))
    return sel


def shop_backend(server):
    """The backend whose EconomyShopGUI is in use: prefers one with a transaction history (clones have none)."""
    def rank(b):
        has_db = os.path.exists(b["dir"] + "/plugins/EconomyShopGUI/transactions/transactions.db")
        return (0 if has_db else 1, 0 if b["name"] == "survival" else 1)
    for pool in (pick(server), backends()):
        c = sorted((b for b in pool if os.path.isdir(b["dir"] + "/plugins/EconomyShopGUI/shops")), key=rank)
        if c:
            return c[0]
    raise Fail("Kein Server mit EconomyShopGUI gefunden.")


def db_creds():
    for b in backends():
        d = b["eco"].get("database") or {}
        if d.get("password"):
            return d
    for f in glob.glob(SERVERS + "/*/plugins/ryvenlink/config.yml"):
        try:
            d = (yaml.safe_load(open(f, encoding="utf-8")) or {}).get("database") or {}
            if d.get("password"):
                return d
        except (OSError, yaml.YAMLError):
            pass
    return None


def sql(query):
    """Runs a query in the MariaDB container; returns rows as lists of strings (None for NULL)."""
    d = db_creds()
    if not d:
        return None
    host = str(d.get("host", "mariadb"))
    r = subprocess.run(["docker", "exec", "-i", "-e", "MYSQL_PWD=" + str(d.get("password", "")), host, "sh", "-c",
                        'exec $(command -v mariadb || command -v mysql) -u"$0" -D "$1" -N -B --raw', str(d.get("user", "ryvendiscord")),
                        str(d.get("name", "ryvendiscord"))], input=query.encode(), capture_output=True, timeout=60)
    if r.returncode != 0:
        err = r.stderr.decode(errors="replace")
        if "doesn't exist" in err:
            return None
        raise Fail("Datenbank: " + err.strip()[:300])
    rows = []
    for ln in r.stdout.decode(errors="replace").splitlines():
        rows.append([None if c == "NULL" else c for c in ln.split("\t")])
    return rows


def q(s):
    return "'" + str(s).replace("\\", "\\\\").replace("'", "''") + "'"

# ───────────────────────────── Team-Konten ausblenden ─────────────────────────────
AUSNAHMEN = "/etc/pxmx/eco-ausnahmen.json"
DEFAULT_CFG = {"gruppen": ["owner", "admin"], "ops": True, "stern": True, "spieler": []}
_excl = None
_reasons = {}


def _lp(query):
    r = subprocess.run(["docker", "exec", "-i", "mariadb", "sh", "-c", 'exec mariadb -h127.0.0.1 -uroot -p"$MARIADB_ROOT_PASSWORD" luckperms -N -B'],
                       input=query.encode(), capture_output=True, timeout=30)
    if r.returncode != 0:
        raise OSError(r.stderr.decode(errors="replace")[:200])
    return [ln.split("	") for ln in r.stdout.decode(errors="replace").splitlines()]


def excluded():
    """Konten, die Geld erzeugen koennen → (uuids, namen_klein, gruppen); Gruende in _reasons[uuid]. Pro Prozesslauf gecacht.
    Regeln: LP-Gruppe (inkl. vererbt), Server-OP (ops.json), Permission '*' (direkt oder ueber Gruppe), manuelle Liste."""
    global _excl
    if _excl is not None:
        return _excl
    cfg = dict(DEFAULT_CFG)
    try:
        cfg.update(json.load(open(AUSNAHMEN, encoding="utf-8")))
    except FileNotFoundError:
        try:
            json.dump(DEFAULT_CFG, open(AUSNAHMEN, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
        except OSError:
            pass
    except (OSError, ValueError):
        pass
    groups = [str(g).lower() for g in (cfg.get("gruppen") or [])]
    uuids, names = set(), set()
    why = _reasons

    def mark(u, reason):
        u = u.lower()
        uuids.add(u)
        if reason not in why.setdefault(u, []):
            why[u].append(reason)

    for x in cfg.get("spieler") or []:
        x = str(x).strip().lower()
        if re.fullmatch(r"[0-9a-f-]{32,36}", x):
            mark(x, "manuell")
        else:
            names.add(x)
    ok = "value=1 and (expiry=0 or expiry>unix_timestamp())"
    try:
        parents, star = {}, set()
        for g, p in _lp("select name, permission from luckperms_group_permissions where %s and (permission='*' or permission like 'group.%%')" % ok):
            if p == "*":
                star.add(g.lower())
            else:
                parents.setdefault(g.lower(), set()).add(p[6:].lower())

        def closure(gs):
            seen, todo = set(), list(gs)
            while todo:
                g = todo.pop()
                if g not in seen:
                    seen.add(g)
                    todo.extend(parents.get(g, ()))
            return seen
        for u, p in _lp("select uuid, permission from luckperms_user_permissions where %s and (permission='*' or permission like 'group.%%')" % ok):
            u = u.lower()
            if p == "*":
                if cfg.get("stern", True):
                    mark(u, "Permission *")
                continue
            for g in closure([p[6:].lower()]):
                if g in groups:
                    mark(u, "Gruppe " + g)
                if g in star and cfg.get("stern", True):
                    mark(u, "* via Gruppe " + g)
        for u, n in _lp("select uuid, username from luckperms_players"):
            if u.lower() in uuids and n:
                names.add(n.lower())
    except (OSError, subprocess.SubprocessError):
        pass
    if cfg.get("ops", True):
        for f in glob.glob("/opt/crafty/docker/servers/*/ops.json"):
            try:
                for o in json.load(open(f, encoding="utf-8")):
                    if o.get("uuid"):
                        mark(o["uuid"], "OP")
                        if o.get("name"):
                            names.add(str(o["name"]).lower())
            except (OSError, ValueError, AttributeError):
                pass
    _excl = (uuids, names, groups)
    return _excl


def is_excl(uid, name=None):
    u, n, _ = excluded()
    if uid and str(uid).lower() in u:
        return True
    if name and str(name).lower() in n:
        return True
    return bool(uid) and str(uid).startswith("name:") and str(uid)[5:].lower() in n


# ───────────────────────────── Essentials accounts ─────────────────────────────
MONEY_RX = re.compile(r"(?m)^money:\s*'?(-?[0-9.Ee+]+)'?\s*$")
NAME_RX = re.compile(r"(?m)^last-account-name:\s*'?([^'\n]+)'?\s*$")


def accounts(bs):
    """uuid → {name, balance} summed over the given backends. CityBuildCore money (wirtschaft/konten.yml) wins;
    the old Essentials userdata is only a fallback for servers without it."""
    acc = {}
    for b in bs:
        kf = b["dir"] + "/plugins/CityBuildCore/wirtschaft/konten.yml"
        if os.path.isfile(kf):
            try:
                data = yaml.safe_load(open(kf, encoding="utf-8")) or {}
            except (OSError, yaml.YAMLError):
                data = None
            if isinstance(data, dict) and data:
                for uid, x in data.items():
                    if not isinstance(x, dict):
                        continue
                    a = acc.setdefault(str(uid), {"name": None, "balance": 0.0})
                    a["balance"] += num(x.get("geld", 0))
                    if x.get("name") and not a["name"]:
                        a["name"] = str(x["name"]).strip()
                continue
        for f in glob.glob(b["dir"] + "/plugins/Essentials/userdata/*.yml"):
            uid = os.path.basename(f)[:-4]
            try:
                t = open(f, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            m = MONEY_RX.search(t)
            n = NAME_RX.search(t)
            a = acc.setdefault(uid, {"name": None, "balance": 0.0})
            if m:
                a["balance"] += num(m.group(1))
            if n and not a["name"]:
                a["name"] = n.group(1).strip()
    return acc


def visible_accounts(bs):
    """accounts() ohne Team-Konten → (acc, ausgeblendet_anzahl, ausgeblendet_guthaben, namen)."""
    acc = accounts(bs)
    hid = [u for u, x in acc.items() if is_excl(u, x["name"])]
    info = (len(hid), sum(acc[u]["balance"] for u in hid), sorted(acc[u]["name"] or u[:8] for u in hid), {acc[u]["name"] or u[:8]: _reasons.get(u.lower(), ["Name"]) for u in hid})
    for u in hid:
        del acc[u]
    return (acc,) + info


# ───────────────────────────── shop (EconomyShopGUI) ─────────────────────────────
def shop_index(b):
    """'Section.pageN.items.id' → item info, from the shops/*.yml files."""
    idx = {}
    for f in sorted(glob.glob(b["dir"] + "/plugins/EconomyShopGUI/shops/*.yml")):
        section = os.path.basename(f)[:-4]
        try:
            data = yaml.safe_load(open(f, encoding="utf-8")) or {}
        except (OSError, yaml.YAMLError):
            continue
        for page, pd in (data.get("pages") or {}).items():
            for iid, it in ((pd or {}).get("items") or {}).items():
                if not isinstance(it, dict):
                    continue
                mat = str(it.get("material") or "")
                name = clean(it.get("displayname") or it.get("name") or "")
                if not name:
                    name = mat.replace("_", " ").title()
                idx["%s.%s.items.%s" % (section, page, iid)] = {
                    "path": "%s.%s.items.%s" % (section, page, iid), "section": section, "page": str(page), "id": str(iid),
                    "material": mat, "name": name, "buy": num(it.get("buy", -1)) if it.get("buy") is not None else -1,
                    "sell": num(it.get("sell", -1)) if it.get("sell") is not None else -1, "hidden": bool(it.get("hidden", False))}
    return idx


def shop_history(b, since, idx):
    """Yields (ts_ms, uuid, action, [(path, amount, money)]) from EconomyShopGUI's transaction database."""
    f = b["dir"] + "/plugins/EconomyShopGUI/transactions/transactions.db"
    if not os.path.exists(f):
        return
    con = sqlite3.connect("file:" + f + "?mode=ro", uri=True, timeout=10)
    try:
        tables = [r[0] for r in con.execute("select name from sqlite_master where type='table' and name like '%ITEM_TRANSACTIONS%'")]
        for t in tables:
            multi = t.startswith("MULTIPLE")
            col = "items" if multi else "item"
            for date, uuid, items, prices, amount, action in con.execute(
                    'select date, uuid, %s, prices, amount, action from "%s" where date >= ?' % (col, t.replace('"', "")), (since,)):
                try:
                    total = sum(num(v) for v in json.loads(prices).values())
                except (ValueError, AttributeError):
                    total = 0.0
                action = (action or "").upper()
                if not multi:
                    parts = [(items, int(amount or 0), total)]
                else:
                    try:
                        lst = json.loads(items)
                    except ValueError:
                        lst = []
                    key = "sell" if action == "SELL" else "buy"
                    weights = []
                    for e in lst:
                        unit = idx.get(e.get("itemPath"), {}).get(key, 0)
                        weights.append(max(unit, 0) * int(e.get("amount", 0)))
                    if sum(weights) <= 0:
                        weights = [int(e.get("amount", 0)) for e in lst]
                    tw = sum(weights) or 1
                    parts = [(e.get("itemPath"), int(e.get("amount", 0)), total * w / tw) for e, w in zip(lst, weights)]
                yield int(date), uuid, action, parts
    finally:
        con.close()


# ───────────────────────────── crates (ExcellentCrates) ─────────────────────────────
def walk_strings(o):
    if isinstance(o, str):
        yield o
    elif isinstance(o, dict):
        for v in o.values():
            yield from walk_strings(v)
    elif isinstance(o, list):
        for v in o:
            yield from walk_strings(v)


def eco_amount(node):
    total = 0.0
    for s in walk_strings(node):
        m = ECO_CMD_RX.match(s.strip())
        if m:
            total += num(m.group(1))
    return total


def crate_money_map(b):
    """(crate_id, reward_id) / reward_id → money paid by that reward (from 'eco give …' commands)."""
    base = b["dir"] + "/plugins/ExcellentCrates"
    by_pair, by_id, names = {}, {}, {}
    for f in glob.glob(base + "/objects/reward/**/*.yml", recursive=True):
        try:
            data = yaml.safe_load(open(f, encoding="utf-8")) or {}
        except (OSError, yaml.YAMLError):
            continue
        rid = os.path.basename(f)[:-4]
        folder = os.path.basename(os.path.dirname(f))          # objects/reward/<crate>/<reward>.yml
        amt = eco_amount(data.get("commands") or data)
        if amt:
            by_id.setdefault(rid, amt)
            by_pair[(folder, rid)] = amt
        nm = clean(((data.get("preview") or {}).get("name")) or "")
        if nm:
            names[rid] = nm
    for f in glob.glob(base + "/crates/*.yml"):
        cid = os.path.basename(f)[:-4]
        try:
            data = yaml.safe_load(open(f, encoding="utf-8")) or {}
        except (OSError, yaml.YAMLError):
            continue
        rl = ((data.get("Rewards") or {}).get("List")) or {}
        if isinstance(rl, dict):
            for rid, rd in rl.items():
                amt = eco_amount(rd)
                if amt:
                    by_pair[(cid, str(rid))] = amt
    return by_pair, by_id, names


def crate_openings(b, since_ms):
    f = b["dir"] + "/plugins/ExcellentCrates/openings.log"
    if not os.path.exists(f):
        return
    since = since_ms / 1000.0
    with open(f, encoding="utf-8", errors="replace") as fh:
        for ln in fh:
            m = OPEN_RX.match(ln)
            if not m:
                continue
            d, mo, y, h, mi, s = (int(x) for x in m.group(1, 2, 3, 4, 5, 6))
            ts = time.mktime((y, mo, d, h, mi, s, 0, 0, -1))
            if ts < since:
                continue
            yield int(ts * 1000), m.group(7), clean(m.group(8)), m.group(9).strip(), clean(m.group(10)), m.group(11).strip()


# ───────────────────────────── summary ─────────────────────────────
BUCKETS = {1: 3600, 3: 3 * 3600, 7: 6 * 3600, 14: 12 * 3600, 30: 86400}


def summary(a):
    days = int(a.get("days", 7))
    if days not in BUCKETS:
        days = 7
    bs = pick(a.get("server"))
    names = [b["name"] for b in bs]
    now = int(time.time() * 1000)
    step = BUCKETS[days] * 1000
    start = now - days * 86400 * 1000
    start -= start % step
    nb = (now - start) // step + 1
    bucket = lambda ts: min(nb - 1, max(0, (ts - start) // step))
    buckets = [start + i * step for i in range(nb)]

    acc, hid_n, hid_bal, hid_names, hid_why = visible_accounts(bs)
    by_name = {}
    for uid, x in acc.items():
        if x["name"]:
            by_name[x["name"].lower()] = uid

    flow_in = [0.0] * nb
    flow_out = [0.0] * nb
    sources = {}
    players = {}
    admin = {"in": 0.0, "out": 0.0, "count": 0}
    admin_team = {"count": 0}
    warnings = []

    def src(name):
        return sources.setdefault(name, {"source": name, "in": 0.0, "out": 0.0, "count": 0, "players": set()})

    def pl(uid, name=None):
        p = players.setdefault(uid, {"uuid": uid, "name": name or (acc.get(uid) or {}).get("name") or uid[:8], "in": 0.0, "out": 0.0, "count": 0,
                                     "shop_sell": 0.0, "shop_buy": 0.0, "crates": 0.0, "pay_in": 0.0, "pay_out": 0.0, "jobs": 0.0, "other": 0.0})
        if name and p["name"] == uid[:8]:
            p["name"] = name
        return p

    def add(ts, uid, name, amount, source):
        if str(source).lower() == "admin":      # /eco-Befehle vom Team verfälschen Geldfluss und Ranglisten – ausgeblendet
            admin["in" if amount >= 0 else "out"] += abs(amount)
            admin["count"] += 1
            return
        if is_excl(uid, name):                  # Team-Konten zaehlen nicht zur Wirtschaft
            admin_team["count"] += 1
            return
        b = bucket(ts)
        s = src(source)
        p = pl(uid, name)
        if amount >= 0:
            flow_in[b] += amount
            s["in"] += amount
            p["in"] += amount
        else:
            flow_out[b] -= amount
            s["out"] -= amount
            p["out"] -= amount
        s["count"] += 1
        s["players"].add(uid)
        p["count"] += 1
        low = source.lower()
        if low.startswith("economyshopgui"):
            p["shop_sell" if amount >= 0 else "shop_buy"] += abs(amount)
        elif low.startswith("excellentcrates"):
            p["crates"] += amount
        elif low == "pay":
            p["pay_in" if amount >= 0 else "pay_out"] += abs(amount)
        elif low == "jobs":
            p["jobs"] += amount
        else:
            p["other"] += amount

    # 1) complete log from RyvenEco (since installation, per server)
    tracking = {}
    rows = sql("select server, min(ts) from eco_tx group by server")
    for r in rows or []:
        if r[0] in names:
            tracking[r[0]] = int(r[1])
    where = "ts >= %d and server in (%s)" % (start, ",".join(q(n) for n in names))
    for r in sql("select ts, uuid, name, delta, source from eco_tx where " + where + " order by ts") or []:
        add(int(r[0]), r[1], r[2], num(r[3]), r[4])

    # 2) before tracking started: shop + crates from their own history
    shop = {"sell": 0.0, "buy": 0.0, "sell_n": 0, "buy_n": 0, "sell_b": [0.0] * nb, "buy_b": [0.0] * nb}
    items = {}
    sb = None
    try:
        sb = shop_backend(a.get("server"))
    except Fail:
        pass
    if sb and sb["name"] in names:
        idx = shop_index(sb)
        cut = tracking.get(sb["name"], now + 1)
        for ts, uid, action, parts in shop_history(sb, start, idx):
            if is_excl(uid):
                continue
            money = sum(p[2] for p in parts)
            sell = action == "SELL"
            b = bucket(ts)
            shop["sell" if sell else "buy"] += money
            shop["sell_n" if sell else "buy_n"] += 1
            shop["sell_b" if sell else "buy_b"][b] += money
            for path, amount, m in parts:
                it = items.get(path)
                if it is None:
                    info = idx.get(path) or {"path": path, "name": path, "material": "", "section": (path or "").split(".")[0], "buy": -1, "sell": -1}
                    it = items[path] = {"path": path, "name": info["name"], "material": info["material"], "section": info["section"],
                                        "buy_price": info["buy"], "sell_price": info["sell"], "sold": 0, "sold_money": 0.0,
                                        "bought": 0, "bought_money": 0.0, "tx": 0, "players": set()}
                it["sold" if sell else "bought"] += amount
                it["sold_money" if sell else "bought_money"] += m
                it["tx"] += 1
                it["players"].add(uid)
            if ts < cut:
                add(ts, uid, None, money if sell else -money, "EconomyShopGUI")

    crates = {}
    rewards = {}
    crate_b = [0.0] * nb
    open_b = [0] * nb
    seen = set()   # a cloned server carries a copy of the same openings.log
    for b in bs:
        if not os.path.isdir(b["dir"] + "/plugins/ExcellentCrates"):
            continue
        by_pair, by_id, rnames = crate_money_map(b)
        cut = tracking.get(b["name"], now + 1)
        for ts, player, rname, rid, cname, cid in crate_openings(b, start):
            if is_excl(by_name.get(player.lower()), player) or player.lower() in excluded()[1]:
                continue
            key = (ts, player, rid, cid)
            if key in seen:
                continue
            seen.add(key)
            money = by_pair.get((cid, rid), by_id.get(rid, 0.0))
            c = crates.setdefault(cid, {"id": cid, "name": cname or cid, "opens": 0, "money": 0.0, "money_wins": 0, "players": {}})
            c["opens"] += 1
            c["money"] += money
            c["players"][player] = c["players"].get(player, 0) + 1
            if money:
                c["money_wins"] += 1
            pretty = rname or rnames.get(rid) or re.sub(r"[^\w]+|_", " ", rid).strip().title() or rid
            r = rewards.setdefault((cid, rid), {"crate": cname or cid, "crate_id": cid, "id": rid, "name": pretty,
                                                 "count": 0, "money_each": money, "money": 0.0})
            r["count"] += 1
            r["money"] += money
            k = bucket(ts)
            crate_b[k] += money
            open_b[k] += 1
            uid = by_name.get(player.lower(), "name:" + player)
            pl(uid, player)
            if money and ts < cut:
                add(ts, uid, player, money, "ExcellentCrates")

    # 3) money supply curve: today's balances minus all later flows ("Kurs")
    current = sum(x["balance"] for x in acc.values())
    supply = [0.0] * nb
    running = current
    for i in range(nb - 1, -1, -1):
        supply[i] = running
        running -= flow_in[i] - flow_out[i]
    supply_start = running
    snaps = []
    for r in sql("select ts, server, total from eco_supply where ts >= %d and server in (%s) order by ts" % (start, ",".join(q(n) for n in names))) or []:
        snaps.append([int(r[0]), r[1], num(r[2])])

    full_since = min(tracking.values()) if len(tracking) == len(names) and tracking else None
    if not tracking:
        warnings.append("RyvenEco protokolliert noch keine Geldbewegungen – bis dahin zählen nur Shop und Crates (rückwirkend).")
    elif full_since and full_since > start:
        warnings.append("Alle Geldquellen (Pay, Jobs, Votes, …) werden erst seit %s erfasst – davor nur Shop und Crates."
                        % time.strftime("%d.%m.%Y %H:%M", time.localtime(full_since / 1000)))

    total_in, total_out = sum(flow_in), sum(flow_out)
    for p in players.values():
        p["balance"] = round((acc.get(p["uuid"]) or {}).get("balance", 0.0), 2)
    richest = sorted(({"uuid": u, "name": x["name"] or u[:8], "balance": round(x["balance"], 2)} for u, x in acc.items()), key=lambda x: -x["balance"])[:25]

    def fix_set(d, key):
        d[key] = len(d[key])
        return d

    return {
        "version": VERSION, "days": days, "servers": names, "all_servers": [b["name"] for b in backends()], "now": now, "start": start,
        "step": step, "buckets": buckets, "flow_in": flow_in, "flow_out": flow_out, "supply": supply, "supply_start": supply_start,
        "supply_now": current, "accounts": len(acc), "snapshots": snaps, "total_in": total_in, "total_out": total_out,
        "tracking": tracking, "warnings": warnings, "tax": tax_info(), "taxes": tax_list(start),
        "admin_ausgeblendet": admin,
        "ausgeblendet": {"anzahl": hid_n, "gruppen": excluded()[2], "gruende": hid_why, "guthaben": round(hid_bal, 2), "namen": hid_names, "buchungen": admin_team["count"]},
        "snapshots_inkl_team": True,
        "sources": sorted((fix_set(s, "players") for s in sources.values()), key=lambda s: -(s["in"] + s["out"])),
        "players": sorted(players.values(), key=lambda p: -p["in"]),
        "richest": richest,
        "shop": {"sell": shop["sell"], "buy": shop["buy"], "sell_n": shop["sell_n"], "buy_n": shop["buy_n"],
                 "sell_b": shop["sell_b"], "buy_b": shop["buy_b"], "server": sb["name"] if sb else None,
                 "items": sorted((fix_set(i, "players") for i in items.values()), key=lambda i: -(i["sold_money"] + i["bought_money"]))[:400]},
        "crates": {"list": sorted(({**c, "players": len(c["players"]),
                                    "top": sorted(c["players"].items(), key=lambda kv: -kv[1])[:3]} for c in crates.values()), key=lambda c: -c["money"]),
                   "rewards": sorted(rewards.values(), key=lambda r: (-r["money"], -r["count"]))[:500],
                   "money_b": crate_b, "opens_b": open_b},
    }


# ───────────────────────────── shop items: list / edit ─────────────────────────────
def items_cmd(a):
    b = shop_backend(a.get("server"))
    idx = shop_index(b)
    sections = sorted({i["section"] for i in idx.values()})
    return {"server": b["name"], "sections": sections, "items": list(idx.values())}


def key_of(line):
    m = re.match(r"^\s*(['\"]?)([^'\":#][^:#]*?)\1\s*:(?:\s|$)", line)
    return m.group(2) if m else None


def indent_of(line):
    return len(line) - len(line.lstrip(" "))


def meaningful(line):
    s = line.strip()
    return bool(s) and not s.startswith("#")


def find_block(lines, keys):
    """Locates a nested mapping key. Returns (key_line, block_end, child_indent_of_parent)."""
    start, end, parent = 0, len(lines), -1
    pos = None
    for k in keys:
        found, child = None, None
        for i in range(start, end):
            if not meaningful(lines[i]):
                continue
            ind = indent_of(lines[i])
            if ind <= parent:
                break
            if child is None:
                child = ind
            if ind == child and key_of(lines[i]) == k:
                found = i
                break
        if found is None:
            return None
        j = found + 1
        while j < end and (not meaningful(lines[j]) or indent_of(lines[j]) > child):
            j += 1
        pos, start, end, parent = found, found + 1, j, child
    return pos, end, parent


def fmt_price(v):
    v = float(v)
    return str(int(v)) if v == int(v) else ("%.2f" % v)


def set_child(lines, pos, end, key, value):
    child = None
    for i in range(pos + 1, end):
        if meaningful(lines[i]):
            if child is None:
                child = indent_of(lines[i])
            if indent_of(lines[i]) == child and key_of(lines[i]) == key:
                lines[i] = " " * child + key + ": " + value
                return
    ind = child if child is not None else indent_of(lines[pos]) + 2
    lines.insert(pos + 1, " " * ind + key + ": " + value)


def shop_file(b, section):
    if not SECTION_RX.match(section or ""):
        raise Fail("Ungültiger Shop-Name.")
    f = b["dir"] + "/plugins/EconomyShopGUI/shops/" + section + ".yml"
    if not os.path.isfile(f):
        raise Fail("Shop-Datei nicht gefunden: " + section)
    return f


def write_checked(f, lines, check, bdir=None):
    text = "\n".join(lines) + "\n"
    data = yaml.safe_load(text)
    if not check(data):
        raise Fail("Änderung konnte nicht sicher geschrieben werden – Datei unverändert.")
    bdir = bdir or os.path.join(os.path.dirname(os.path.dirname(f)), "panel-backups")
    os.makedirs(bdir, exist_ok=True)
    shutil.copy2(f, os.path.join(bdir, os.path.basename(f) + "." + time.strftime("%Y%m%d-%H%M%S")))
    st = os.stat(f)
    tmp = f + ".panel-tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    os.chown(tmp, st.st_uid, st.st_gid)
    os.chmod(tmp, st.st_mode & 0o7777)
    os.replace(tmp, f)
    # keep only the 30 newest backups per file
    old = sorted(glob.glob(os.path.join(bdir, os.path.basename(f) + ".*")))[:-30]
    for o in old:
        os.remove(o)


def log_change(actor, text):
    try:
        os.makedirs(os.path.dirname(CHANGELOG), exist_ok=True)
        with open(CHANGELOG, "a", encoding="utf-8") as fh:
            fh.write("%s\t%s\t%s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), actor or "?", text))
    except OSError:
        pass


def price_arg(v, label):
    if v is None or v == "":
        return None
    v = float(v)
    if v != -1 and (v < 0 or v > 1e12):
        raise Fail(label + " muss ≥ 0 sein (oder -1 = deaktiviert).")
    return v


def setprice(a):
    b = shop_backend(a.get("server"))
    m = PATH_RX.match(a.get("path") or "")
    if not m:
        raise Fail("Ungültiger Item-Pfad.")
    section, page, iid = m.groups()
    buy, sell = price_arg(a.get("buy"), "Kaufpreis"), price_arg(a.get("sell"), "Verkaufspreis")
    if buy is None and sell is None:
        raise Fail("Nichts zu ändern.")
    f = shop_file(b, section)
    lines = open(f, encoding="utf-8").read().split("\n")
    while lines and lines[-1] == "":
        lines.pop()
    blk = find_block(lines, ["pages", page, "items", iid])
    if not blk:
        raise Fail("Item nicht gefunden: " + a["path"])
    pos, end, _ = blk
    old = (shop_index(b).get(a["path"]) or {})
    if buy is not None:
        set_child(lines, pos, end, "buy", fmt_price(buy))
        end = find_block(lines, ["pages", page, "items", iid])[1]
    if sell is not None:
        set_child(lines, pos, end, "sell", fmt_price(sell))

    def check(d):
        it = d["pages"][page]["items"]
        it = it.get(iid, it.get(int(iid) if iid.isdigit() else iid))
        return (buy is None or float(it["buy"]) == buy) and (sell is None or float(it["sell"]) == sell)

    write_checked(f, lines, check)
    log_change(a.get("actor"), "Preis %s (%s): Kauf %s → %s, Verkauf %s → %s" % (
        a["path"], old.get("name", ""), fmt_price(old.get("buy", -1)), fmt_price(buy) if buy is not None else "=",
        fmt_price(old.get("sell", -1)), fmt_price(sell) if sell is not None else "="))
    return {"ok": True, "path": a["path"]}


def additem(a):
    b = shop_backend(a.get("server"))
    section = a.get("section") or ""
    mat = (a.get("material") or "").strip().upper()
    if not MAT_RX.match(mat):
        raise Fail("Ungültiges Material (z. B. DIAMOND, OAK_LOG).")
    buy, sell = price_arg(a.get("buy"), "Kaufpreis"), price_arg(a.get("sell"), "Verkaufspreis")
    buy = -1 if buy is None else buy
    sell = -1 if sell is None else sell
    name = str(a.get("name") or "").strip()
    if len(name) > 80 or any(c in name for c in "\n\r'"):
        raise Fail("Ungültiger Anzeigename.")
    f = shop_file(b, section)
    lines = open(f, encoding="utf-8").read().split("\n")
    while lines and lines[-1] == "":
        lines.pop()
    data = yaml.safe_load("\n".join(lines)) or {}
    pages = data.get("pages") or {}
    if not pages:
        raise Fail("Shop hat keine Seiten.")
    page = sorted(pages.keys(), key=lambda p: int(re.sub(r"\D", "", str(p)) or 0))[-1]
    ids = [int(k) for k in ((pages[page] or {}).get("items") or {}).keys() if str(k).isdigit()]
    if len(ids) >= 45:
        raise Fail("Die letzte Seite (%s) ist voll (45 Items) – bitte im Spiel mit /editshop eine neue Seite anlegen." % page)
    new_id = str(max(ids) + 1 if ids else 1)
    blk = find_block(lines, ["pages", str(page), "items"])
    if not blk:
        raise Fail("items-Bereich nicht gefunden.")
    pos, end, _ = blk
    last = end - 1
    while last > pos and not meaningful(lines[last]):
        last -= 1
    child = None
    for i in range(pos + 1, end):
        if meaningful(lines[i]):
            child = indent_of(lines[i])
            break
    if child is None:
        child = indent_of(lines[pos]) + 2
    block = [" " * child + "'" + new_id + "':", " " * (child + 2) + "material: " + mat]
    if name:
        block.append(" " * (child + 2) + "displayname: '" + name + "'")
    block += [" " * (child + 2) + "buy: " + fmt_price(buy), " " * (child + 2) + "sell: " + fmt_price(sell)]
    lines[last + 1:last + 1] = block

    def check(d):
        it = d["pages"][page]["items"]
        it = it.get(new_id, it.get(int(new_id)))
        return it and it["material"] == mat

    write_checked(f, lines, check)
    path = "%s.%s.items.%s" % (section, page, new_id)
    log_change(a.get("actor"), "Item hinzugefügt %s: %s Kauf %s Verkauf %s" % (path, mat, fmt_price(buy), fmt_price(sell)))
    return {"ok": True, "path": path}


def tax_list(start=None):
    """Verkaufssteuer je Server (eigene RyvenEco-Config) + im Zeitraum eingenommene Steuer."""
    got = {}
    if start is not None:
        try:
            for r in sql("select server, sum(-delta), count(*) from eco_tx where source='Steuer' and ts >= %d group by server" % start) or []:
                got[r[0]] = (num(r[1]), int(num(r[2])))
        except Fail:
            pass
    out = []
    for b in backends():
        if not b["eco"]:
            continue
        t = b["eco"].get("sell-tax") or {}
        g = got.get(b["name"], (0.0, 0))
        out.append({"server": b["name"], "enabled": bool(t.get("enabled", False)), "percent": num(t.get("percent", 0)),
                    "sources": [str(s) for s in (t.get("sources") or [])], "eingenommen": g[0], "buchungen": g[1]})
    return out


def tax_info():
    l = tax_list()
    return {k: v for k, v in l[0].items() if k in ("enabled", "percent", "sources")} if l else {"enabled": False, "percent": 0.0, "sources": []}


def crafty_stdin(server_id, command):
    tok = json.load(open(GATEWAY_CONF, encoding="utf-8")).get("crafty_token")
    if not tok:
        raise Fail("Kein Crafty-Token gefunden.")
    req = urllib.request.Request("https://127.0.0.1:8443/api/v2/servers/%s/stdin" % server_id, data=command.encode(), method="POST",
                                 headers={"Authorization": "Bearer " + tok, "Content-Type": "text/plain"})
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    with urllib.request.urlopen(req, context=ctx, timeout=15) as r:
        r.read()


def settax(a):
    """Setzt die Verkaufssteuer – nur auf dem Server a["server"] (Name) oder ohne Angabe auf allen – und lädt RyvenEco neu."""
    pct = float(a.get("percent", 0))
    ziel = str(a.get("server") or "")
    if ziel and ziel not in [b["name"] for b in backends()]:
        raise Fail("Unbekannter Server: " + ziel)
    if pct < 0 or pct > 90:
        raise Fail("Steuersatz muss zwischen 0 und 90 % liegen.")
    enabled = bool(a.get("enabled", True)) and pct > 0
    done = []
    for b in backends():
        if ziel and b["name"] != ziel:
            continue
        f = b["dir"] + "/plugins/RyvenEco/config.yml"
        if not os.path.isfile(f):
            continue
        lines = open(f, encoding="utf-8").read().split("\n")
        while lines and lines[-1] == "":
            lines.pop()
        blk = find_block(lines, ["sell-tax"])
        if not blk:
            lines += ["sell-tax:", "  enabled: true", "  percent: 10", "  sources: [EconomyShopGUI, AxSellwands]",
                      "  message: \"&8[&cSteuer&8] &7Verkaufssteuer %percent% %: &c-%tax% $ &8· &7ausgezahlt: &a%net% $\""]
            blk = find_block(lines, ["sell-tax"])
        pos, end, _ = blk
        set_child(lines, pos, end, "enabled", "true" if enabled else "false")
        pos, end, _ = find_block(lines, ["sell-tax"])
        set_child(lines, pos, end, "percent", fmt_price(pct))

        def check(d):
            t = d.get("sell-tax") or {}
            return bool(t.get("enabled")) == enabled and float(t.get("percent")) == pct

        write_checked(f, lines, check, os.path.dirname(f) + "/backups")
        try:
            crafty_stdin(b["id"], "ryveneco reload")
        except Exception:  # server offline – picks the config up on its next start
            pass
        done.append(b["name"])
    if not done:
        raise Fail("RyvenEco ist auf keinem Server installiert.")
    log_change(a.get("actor"), "Verkaufssteuer: %s %% (%s) auf %s" % (fmt_price(pct), "an" if enabled else "aus", ", ".join(done)))
    return {"ok": True, "servers": done, "percent": pct, "enabled": enabled}


def reload_cmd(a):
    b = shop_backend(a.get("server"))
    crafty_stdin(b["id"], "sreload")
    log_change(a.get("actor"), "Shop neu geladen (sreload) auf " + b["name"])
    return {"ok": True, "server": b["name"]}


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "summary"
    arg = {}
    if len(sys.argv) > 2:
        arg = json.loads(base64.b64decode(sys.argv[2]).decode() if sys.argv[2] != "-" else sys.stdin.read())
    fn = {"summary": summary, "items": items_cmd, "setprice": setprice, "additem": additem, "reload": reload_cmd, "settax": settax,
          "version": lambda a: {"version": VERSION}}.get(cmd)
    try:
        if not fn:
            raise Fail("Unbekannter Befehl")
        res = {"status": "ok", "data": fn(arg)}
    except Fail as e:
        res = {"status": "error", "error": str(e)}
    except Exception as e:  # noqa – report, never crash with a trace
        res = {"status": "error", "error": "%s: %s" % (type(e).__name__, str(e)[:300])}
    sys.stdout.write(json.dumps(res, ensure_ascii=False, separators=(",", ":")))


if __name__ == "__main__":
    main()
