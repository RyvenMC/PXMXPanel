#!/usr/bin/env python3
"""
RyvenPanel – Moderation (mod.py)

Reports, Strafen (RyvenBans) und Discord-Verknüpfungen (Verify) aus der RyvenLink-Datenbank lesen und Strafen über
RyvenBans auf dem Proxy ausführen. Aufruf durch das Panel (Owner, per SSH) oder das Team-Gateway (Team-Logins mit den
Rechten moderation.*):

    python3 mod.py <befehl> [base64-json]

Befehle: overview                                  – Zähler für die Übersicht
         reports     {status, q, limit}            – Reports (open / claimed / done / rejected / all)
         report      {id}                          – ein Report inkl. Chatverlauf
         report_set  {id, status, actor}           – Report übernehmen / erledigen / ablehnen (Melder bekommt Nachricht)
         punishments {type, active, q, limit}      – Strafen (ban / mute / kick / warn / all)
         links       {q, limit}                    – Discord-Verknüpfungen
         player      {name}                        – Spieler: Daten, Verknüpfung, Strafen, Reports
         templates                                 – Strafvorlagen aus der RyvenLink-Config
         act         {action, player, duration, reason, silent, actor}
         verbindungen                              – Datenschutz-Wächter: live offene + bisher gesehene ausgehende Verbindungen
         verbindung_ok {ip, dienst, actor}         – Ziel als geprüft/bekannt markieren
         ki_overview / ki_verlauf {filter,source,q,offset,limit} / ki_set_status {id,status} / ki_wissen / ki_wissen_lesen {name} /
         ki_wissen_speichern {name,text} / ki_config / ki_config_set {cooldown_seconds,daily_limit} – KI-Support (Bot ryvenlink-bot)
                     action: warn / kick / mute / ban / ipban / unmute / unban
"""
import base64, datetime, glob, gzip, hashlib, io, json, os, re, secrets, shutil, smtplib, sqlite3, ssl, subprocess, sys, tempfile, time, urllib.error, urllib.parse, urllib.request, zipfile
from email.message import EmailMessage

import yaml

VERSION = "1.5"
SERVERS = os.environ.get("RP_MOD_SERVERS", "/opt/crafty/docker/servers")
GATEWAY_CONF = "/etc/pxmx/gateway.json"
NAME_RX = re.compile(r"^\.?[A-Za-z0-9_]{1,17}$")
DUR_RX = re.compile(r"^(\d{1,5})(s|m|min|h|d|w|mo|y)$|^perm$", re.I)
ACTIONS = ("warn", "kick", "mute", "ban", "ipban", "unmute", "unban")
STATUSES = ("open", "claimed", "done", "rejected")


class Fail(Exception):
    pass


def proxy():
    """(crafty id, dir) of the Velocity proxy."""
    for f in glob.glob(SERVERS + "/*/velocity.toml"):
        d = os.path.dirname(f)
        return os.path.basename(d), d
    raise Fail("Velocity-Proxy nicht gefunden.")


def link_config():
    _, d = proxy()
    for f in (d + "/plugins/ryvenlink/config.yml", d + "/plugins/RyvenLink/config.yml"):
        if os.path.isfile(f):
            return yaml.safe_load(open(f, encoding="utf-8")) or {}
    raise Fail("RyvenLink-Config auf dem Proxy nicht gefunden.")


def db_creds():
    d = link_config().get("database") or {}
    if not d.get("password"):
        raise Fail("Keine Datenbank-Zugangsdaten in der RyvenLink-Config.")
    return d


def q(v):
    """SQL string literal."""
    if v is None:
        return "NULL"
    return "'" + str(v).replace("\\", "\\\\").replace("'", "''").replace("\x00", "") + "'"


def rows(query):
    """Runs a query whose single column is JSON_OBJECT(...); returns a list of dicts."""
    d = db_creds()
    host = str(d.get("host", "mariadb")).split(":")[0]
    r = subprocess.run(["docker", "exec", "-i", "-e", "MYSQL_PWD=" + str(d.get("password", "")), host, "sh", "-c",
                        'exec $(command -v mariadb || command -v mysql) -u"$0" -D "$1" -N -B --raw', str(d.get("user") or d.get("username") or "ryvendiscord"),
                        str(d.get("name") or d.get("database") or "ryvendiscord")], input=query.encode(), capture_output=True, timeout=60)
    if r.returncode != 0:
        raise Fail("Datenbank: " + r.stderr.decode(errors="replace").strip()[:300])
    out = []
    for ln in r.stdout.decode(errors="replace").splitlines():
        ln = ln.strip()
        if ln.startswith("{"):
            out.append(json.loads(ln))
    return out


def execute(query):
    d = db_creds()
    host = str(d.get("host", "mariadb")).split(":")[0]
    r = subprocess.run(["docker", "exec", "-i", "-e", "MYSQL_PWD=" + str(d.get("password", "")), host, "sh", "-c",
                        'exec $(command -v mariadb || command -v mysql) -u"$0" -D "$1" -N -B', str(d.get("user") or d.get("username") or "ryvendiscord"),
                        str(d.get("name") or d.get("database") or "ryvendiscord")], input=query.encode(), capture_output=True, timeout=60)
    if r.returncode != 0:
        raise Fail("Datenbank: " + r.stderr.decode(errors="replace").strip()[:300])


def limit(a, default=300, top=2000):
    try:
        return max(1, min(top, int(a.get("limit") or default)))
    except (TypeError, ValueError):
        return default


def like(s):
    return q("%" + str(s).replace("%", "").replace("_", "\\_") + "%")


PUN_COLS = ("JSON_OBJECT('id', id, 'type', type, 'uuid', uuid, 'name', name, 'ip_ban', ip_ban, 'reason', reason, 'template', template, "
            "'actor', actor_name, 'server', server, 'silent', silent, 'created', created_at, 'expires', expires_at, 'active', active, "
            "'removed_by', removed_by, 'removed_at', removed_at, 'removed_reason', removed_reason)")
REP_COLS = ("JSON_OBJECT('id', id, 'reporter', reporter_name, 'reporter_uuid', reporter_uuid, 'target', target_name, 'reason', reason, "
            "'server', server, 'created', created_at, 'status', status, 'handler', handler, 'has_chatlog', chatlog IS NOT NULL AND chatlog <> '')")


# ───────────────────────────── commands ─────────────────────────────
def overview(a):
    r = rows("SELECT JSON_OBJECT("
             "'reports_open', (SELECT COUNT(*) FROM rl_reports WHERE status='open'), "
             "'reports_claimed', (SELECT COUNT(*) FROM rl_reports WHERE status='claimed'), "
             "'bans', (SELECT COUNT(*) FROM rl_punishments WHERE type='ban' AND active=1), "
             "'mutes', (SELECT COUNT(*) FROM rl_punishments WHERE type='mute' AND active=1), "
             "'today', (SELECT COUNT(*) FROM rl_punishments WHERE created_at > NOW() - INTERVAL 1 DAY), "
             "'links', (SELECT COUNT(*) FROM rl_links), "
             "'links_today', (SELECT COUNT(*) FROM rl_links WHERE linked_at > NOW() - INTERVAL 1 DAY))")
    return r[0] if r else {}


def reports(a):
    st = str(a.get("status") or "open")
    where = []
    if st == "active":
        where.append("status IN ('open','claimed')")
    elif st in STATUSES:
        where.append("status=" + q(st))
    if a.get("q"):
        s = like(a["q"])
        where.append("(target_name LIKE %s OR reporter_name LIKE %s OR reason LIKE %s)" % (s, s, s))
    return {"reports": rows("SELECT %s FROM rl_reports %s ORDER BY id DESC LIMIT %d" % (REP_COLS, ("WHERE " + " AND ".join(where)) if where else "", limit(a)))}


def report(a):
    rid = int(a.get("id") or 0)
    r = rows("SELECT JSON_MERGE_PATCH(%s, JSON_OBJECT('chatlog', chatlog)) FROM rl_reports WHERE id=%d" % (REP_COLS, rid))
    if not r:
        raise Fail("Report #%d nicht gefunden." % rid)
    rep = r[0]
    rep["target_punishments"] = rows("SELECT %s FROM rl_punishments WHERE name=%s ORDER BY id DESC LIMIT 20" % (PUN_COLS, q(rep["target"])))
    rep["target_reports"] = rows("SELECT JSON_OBJECT('n', COUNT(*)) FROM rl_reports WHERE target_name=%s" % q(rep["target"]))[0]["n"]
    return rep


def report_set(a):
    rid = int(a.get("id") or 0)
    st = str(a.get("status") or "")
    if st not in ("claimed", "done", "rejected", "open"):
        raise Fail("Ungültiger Status.")
    actor = str(a.get("actor") or "Panel")[:64]
    cur = rows("SELECT %s FROM rl_reports WHERE id=%d" % (REP_COLS, rid))
    if not cur:
        raise Fail("Report #%d nicht gefunden." % rid)
    execute("UPDATE rl_reports SET status=%s, handler=%s WHERE id=%d" % (q(st), q(actor), rid))
    r = cur[0]
    if st in ("done", "rejected") and r["status"] not in ("done", "rejected"):
        txt = "wurde vom Team bearbeitet. Danke für deine Meldung! 💜" if st == "done" else "wurde geprüft und abgelehnt."
        msg = "<gray>Dein Report <white>#%d</white> gegen <white>%s</white> %s</gray>" % (rid, r["target"], txt)
        execute("INSERT INTO rl_notify (uuid, message) VALUES (%s, %s)" % (q(r["reporter_uuid"]), q(msg)))
    return {"id": rid, "status": st}


def punishments(a):
    t = str(a.get("type") or "all")
    where = []
    if t in ("ban", "mute", "kick", "warn"):
        where.append("type=" + q(t))
    if a.get("active"):
        where.append("active=1 AND type IN ('ban','mute')")
    if a.get("q"):
        s = like(a["q"])
        where.append("(name LIKE %s OR reason LIKE %s OR actor_name LIKE %s OR uuid=%s)" % (s, s, s, q(a["q"])))
    return {"punishments": rows("SELECT %s FROM rl_punishments %s ORDER BY id DESC LIMIT %d" % (PUN_COLS, ("WHERE " + " AND ".join(where)) if where else "", limit(a)))}


def links(a):
    where = ""
    if a.get("q"):
        s = like(a["q"])
        where = "WHERE l.mc_name LIKE %s OR CAST(l.discord_id AS CHAR) LIKE %s OR l.uuid=%s" % (s, s, q(a["q"]))
    r = rows("SELECT JSON_OBJECT('uuid', l.uuid, 'name', l.mc_name, 'discord_id', CAST(l.discord_id AS CHAR), 'linked', l.linked_at, "
             "'groups', l.groups_csv, 'synced', l.synced_csv, 'last_seen', p.last_seen, 'online', p.online, 'playtime', p.playtime_sec) "
             "FROM rl_links l LEFT JOIN rl_players p ON p.uuid = l.uuid %s ORDER BY l.linked_at DESC LIMIT %d" % (where, limit(a)))
    pend = rows("SELECT JSON_OBJECT('n', COUNT(*)) FROM rl_codes WHERE expires_at > NOW()")
    return {"links": r, "pending_codes": pend[0]["n"] if pend else 0}


def player(a):
    name = str(a.get("name") or "")
    if not NAME_RX.match(name):
        raise Fail("Ungültiger Spielername.")
    p = rows("SELECT JSON_OBJECT('uuid', uuid, 'name', name, 'first_join', first_join, 'last_seen', last_seen, 'playtime', playtime_sec, "
             "'online', online, 'server', server) FROM rl_players WHERE name=%s ORDER BY last_seen DESC LIMIT 1" % q(name))
    info = p[0] if p else {"name": name}
    cond = "uuid=%s" % q(info["uuid"]) if info.get("uuid") else "name=%s" % q(name)
    info["punishments"] = rows("SELECT %s FROM rl_punishments WHERE %s ORDER BY id DESC LIMIT 100" % (PUN_COLS, cond))
    lk = rows("SELECT JSON_OBJECT('discord_id', CAST(discord_id AS CHAR), 'linked', linked_at, 'groups', groups_csv) FROM rl_links WHERE %s" % cond)
    info["link"] = lk[0] if lk else None
    info["reports_against"] = rows("SELECT %s FROM rl_reports WHERE target_name=%s ORDER BY id DESC LIMIT 50" % (REP_COLS, q(info.get("name", name))))
    info["reports_by"] = rows("SELECT JSON_OBJECT('n', COUNT(*)) FROM rl_reports WHERE reporter_name=%s" % q(info.get("name", name)))[0]["n"]
    return info


def default_templates():
    """Templates from the config.yml inside RyvenLink.jar (used when the live config has none)."""
    import zipfile
    _, d = proxy()
    for jar in glob.glob(d + "/plugins/*.jar"):
        try:
            z = zipfile.ZipFile(jar)
            if "config.yml" in z.namelist() and "velocity-plugin.json" in z.namelist() and b"punish-templates" in z.read("config.yml"):
                return (yaml.safe_load(z.read("config.yml").decode("utf-8")) or {}).get("punish-templates") or {}
        except (OSError, zipfile.BadZipFile, yaml.YAMLError):
            pass
    return {}


def templates(a):
    out = []
    for k, v in sorted((link_config().get("punish-templates") or default_templates()).items()):
        v = v or {}
        out.append({"key": str(k), "type": str(v.get("type", "")), "reason": str(v.get("reason", "")), "ladder": [str(x) for x in (v.get("ladder") or [])]})
    return {"templates": out}


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


def act(a):
    action = str(a.get("action") or "")
    name = str(a.get("player") or "").strip()
    if action not in ACTIONS:
        raise Fail("Unbekannte Aktion.")
    if not NAME_RX.match(name):
        raise Fail("Ungültiger Spielername.")
    dur = str(a.get("duration") or "").strip().lower()
    if dur and not DUR_RX.match(dur):
        raise Fail("Ungültige Dauer – z. B. 30m, 6h, 7d, 30d oder perm.")
    reason = re.sub(r"[\r\n\t]+", " ", str(a.get("reason") or "")).strip()[:200]
    if action in ("warn", "kick", "mute", "ban", "ipban") and not reason:
        raise Fail("Bitte einen Grund oder eine Vorlage angeben.")
    actor = re.sub(r"[\r\n\t]+", " ", str(a.get("actor") or "Panel"))[:60]
    payload = {"action": action, "player": name, "actor": actor, "reason": reason}
    if dur and dur != "perm" and action in ("mute", "ban", "ipban"):
        payload["duration"] = dur
    if a.get("silent"):
        payload["silent"] = True
    sid, _ = proxy()
    before = rows("SELECT JSON_OBJECT('m', COALESCE(MAX(id), 0)) FROM rl_punishments")[0]["m"]
    crafty_stdin(sid, "ryvenbans exec " + base64.b64encode(json.dumps(payload).encode()).decode())
    # wait for the proxy to write the result
    for _ in range(16):
        time.sleep(0.5)
        if action in ("unban", "unmute"):
            t = action[2:]
            left = rows("SELECT JSON_OBJECT('n', COUNT(*)) FROM rl_punishments WHERE name=%s AND type=%s AND active=1" % (q(name), q(t)))[0]["n"]
            if left == 0:
                return {"done": True, "message": "%s wurde %s." % (name, "entbannt" if t == "ban" else "entmutet")}
        else:
            new = rows("SELECT %s FROM rl_punishments WHERE id > %d AND name=%s ORDER BY id DESC LIMIT 1" % (PUN_COLS, int(before), q(name)))
            if new:
                return {"done": True, "punishment": new[0]}
    if action == "kick":
        return {"done": False, "message": "Kein Eintrag – ist %s gerade online?" % name}
    return {"done": False, "message": "Befehl gesendet, aber keine Bestätigung – Spieler unbekannt, geschützt oder nicht bestraft. Siehe Proxy-Konsole."}


# ───────────────────────────── Datenschutz-Wächter: ausgehende Verbindungen ─────────────────────────────
OUT_COLS = ("JSON_OBJECT('ip', ip, 'port', port, 'server', server, 'dienst', dienst, 'zertifikat', zertifikat, 'rdns', rdns, "
            "'bekannt', bekannt, 'zuerst', zuerst, 'zuletzt', zuletzt, 'anzahl', anzahl, 'freigabe', freigabe, 'grund', grund)")
IP_RX = re.compile(r"^[0-9a-fA-F:.]{3,45}$")


def _crafty_names():
    """server-id-Präfix (8 Zeichen) → Servername aus Crafty."""
    try:
        tok = json.load(open(GATEWAY_CONF, encoding="utf-8")).get("crafty_token")
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        req = urllib.request.Request("https://127.0.0.1:8443/api/v2/servers", headers={"Authorization": "Bearer " + tok})
        with urllib.request.urlopen(req, context=ctx, timeout=10) as r:
            return {s["server_id"][:8]: s.get("server_name") or s["server_id"][:8] for s in json.load(r).get("data", [])}
    except Exception:
        return {}


def _hex_ip(h):
    import ipaddress
    if len(h) == 8:
        return str(ipaddress.IPv4Address(bytes.fromhex(h)[::-1]))
    b = b"".join(bytes.fromhex(h[i:i + 8])[::-1] for i in range(0, 32, 8))
    a = ipaddress.IPv6Address(b)
    return str(a.ipv4_mapped) if a.ipv4_mapped else str(a)


def _privat(ip):
    import ipaddress
    try:
        a = ipaddress.ip_address(ip)
        return a.is_private or a.is_loopback or a.is_link_local
    except ValueError:
        return True


def live_connections():
    """Aktuell offene ausgehende Verbindungen aller Server im Crafty-Container (liest /proc des Containers)."""
    r = subprocess.run(["docker", "inspect", "-f", "{{.State.Pid}}", "crafty_container"], capture_output=True, timeout=10)
    pid = r.stdout.decode().strip()
    if not pid.isdigit() or pid == "0":
        raise Fail("Crafty-Container läuft nicht.")
    netns = os.readlink("/proc/%s/ns/net" % pid)
    # Socket-Inode → Server (über alle Prozesse im selben Netz-Namensraum)
    inode_server = {}
    for p in os.listdir("/proc"):
        if not p.isdigit():
            continue
        try:
            if os.readlink("/proc/%s/ns/net" % p) != netns:
                continue
            cwd = os.readlink("/proc/%s/cwd" % p)
            m = re.search(r"/servers/([0-9a-f]{8})", cwd)
            srv = m.group(1) if m else ("Crafty" if cwd.startswith("/crafty") else os.path.basename(cwd) or "?")
            for fd in os.listdir("/proc/%s/fd" % p):
                try:
                    l = os.readlink("/proc/%s/fd/%s" % (p, fd))
                    if l.startswith("socket:["):
                        inode_server[l[8:-1]] = srv
                except OSError:
                    pass
        except OSError:
            continue
    out, seen = [], set()
    for f in ("tcp", "tcp6"):
        try:
            lines = open("/proc/%s/net/%s" % (pid, f)).read().splitlines()[1:]
        except OSError:
            continue
        for ln in lines:
            t = ln.split()
            if len(t) < 10 or t[3] != "01":
                continue
            lport = int(t[1].split(":")[1], 16)
            if lport < 32768:      # eingehend (Spieler → Server)
                continue
            rh, rp = t[2].split(":")
            ip, port = _hex_ip(rh), int(rp, 16)
            if _privat(ip):
                continue
            srv = inode_server.get(t[9], "?")
            key = (ip, port, srv)
            if key in seen:
                for o in out:
                    if (o["ip"], o["port"], o["server"]) == key:
                        o["verbindungen"] += 1
                continue
            seen.add(key)
            out.append({"ip": ip, "port": port, "server": srv, "verbindungen": 1})
    return out


def verbindungen(a):
    _outbound_spalten()
    names = _crafty_names()
    sni = _sni_map()
    try:
        hist = rows("SELECT %s FROM rl_outbound ORDER BY bekannt, zuletzt DESC LIMIT 1000" % OUT_COLS)
    except Fail:
        hist = []
    by_ip = {}
    for h in hist:
        h["server_name"] = names.get(h["server"], h["server"])
        by_ip.setdefault(h["ip"], h)
    live = live_alle()
    for c in live:
        h = by_ip.get(c["ip"])
        c["server_name"] = names.get(c["server"], c["server"])
        c["dienst"] = h.get("dienst") if h else None
        c["bekannt"] = bool(h and h.get("bekannt"))
        c["neu"] = h is None
        c["host"] = sni.get(c["ip"])
        c["freigabe"], c["grund"] = (h.get("freigabe"), h.get("grund")) if h else (None, None)
        info = _host_info(c["host"])
        if info and not c["freigabe"]:
            c["freigabe"], c["grund"] = _freigabe(info)
        if info and not c["bekannt"]:
            c["bekannt"], c["dienst"] = True, "%s (%s)" % (info[1], c["host"])
        elif not c["dienst"] and c["host"]:
            c["dienst"] = c["host"]
    live.sort(key=lambda c: (c["bekannt"], c["server_name"], c["ip"]))
    return {"live": live, "verlauf": hist, "zeit": int(time.time()),
            "unbekannt": sum(1 for h in hist if not h.get("bekannt")), "bekannt": sum(1 for h in hist if h.get("bekannt"))}


def verbindung_ok(a):
    ip = str(a.get("ip") or "")
    if not IP_RX.match(ip):
        raise Fail("Ungültige IP.")
    dienst = re.sub(r"[\r\n\t]+", " ", str(a.get("dienst") or "")).strip()[:120]
    if not dienst:
        raise Fail("Bitte angeben, welcher Dienst das ist.")
    who = re.sub(r"[\r\n\t]+", " ", str(a.get("actor") or "Panel"))[:60]
    grund = re.sub(r"[\r\n\t]+", " ", str(a.get("grund") or "")).strip()[:200] or "Vom Team geprüft"
    freigabe = re.sub(r"[\r\n\t]+", " ", str(a.get("freigabe") or "")).strip()[:100] or "Manuell freigegeben"
    freigabe = "%s (freigegeben: %s, %s)" % (freigabe, who[:30], time.strftime("%d.%m.%Y"))   # Freigeber + Datum immer serverseitig
    _outbound_spalten()
    execute("UPDATE rl_outbound SET bekannt=1, gemeldet=1, dienst=%s, freigabe=%s, grund=%s WHERE ip=%s"
            % (q(dienst + " (geprüft: " + who + ")"), q(freigabe), q(grund), q(ip)))
    return {"ip": ip, "dienst": dienst, "freigabe": freigabe, "grund": grund}



# ───────────────────────────── Nutzer & Ränge (LuckPerms, Money, Coins) ─────────────────────────────
UUID_RX = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
GROUP_RX = re.compile(r"^[a-z0-9_\-]{1,36}$")
PERM_RX = re.compile(r"^-?[A-Za-z0-9_.\-*:]{1,200}$")
LPDUR_RX = re.compile(r"^(\d{1,5}(mo|[smhdwy]))+$")
CTX_RX = re.compile(r"^[A-Za-z0-9_\-]{1,40}$")
PLAYER_RX = re.compile(r"^\.?[A-Za-z0-9_]{1,17}$")


def _yaml(f):
    try:
        return yaml.safe_load(open(f, encoding="utf-8")) or {}
    except (OSError, yaml.YAMLError):
        return {}


def _crafty(path, method="GET", body=None):
    tok = json.load(open(GATEWAY_CONF, encoding="utf-8")).get("crafty_token")
    req = urllib.request.Request("https://127.0.0.1:8443/api/v2" + path, data=body.encode() if body is not None else None, method=method,
                                 headers={"Authorization": "Bearer " + tok, "Content-Type": "text/plain"})
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    with urllib.request.urlopen(req, context=ctx, timeout=15) as r:
        return json.loads(r.read().decode() or "{}")


def nutzer_server():
    """Paper-Server mit LuckPerms: [{id, name, dir, running}] – nach Spielerzahl sortiert."""
    names = _crafty_names()
    out = []
    for d in sorted(glob.glob(SERVERS + "/*/plugins/LuckPerms")):
        sd = d.rsplit("/plugins/", 1)[0]
        if os.path.exists(sd + "/velocity.toml"):
            continue
        sid = os.path.basename(sd)
        try:
            running = bool(_crafty("/servers/%s/stats" % sid).get("data", {}).get("running"))
        except Exception:
            running = False
        n = len(glob.glob(sd + "/plugins/Essentials/userdata/*.yml"))
        out.append({"id": sid, "name": names.get(sid[:8], sid[:8]), "dir": sd, "running": running, "spieler": n})
    out.sort(key=lambda s: -s["spieler"])
    return out


def _server(a):
    ss = nutzer_server()
    if not ss:
        raise Fail("Kein Minecraft-Server mit LuckPerms gefunden.")
    want = str(a.get("server") or "")
    return next((s for s in ss if s["id"] == want or s["name"] == want), ss[0]), ss


def _dbq(cfg, query):
    """cfg = (host, user, password, database). Gibt Zeilen als Listen zurück (None für NULL)."""
    host, user, pw, db = cfg
    r = subprocess.run(["docker", "exec", "-i", "-e", "MYSQL_PWD=" + str(pw), host, "sh", "-c",
                        'exec $(command -v mariadb || command -v mysql) -u"$0" -D "$1" -N -B --raw', str(user), str(db)],
                       input=query.encode(), capture_output=True, timeout=60)
    if r.returncode != 0:
        raise Fail("Datenbank: " + r.stderr.decode(errors="replace").strip()[:300])
    return [[None if c == "NULL" else c for c in ln.split("\t")] for ln in r.stdout.decode(errors="replace").splitlines()]


def _lp(srv):
    d = (_yaml(srv["dir"] + "/plugins/LuckPerms/config.yml").get("data") or {})
    host = str(d.get("address", "mariadb")).split(":")[0]
    return (host, d.get("username"), d.get("password"), d.get("database")), str(d.get("table-prefix") or "luckperms_")


def _core_coins(srv):
    """Coins aus dem CityBuildCore-Modul (ersetzt PlayerPoints): uuid → Coins, oder None wenn es das Modul nicht gibt."""
    f = srv["dir"] + "/plugins/CityBuildCore/coins/konten.yml"
    if not os.path.isfile(f):
        return None
    k = (_yaml(f).get("konten") or {})
    return {u: int((v or {}).get("coins") or 0) for u, v in k.items()}


def _core_coins_log(srv, uuid=None, limit=300):
    """Buchungen aus coins/verlauf.log (neueste zuerst): zeit, wer, uuid, name, art, menge, neu, grund."""
    f = srv["dir"] + "/plugins/CityBuildCore/coins/verlauf.log"
    out = []
    try:
        zeilen = open(f, encoding="utf-8", errors="replace").read().splitlines()
    except OSError:
        return out
    for ln in reversed(zeilen):
        t = ln.split("\t")
        if len(t) < 7 or (uuid and t[2] != uuid):
            continue
        try:
            ts = int(time.mktime(time.strptime(t[0], "%Y-%m-%d %H:%M:%S")))
        except ValueError:
            continue
        out.append({"time": ts, "actor": t[1], "target": t[3], "text": "%s %s – %s (jetzt %s)" % (t[4], t[5], t[7] if len(t) > 7 else "", t[6]), "art": "Coins"})
        if len(out) >= limit:
            break
    return out


def _pp(srv):
    m = (_yaml(srv["dir"] + "/plugins/PlayerPoints/config.yml").get("mysql-settings") or {})
    if not m.get("user-name"):
        return None
    return (str(m.get("hostname", "mariadb")), m.get("user-name"), m.get("user-password"), m.get("database-name"))


def _money(srv):
    """uuid → Kontostand (Essentials) auf dem gewählten Server."""
    out = {}
    for f in glob.glob(srv["dir"] + "/plugins/Essentials/userdata/*.yml"):
        try:
            t = open(f, encoding="utf-8", errors="replace").read()
        except OSError:
            continue
        m = re.search(r"(?m)^money:\s*'?(-?[0-9.Ee+]+)'?\s*$", t)
        if m:
            try:
                out[os.path.basename(f)[:-4]] = float(m.group(1))
            except ValueError:
                pass
    return out


def _gruppen(cfg, pre):
    gs = {}
    for r in _dbq(cfg, "select 'R', name, '' from {p}groups;\n"
                       "select 'W', name, permission from {p}group_permissions where (permission like 'weight.%' or permission like 'prefix.%' "
                       "or permission like 'displayname.%' or permission like 'group.%') and value=1;\n"
                       "select 'C', name, count(*) from {p}group_permissions group by name;".replace("{p}", pre)):
        k, name, v = (r + [None, None, None])[:3]
        g = gs.setdefault(name, {"name": name, "weight": 0, "prefix": "", "display": "", "parents": [], "perms": 0, "members": 0})
        if k == "C":
            g["perms"] = int(v or 0)
        elif k == "W" and v:
            if v.startswith("weight."):
                try:
                    g["weight"] = int(v[7:])
                except ValueError:
                    pass
            elif v.startswith("prefix."):
                g["prefix"] = v.split(".", 2)[2] if v.count(".") >= 2 else ""
            elif v.startswith("displayname."):
                g["display"] = v[12:]
            elif v.startswith("group."):
                g["parents"].append(v[6:])
    return gs


def nutzer(a):
    """Spielerliste: LuckPerms-Spieler mit Rängen, Money (gewählter Server), Coins (PlayerPoints)."""
    srv, ss = _server(a)
    cfg, pre = _lp(srv)
    now = int(time.time())
    players = {}
    for r in _dbq(cfg, ("select 'P', uuid, username, primary_group, '', '' from {p}players;\n"
                        "select 'G', uuid, substring(permission,7), expiry, server, world from {p}user_permissions "
                        "where permission like 'group.%' and value=1;").replace("{p}", pre)):
        k, u, x, y, z, w = (r + [None] * 6)[:6]
        if not u or not UUID_RX.match(u):
            continue
        p = players.setdefault(u, {"uuid": u, "name": u[:8], "primary": "default", "groups": []})
        if k == "P":
            p["name"], p["primary"] = x or u[:8], y or "default"
        elif int(y or 0) == 0 or int(y or 0) > now:
            p["groups"].append({"group": x, "expiry": int(y or 0), "server": z or "global", "world": w or "global"})
    money = _money(srv)
    coins = _core_coins(srv) or {}
    pp = None if coins or _core_coins(srv) is not None else _pp(srv)
    if pp:
        try:
            for r in _dbq(pp, "select uuid, points from playerpoints_points;"):
                coins[r[0]] = int(r[1] or 0)
        except Fail:
            pass
    gs = _gruppen(cfg, pre)
    lst = []
    for p in players.values():
        p["money"] = money.get(p["uuid"])
        p["coins"] = coins.get(p["uuid"])
        top = max([g["group"] for g in p["groups"]] + [p["primary"]], key=lambda g: (gs.get(g, {}).get("weight", 0), g == p["primary"]))
        p["rank"] = top
        p["weight"] = gs.get(top, {}).get("weight", 0)
        lst.append(p)
    lst.sort(key=lambda p: (-p["weight"], p["name"].lower()))
    return {"server": {"id": srv["id"], "name": srv["name"], "running": srv["running"]},
            "servers": [{"id": s["id"], "name": s["name"], "running": s["running"]} for s in ss],
            "players": lst, "groups": sorted(gs.values(), key=lambda g: (-g["weight"], g["name"])),
            "coins": pp is not None or _core_coins(srv) is not None}


def nutzer_detail(a):
    srv, _ = _server(a)
    u = str(a.get("uuid") or "")
    if not UUID_RX.match(u):
        raise Fail("Ungültige UUID.")
    cfg, pre = _lp(srv)
    now = int(time.time())
    perms, groups, log = [], [], []
    for r in _dbq(cfg, ("select 'X', permission, value, server, world, expiry from {p}user_permissions where uuid='%s';\n"
                        "select 'A', time, actor_name, concat(type, ' ', action), '', '' from {p}actions where acted_uuid='%s' order by id desc limit 80;")
                  .replace("{p}", pre) % (u, u)):
        k = r[0]
        if k == "X":
            exp = int(r[5] or 0)
            if exp and exp < now:
                continue
            row = {"perm": r[1], "value": r[2] == "1", "server": r[3] or "global", "world": r[4] or "global", "expiry": exp}
            if r[1].startswith("group.") and r[2] == "1":
                groups.append({"group": r[1][6:], "server": row["server"], "world": row["world"], "expiry": exp})
            else:
                perms.append(row)
        elif k == "A":
            log.append({"time": int(r[1] or 0), "actor": r[2], "text": r[3], "art": "LuckPerms"})
    pp = _pp(srv)
    if pp:
        try:
            for r in _dbq(pp, "select unix_timestamp(timestamp), coalesce((select username from playerpoints_username_cache c where c.uuid=t.source), t.source, 'Konsole'), "
                              "concat(transaction_type, ' ', amount, ' – ', coalesce(description,'')) from playerpoints_transaction_log t where receiver='%s' "
                              "order by timestamp desc limit 60;" % u):
                log.append({"time": int(float(r[0] or 0)), "actor": r[1], "text": r[2], "art": "Coins"})
        except Fail:
            pass
    log += [{k: v for k, v in x.items() if k != "target"} for x in _core_coins_log(srv, u, 60)]
    log.sort(key=lambda x: -x["time"])
    return {"uuid": u, "perms": sorted(perms, key=lambda p: p["perm"]), "groups": groups, "log": log[:120]}


def rang_detail(a):
    srv, _ = _server(a)
    g = str(a.get("group") or "")
    if not GROUP_RX.match(g):
        raise Fail("Ungültiger Rang.")
    cfg, pre = _lp(srv)
    now = int(time.time())
    perms = []
    for r in _dbq(cfg, ("select permission, value, server, world, expiry from {p}group_permissions where name='%s' order by permission;").replace("{p}", pre) % g):
        exp = int(r[4] or 0)
        if exp and exp < now:
            continue
        perms.append({"perm": r[0], "value": r[1] == "1", "server": r[2] or "global", "world": r[3] or "global", "expiry": exp})
    members = _dbq(cfg, ("select count(distinct uuid) from {p}user_permissions where permission='group.%s' and value=1;").replace("{p}", pre) % g)
    return {"group": g, "perms": perms, "members": int(members[0][0]) if members else 0}


def nutzer_log(a):
    srv, _ = _server(a)
    cfg, pre = _lp(srv)
    out = [{"time": int(r[0] or 0), "actor": r[1], "target": r[2], "text": r[3], "art": "LuckPerms"}
           for r in _dbq(cfg, ("select time, actor_name, acted_name, concat(type, ' ', action) from {p}actions order by id desc limit 300;").replace("{p}", pre))]
    pp = _pp(srv)
    if pp:
        try:
            for r in _dbq(pp, "select unix_timestamp(timestamp), coalesce((select username from playerpoints_username_cache c where c.uuid=t.source), t.source, 'Konsole'), "
                              "coalesce((select username from playerpoints_username_cache c where c.uuid=t.receiver), t.receiver), "
                              "concat(transaction_type, ' ', amount, ' – ', coalesce(description,'')) from playerpoints_transaction_log t order by timestamp desc limit 300;"):
                out.append({"time": int(float(r[0] or 0)), "actor": r[1], "target": r[2], "text": r[3], "art": "Coins"})
        except Fail:
            pass
    out += _core_coins_log(srv)
    out.sort(key=lambda x: -x["time"])
    return {"log": out[:400]}


def _ctx(server, world):
    s = ""
    for k, v in (("server", server), ("world", world)):
        v = str(v or "").strip()
        if v and v != "global":
            if not CTX_RX.match(v):
                raise Fail("Ungültiger Kontext: " + v)
            s += " %s=%s" % (k, v)
    return s


def _ausfuehren(srv, cmds):
    """Befehle in die Konsole des Servers schreiben und die neuen Konsolenzeilen zurückgeben."""
    if not srv["running"]:
        raise Fail("„%s“ läuft gerade nicht – Änderungen gehen nur bei laufendem Server." % srv["name"])

    def logs():
        try:
            return [re.sub(r"\x1b\[[0-9;]*m", "", str(x)).rstrip() for x in _crafty("/servers/%s/logs?raw=true" % srv["id"]).get("data", [])]
        except Exception:
            return []
    vorher = logs()
    for c in cmds:
        _crafty("/servers/%s/stdin" % srv["id"], "POST", c)
    time.sleep(1.4)
    nachher = logs()
    neu = nachher
    for i in range(len(nachher), 0, -1):          # längste Überlappung vom Ende von „vorher“ finden
        if vorher and nachher[:i] == vorher[-i:]:
            neu = nachher[i:]
            break
    zeilen = [re.sub(r"^\[[\d:]+ [A-Z]+\]:?\s*", "", z) for z in neu if z.strip()][:14]
    warn = any(re.search(r"(?i)unknown|not found|invalid|error|fehler|nicht gefunden|usage|benutzung|does not exist|could not", z) for z in zeilen)
    return {"befehle": cmds, "ausgabe": zeilen, "warnung": warn, "server": srv["name"]}


def nutzer_act(a):
    """Änderungen per Konsolenbefehl: Rang/Gruppe, Permission, Money, Coins (Spieler) sowie Rang-Verwaltung."""
    srv, _ = _server(a)
    op = str(a.get("op") or "")
    u = str(a.get("uuid") or "")
    name = str(a.get("name") or "")
    g = str(a.get("group") or "").lower()
    perm = str(a.get("perm") or "").strip()
    dur = str(a.get("dur") or "").strip().lower()
    ctx = _ctx(a.get("server_ctx"), a.get("world_ctx"))
    val = "false" if a.get("value") in (False, "false") else "true"
    exp = int(a.get("expiry") or 0)

    def need(cond, msg):
        if not cond:
            raise Fail(msg)
    if op.startswith("user_"):
        need(UUID_RX.match(u), "Ungültige UUID.")
    if op in ("user_set", "user_add", "user_addtemp", "user_remove", "grp_delete", "grp_create", "grp_weight", "grp_prefix", "grp_perm_set",
              "grp_perm_unset", "grp_parent_add", "grp_parent_remove"):
        need(GROUP_RX.match(g), "Ungültiger Rang.")
    if op in ("user_addtemp", "user_perm_settemp"):
        need(LPDUR_RX.match(dur), "Ungültige Dauer (z. B. 30d, 12h, 1mo, 1w2d).")
    if "perm" in op:
        need(PERM_RX.match(perm), "Ungültige Permission (keine Leerzeichen).")

    if op == "user_set":
        cmds = ["lp user %s parent set %s" % (u, g)]
    elif op == "user_add":
        cmds = ["lp user %s parent add %s%s" % (u, g, ctx)]
    elif op == "user_addtemp":
        cmds = ["lp user %s parent addtemp %s %s%s" % (u, g, dur, ctx)]
    elif op == "user_remove":
        cmds = ["lp user %s parent %s %s%s" % (u, "removetemp" if exp else "remove", g, ctx)]
    elif op == "user_perm_set":
        cmds = ["lp user %s permission set %s %s%s" % (u, perm, val, ctx)]
    elif op == "user_perm_settemp":
        cmds = ["lp user %s permission settemp %s %s %s%s" % (u, perm, val, dur, ctx)]
    elif op == "user_perm_unset":
        cmds = ["lp user %s permission %s %s%s" % (u, "unsettemp" if exp else "unset", perm, ctx)]
    elif op in ("user_money", "user_coins"):
        need(PLAYER_RX.match(name), "Ungültiger Spielername.")
        art = str(a.get("art") or "")
        need(art in ("set", "give", "take"), "Ungültige Aktion.")
        try:
            betrag = float(str(a.get("amount")).replace(",", "."))
        except ValueError:
            raise Fail("Ungültiger Betrag.")
        need(0 <= betrag <= 1e12, "Ungültiger Betrag.")
        if op == "user_coins":
            cmds = ["points %s %s %d" % (art, name, round(betrag))]
        else:
            cmds = ["eco %s %s %s" % (art, name, ("%.2f" % betrag).rstrip("0").rstrip("."))]
    elif op == "grp_create":
        cmds = ["lp creategroup " + g]
    elif op == "grp_delete":
        need(g not in ("default",), "Der Rang default kann nicht gelöscht werden.")
        cmds = ["lp deletegroup " + g]
    elif op == "grp_weight":
        try:
            w = int(a.get("weight"))
        except (TypeError, ValueError):
            raise Fail("Gewicht muss eine Zahl sein.")
        need(0 <= w <= 100000, "Gewicht 0–100000.")
        cmds = ["lp group %s setweight %d" % (g, w)]
    elif op == "grp_prefix":
        text = re.sub(r"[\"\r\n\t\\]", "", str(a.get("prefix") or ""))[:64]
        try:
            prio = int(a.get("prio") or 100)
        except ValueError:
            prio = 100
        cmds = ["lp group %s meta removeprefix %d" % (g, prio)] + (["lp group %s meta setprefix %d \"%s\"" % (g, prio, text)] if text else [])
    elif op == "grp_perm_set":
        cmds = ["lp group %s permission set %s %s%s" % (g, perm, val, ctx)]
    elif op == "grp_perm_unset":
        cmds = ["lp group %s permission %s %s%s" % (g, "unsettemp" if exp else "unset", perm, ctx)]
    elif op in ("grp_parent_add", "grp_parent_remove"):
        par = str(a.get("parent") or "").lower()
        need(GROUP_RX.match(par), "Ungültiger Eltern-Rang.")
        cmds = ["lp group %s parent %s %s" % (g, "add" if op == "grp_parent_add" else "remove", par)]
    else:
        raise Fail("Unbekannte Aktion.")
    return _ausfuehren(srv, cmds)




# ───────────────────────────── Datenschutz & Compliance ─────────────────────────────
COMPLIANCE_FILE = "/var/lib/pxmx/compliance.json"
PLUGIN_CACHE = "/var/lib/pxmx/plugin-cache.json"
KATEGORIEN = ["Spielername & UUID", "IP-Adresse", "Chatnachrichten", "Befehle", "Standort & Weltaktionen", "Spielstand & Inventar",
              "Wirtschaft & Transaktionen", "Rechte & Ränge", "Strafen & Reports", "Discord-Konto", "Client & Gerät", "Statistik & Telemetrie"]
# Vorschläge für bekannte Plugins (Teilstring des Namens, klein) → Kategorien, Zweck
BEKANNT = [
    ("luckperms", ["Spielername & UUID", "Rechte & Ränge"], "Rechte- und Rangverwaltung"),
    ("essentials", ["Spielername & UUID", "IP-Adresse", "Standort & Weltaktionen", "Wirtschaft & Transaktionen", "Spielstand & Inventar"], "Grundfunktionen (Homes, Teleport, Geld)"),
    ("coreprotect", ["Spielername & UUID", "Chatnachrichten", "Befehle", "Standort & Weltaktionen"], "Grief-Schutz / Rückverfolgung"),
    ("ryvenlink", ["Spielername & UUID", "IP-Adresse", "Chatnachrichten", "Strafen & Reports", "Discord-Konto"], "Netzwerk, Strafen, Reports, Discord-Verknüpfung"),
    ("ryvenbans", ["Spielername & UUID", "IP-Adresse", "Strafen & Reports", "Chatnachrichten"], "Strafen, Datenschutz-Zustimmung, Verbindungs-Wächter"),
    ("ryveneco", ["Spielername & UUID", "Wirtschaft & Transaktionen"], "Geldbuchungen protokollieren, Verkaufssteuer"),
    ("citybuildcore", ["Spielername & UUID", "Wirtschaft & Transaktionen", "Standort & Weltaktionen", "Spielstand & Inventar"], "CityBuild-Funktionen"),
    ("geyser", ["Spielername & UUID", "Client & Gerät"], "Bedrock-Spieler verbinden"),
    ("floodgate", ["Spielername & UUID", "Client & Gerät"], "Bedrock-Konten zuordnen"),
    ("playerpoints", ["Spielername & UUID", "Wirtschaft & Transaktionen"], "Coins"),
    ("economyshopgui", ["Spielername & UUID", "Wirtschaft & Transaktionen"], "Server-Shop"),
    ("excellentcrates", ["Spielername & UUID", "Spielstand & Inventar"], "Kisten / Belohnungen"),
    ("skinsrestorer", ["Spielername & UUID"], "Skins"),
    ("ajleaderboard", ["Spielername & UUID", "Statistik & Telemetrie"], "Bestenlisten"),
    ("dailyreward", ["Spielername & UUID"], "Tägliche Belohnungen"),
    ("worldguard", ["Standort & Weltaktionen"], "Regionsschutz"),
    ("worldedit", ["Standort & Weltaktionen"], "Bauwerkzeug"),
    ("vulcan", ["Spielername & UUID", "Client & Gerät"], "Anti-Cheat"),
    ("grim", ["Spielername & UUID", "Client & Gerät"], "Anti-Cheat"),
    ("tab", ["Spielername & UUID", "Rechte & Ränge"], "Tabliste"),
    ("deluxemenus", ["Spielername & UUID"], "Menüs"),
    ("placeholderapi", ["Spielername & UUID"], "Platzhalter"),
    ("viaversion", ["Client & Gerät"], "Ältere/neuere Clients"),
    ("protocollib", [], "Technische Bibliothek"),
    ("vault", [], "Technische Schnittstelle"),
    ("crazyauctions", ["Spielername & UUID", "Wirtschaft & Transaktionen", "Spielstand & Inventar"], "Auktionshaus"),
    ("jobs", ["Spielername & UUID", "Wirtschaft & Transaktionen"], "Jobs"),
    ("discordsrv", ["Spielername & UUID", "Chatnachrichten", "Discord-Konto"], "Discord-Anbindung"),
    ("plan", ["Spielername & UUID", "IP-Adresse", "Statistik & Telemetrie"], "Spieler-Analyse"),
    ("maintenance", [], "Wartungsmodus"),
]
TELEMETRIE = {"org/bstats/": "bStats (bstats.org)", "dev/faststats/": "FastStats", "com/artillexstudios/axapi/metrics": "Artillex-Statistik",
              "de/oliver/fancyanalytics": "FancyAnalytics", "net/pistonmaster/pistonutils/": "pStats", "/updatechecker": "Update-Prüfung",
              "/UpdateChecker": "Update-Prüfung", "/updater/": "Update-Prüfung"}
DOKU_RX = re.compile(r"^(wiki|docs?|javadocs?|help|support|forum|blog|ci|dev)\.|(\.|^)(javadoc\.io|fandom\.com|medium\.com|tiny\.cc|bit\.ly|readthedocs\.io|"
                     r"gitbook\.io|digminecraft\.com|appspot\.com|regular-expressions\.info|mysql\.com|mongodb\.com|wiki\.vg|beispiel\.com|contoh\.com|"
                     r"esempio\.com|birdflop\.com|dsc\.gg|t\.me|boosty\.to|wixsite\.com|intellectualsites\.github\.io)$", re.I)
CONF_EXT = (".yml", ".yaml", ".json", ".toml", ".properties", ".conf", ".hocon")
URL_RX = re.compile(r"https?://([A-Za-z0-9.\-]+\.[A-Za-z]{2,})")
URL_IGNORE = re.compile(r"(^|\.)(github\.com|spigotmc\.org|minecraft\.wiki|minecraft\.net|wiki\.|docs\.|youtube\.com|example\.com|paper(mc)?\.io|"
                        r"modrinth\.com|hangar\.|discord\.gg|discord\.com/invite|w3\.org|apache\.org|yaml\.org|luckperms\.net|essentialsx\.net|"
                        r"geysermc\.org|bstats\.org|ryvenmc\.de|store\.ryvenmc\.de|mojang\.com|minecraft-heads\.com|crowdin\.com|patreon\.com|"
                        r"buymeacoffee\.com|paypal\.me|ko-fi\.com|polymart\.org|builtbybit\.com|mc-market\.org|songoda\.com|gitlab\.com|jitpack\.io|"
                        r"textures\.minecraft\.net|imgur\.com|placeholderapi\.com|helpch\.at|wikipedia\.org|regex101\.com|docs\.oracle\.com)$", re.I)


def _json_load(f, default):
    try:
        return json.load(open(f, encoding="utf-8"))
    except (OSError, ValueError):
        return default


def _json_save(f, data):
    tmp = f + ".tmp"
    with open(tmp, "w", encoding="utf-8") as h:
        json.dump(data, h, ensure_ascii=False, indent=1)
    os.chmod(tmp, 0o600)
    os.replace(tmp, f)


def _container_namen():
    r = subprocess.run(["docker", "ps", "--format", "{{.Names}}"], capture_output=True, text=True, timeout=15)
    return [n for n in r.stdout.split() if n]


def _netz_lesen(pid, wer, out, seen):
    """Offene ausgehende TCP-Verbindungen eines Netz-Namensraums. wer(inode) → Name."""
    for f in ("tcp", "tcp6"):
        try:
            lines = open("/proc/%s/net/%s" % (pid, f)).read().splitlines()[1:]
        except OSError:
            continue
        for ln in lines:
            t = ln.split()
            if len(t) < 10 or t[3] != "01":
                continue
            if int(t[1].split(":")[1], 16) < 32768:      # eingehend
                continue
            rh, rp = t[2].split(":")
            ip, port = _hex_ip(rh), int(rp, 16)
            if _privat(ip):
                continue
            srv = wer(t[9])
            key = (ip, port, srv)
            if key in seen:
                seen[key]["verbindungen"] += 1
                continue
            seen[key] = {"ip": ip, "port": port, "server": srv, "verbindungen": 1}
            out.append(seen[key])


def live_alle():
    """Wie live_connections(), aber für jeden laufenden Container (Minecraft-Server einzeln, sonst der Container-Name)."""
    out, seen, netns_done = [], {}, set()
    try:
        for c in live_connections():
            c["container"] = "crafty_container"
            out.append(c)
            seen[(c["ip"], c["port"], c["server"])] = c
        netns_done.add(os.readlink("/proc/%s/ns/net" % subprocess.run(["docker", "inspect", "-f", "{{.State.Pid}}", "crafty_container"],
                                                                     capture_output=True, text=True, timeout=10).stdout.strip()))
    except Exception:
        pass
    for name in _container_namen():
        if name == "crafty_container":
            continue
        pid = subprocess.run(["docker", "inspect", "-f", "{{.State.Pid}}", name], capture_output=True, text=True, timeout=10).stdout.strip()
        if not pid.isdigit() or pid == "0":
            continue
        try:
            ns = os.readlink("/proc/%s/ns/net" % pid)
        except OSError:
            continue
        if ns in netns_done:
            continue
        netns_done.add(ns)
        n0 = len(out)
        _netz_lesen(pid, lambda _i, n=name: n, out, seen)
        for c in out[n0:]:
            c["container"] = name
    return out


def _jar_info(path):
    import zipfile
    info = {"datei": os.path.basename(path), "groesse": os.path.getsize(path)}
    try:
        with zipfile.ZipFile(path) as z:
            names = set(z.namelist())
            meta = None
            for m in ("paper-plugin.yml", "plugin.yml", "velocity-plugin.json", "bungee.yml"):
                if m in names:
                    raw = z.read(m).decode("utf-8", "replace")
                    meta = json.loads(raw) if m.endswith(".json") else (yaml.safe_load(raw) or {})
                    info["art"] = {"paper-plugin.yml": "Paper", "plugin.yml": "Bukkit/Paper", "velocity-plugin.json": "Velocity", "bungee.yml": "Bungee"}[m]
                    break
            meta = meta if isinstance(meta, dict) else {}
            au = meta.get("authors") or meta.get("author") or []
            if isinstance(au, str):
                au = [au]
            info.update({"name": str(meta.get("name") or meta.get("id") or os.path.basename(path)[:-4]), "id": str(meta.get("id") or ""),
                         "version": str(meta.get("version") or "?"), "autoren": [str(x.get("name") if isinstance(x, dict) else x) for x in au][:8],
                         "website": str(meta.get("website") or meta.get("url") or ""), "beschreibung": str(meta.get("description") or "")[:300]})
            tel = set()
            joined = "\n".join(names)
            for k, v in TELEMETRIE.items():
                if k in joined:
                    tel.add(v)
            info["telemetrie"] = sorted(tel)
    except Exception as e:
        info.update({"name": os.path.basename(path)[:-4], "version": "?", "autoren": [], "website": "", "beschreibung": "", "telemetrie": [],
                     "fehler": "Jar nicht lesbar: " + str(e)[:80]})
    return info


def _ordner_scan(d, db_namen, db_user):
    """Config-Dateien eines Plugin-Ordners: Speicherart, genutzte MariaDB-Datenbanken/Benutzer, lokale DB-Dateien, externe Adressen."""
    res = {"speicher": set(), "datenbanken": set(), "db_user": set(), "lokal": [], "hosts": set()}
    if not os.path.isdir(d):
        return res
    n = 0
    for root, dirs, files in os.walk(d):
        if root[len(d):].count(os.sep) >= 3:
            dirs[:] = []
        dirs[:] = [x for x in dirs if x.lower() not in ("logs", "backups", "backup", "cache", "lang", "languages", "locale", "translations", "playerdata", "userdata", "data", "libs", "libraries")]
        for fn in files:
            p = os.path.join(root, fn)
            low = fn.lower()
            if low.endswith((".db", ".sqlite", ".sqlite3", ".mv.db", ".h2.db")):
                try:
                    res["lokal"].append({"datei": os.path.relpath(p, d), "groesse": os.path.getsize(p)})
                except OSError:
                    pass
                continue
            if not low.endswith(CONF_EXT) or n > 60:
                continue
            try:
                if os.path.getsize(p) > 400000:
                    continue
                t = open(p, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            n += 1
            for m in re.finditer(r"(?i)(?:storage[-_ ]?(?:method|type)|\btype|driver|dialect)\s*[:=]\s*['\"]?(mysql|mariadb|sqlite|h2|mongodb|postgres(?:ql)?|yaml|json|flatfile)\b", t):
                res["speicher"].add(m.group(1).lower())
            for m in re.finditer(r"jdbc:(mysql|mariadb)://[^/\s'\"]+/([A-Za-z0-9_]+)", t):
                res["speicher"].add(m.group(1).lower())
                res["datenbanken"].add(m.group(2))
            for m in re.finditer(r"(?im)^\s*['\"]?(?:database|database[-_]?name|db[-_]?name|dbname|schema)['\"]?\s*[:=]\s*['\"]?([A-Za-z0-9_]+)", t):
                if m.group(1) in db_namen:
                    res["datenbanken"].add(m.group(1))
            for m in re.finditer(r"(?im)^\s*['\"]?(?:user(?:name)?|user[-_]name|db[-_]?user)['\"]?\s*[:=]\s*['\"]?([A-Za-z0-9_]+)", t):
                if m.group(1) in db_user:
                    res["db_user"].add(m.group(1))
            ohne_kommentar = "\n".join(z for z in t.splitlines() if not z.lstrip().startswith(("#", "//", ";")))
            for m in URL_RX.finditer(ohne_kommentar):
                h = m.group(1).lower()
                if not URL_IGNORE.search(h) and not DOKU_RX.search(h) and not re.match(r"^\d+\.\d+\.\d+\.\d+$", h):
                    res["hosts"].add(h)
    if res["datenbanken"] or res["db_user"]:
        res["speicher"].add("mariadb")
    return res


def _mariadb_root(query):
    pw = ""
    r = subprocess.run(["docker", "inspect", "mariadb", "-f", "{{range .Config.Env}}{{println .}}{{end}}"], capture_output=True, text=True, timeout=10)
    for ln in r.stdout.splitlines():
        if ln.startswith("MARIADB_ROOT_PASSWORD=") or ln.startswith("MYSQL_ROOT_PASSWORD="):
            pw = ln.split("=", 1)[1]
    if not pw:
        raise Fail("Kein Root-Zugang zur Datenbank gefunden.")
    r = subprocess.run(["docker", "exec", "-i", "-e", "MYSQL_PWD=" + pw, "mariadb", "sh", "-c", 'exec $(command -v mariadb || command -v mysql) -h127.0.0.1 -uroot -N -B --raw'],
                       input=query.encode(), capture_output=True, timeout=60)
    if r.returncode != 0:
        raise Fail("Datenbank: " + r.stderr.decode(errors="replace").strip()[:200])
    return [[None if c == "NULL" else c for c in ln.split("\t")] for ln in r.stdout.decode(errors="replace").splitlines()]


def _db_inventar():
    sys_db = ("information_schema", "performance_schema", "mysql", "sys")
    dbs = {}
    for r in _mariadb_root("select table_schema, coalesce(sum(data_length+index_length),0), count(*), coalesce(sum(table_rows),0) "
                           "from information_schema.tables group by table_schema;\nshow databases;"):
        if len(r) == 1:
            if r[0] not in sys_db:
                dbs.setdefault(r[0], {"name": r[0], "groesse": 0, "tabellen": 0, "zeilen": 0, "benutzer": [], "aktiv": [], "plugins": []})
        elif r[0] not in sys_db:
            dbs[r[0]] = {"name": r[0], "groesse": int(num_(r[1])), "tabellen": int(num_(r[2])), "zeilen": int(num_(r[3])), "benutzer": [], "aktiv": [], "plugins": []}
    users, global_admins = set(), []
    for r in _mariadb_root("select user, host, db from mysql.db;"):
        users.add(r[0])
        db = r[2].replace("\\_", "_") if r[2] else ""
        for name, d in dbs.items():
            if name == db or (("%" in db or "_" in db) and re.fullmatch(db.replace("%", ".*").replace("\\_", "_"), name)):
                d["benutzer"].append("%s@%s" % (r[0], r[1]))
    for r in _mariadb_root("select user, host from mysql.user where Select_priv='Y' or Super_priv='Y';"):
        users.add(r[0])
        global_admins.append("%s@%s" % (r[0], r[1]))
    for r in _mariadb_root("select user, db, count(*) from information_schema.processlist where db is not null group by user, db;"):
        if r[1] in dbs:
            dbs[r[1]]["aktiv"].append({"benutzer": r[0], "verbindungen": int(num_(r[2]))})
    for r in _mariadb_root("select user from mysql.user;"):
        users.add(r[0])
    return dbs, users, global_admins


def num_(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return 0.0


def _vorschlag(name):
    low = name.lower().replace("-", "").replace("_", "").replace(" ", "")
    for key, kat, zweck in BEKANNT:
        if key in low:
            return {"kategorien": kat, "zweck": zweck}
    return None


def plugins(a):
    """Plugin-Inventar aller Server mit Version, Entwickler, Telemetrie, Datenbanken, externen Adressen und Datenschutz-Doku."""
    names = _crafty_names()
    cache = _json_load(PLUGIN_CACHE, {})
    neu_cache = {}
    try:
        dbs, db_user, _ = _db_inventar()
        db_namen = set(dbs)
    except Fail:
        dbs, db_user, db_namen = {}, set(), set()
    doku = _json_load(COMPLIANCE_FILE, {})
    out = {}
    for sd in sorted(glob.glob(SERVERS + "/*/")):
        sd = sd.rstrip("/")
        sid = os.path.basename(sd)
        sname = names.get(sid[:8], sid[:8])
        proxy_ = os.path.exists(sd + "/velocity.toml")
        for jar in sorted(glob.glob(sd + "/plugins/*.jar")):
            st = os.stat(jar)
            key = "%s|%d|%d" % (jar, st.st_size, int(st.st_mtime))
            info = cache.get(key) or _jar_info(jar)
            neu_cache[key] = info
            pid = info["name"].lower()
            p = out.setdefault(pid, {"name": info["name"], "autoren": info["autoren"], "website": info["website"], "beschreibung": info["beschreibung"],
                                     "art": info.get("art", "?"), "telemetrie": set(info.get("telemetrie", [])), "server": [], "speicher": set(),
                                     "datenbanken": set(), "db_user": set(), "lokal": [], "hosts": set(), "versionen": set()})
            p["telemetrie"] |= set(info.get("telemetrie", []))
            p["versionen"].add(info["version"])
            ordner = None
            for cand in (info["name"], info.get("id") or "", info["name"].lower()):
                if cand and os.path.isdir(sd + "/plugins/" + cand):
                    ordner = sd + "/plugins/" + cand
                    break
            scan = _ordner_scan(ordner, db_namen, db_user) if ordner else None
            if scan:
                p["speicher"] |= scan["speicher"]
                p["datenbanken"] |= scan["datenbanken"]
                p["db_user"] |= scan["db_user"]
                p["hosts"] |= scan["hosts"]
                p["lokal"] += [dict(x, server=sname) for x in scan["lokal"][:6]]
            p["server"].append({"server": sname, "proxy": proxy_, "version": info["version"], "datei": info["datei"], "groesse": info["groesse"],
                                "geaendert": int(st.st_mtime)})
    _json_save(PLUGIN_CACHE, neu_cache)
    user_db = {}
    for d in dbs.values():
        for u in d["benutzer"]:
            user_db.setdefault(u.split("@")[0], set()).add(d["name"])
    liste = []
    for pid, p in out.items():
        for u in p["db_user"]:                      # Benutzer aus der Config → Datenbanken, auf die er Rechte hat
            if not p["datenbanken"] and u in user_db:
                p["datenbanken"] |= user_db[u]
        doc = doku.get(pid)
        vers = sorted(p["versionen"])
        status = "dokumentiert" if doc else "offen"
        if doc and doc.get("version") and doc["version"] not in vers:
            status = "geändert"
        ext = sorted(p["hosts"]) + sorted(p["telemetrie"])
        liste.append({"id": pid, "name": p["name"], "autoren": p["autoren"], "website": p["website"], "beschreibung": p["beschreibung"], "art": p["art"],
                      "versionen": vers, "server": p["server"], "telemetrie": sorted(p["telemetrie"]), "hosts": sorted(p["hosts"])[:15],
                      "speicher": sorted(p["speicher"]), "datenbanken": sorted(p["datenbanken"]), "db_user": sorted(p["db_user"]), "lokal": p["lokal"][:10],
                      "extern": bool(ext), "doku": doc, "vorschlag": None if doc else _vorschlag(p["name"]), "status": status})
    norm = lambda s: re.sub(r"[^a-z0-9]", "", s.lower())
    for d in dbs.values():                          # Datenbank ohne gefundene Config → Plugin mit gleichem Namen (cb_ = CityBuild-Kopie)
        if not any(d["name"] in x["datenbanken"] for x in liste):
            for x in liste:
                if norm(x["name"]) == norm(re.sub(r"^cb_", "", d["name"])):
                    x["datenbanken"] = sorted(set(x["datenbanken"]) | {d["name"]})
                    if "mariadb" not in x["speicher"]:
                        x["speicher"] = sorted(set(x["speicher"]) | {"mariadb"})
    for d in dbs.values():
        d["plugins"] = sorted(x["name"] for x in liste if d["name"] in x["datenbanken"])
    liste.sort(key=lambda x: ({"offen": 0, "geändert": 1, "dokumentiert": 2}[x["status"]], x["name"].lower()))
    return {"plugins": liste, "kategorien": KATEGORIEN, "datenbanken": sorted(dbs.values(), key=lambda d: -d["groesse"]),
            "zeit": int(time.time())}


def datenbanken(a):
    dbs, _, admins = _db_inventar()
    return {"datenbanken": sorted(dbs.values(), key=lambda d: -d["groesse"]), "globale_rechte": admins}


def plugin_doku(a):
    pid = str(a.get("id") or "").lower()[:80]
    if not pid:
        raise Fail("Plugin fehlt.")
    doku = _json_load(COMPLIANCE_FILE, {})
    if a.get("loeschen"):
        doku.pop(pid, None)
    else:
        clean = lambda s, n=500: re.sub(r"[\x00-\x08\x0b-\x1f]", "", str(s or "")).strip()[:n]
        kat = [k for k in (a.get("kategorien") or []) if k in KATEGORIEN]
        doku[pid] = {"kategorien": kat, "zweck": clean(a.get("zweck"), 300), "rechtsgrundlage": clean(a.get("rechtsgrundlage"), 120),
                     "speicherdauer": clean(a.get("speicherdauer"), 120), "extern": clean(a.get("extern"), 300), "notiz": clean(a.get("notiz"), 600),
                     "version": clean(a.get("version"), 60), "geprueft_von": clean(a.get("actor"), 60), "geprueft_am": int(time.time())}
    _json_save(COMPLIANCE_FILE, doku)
    return {"ok": True}


def ds_status(a):
    """Kurzstatus für die Seitenleiste: unbekannte Ziele + Plugins ohne aktuelle Doku."""
    try:
        unb = rows("SELECT JSON_OBJECT('n', count(*)) FROM rl_outbound WHERE bekannt=0")[0]["n"]
    except Exception:
        unb = 0
    doku = _json_load(COMPLIANCE_FILE, {})
    cache = _json_load(PLUGIN_CACHE, {})
    namen = {v["name"].lower() for v in cache.values() if isinstance(v, dict) and v.get("name")}
    return {"unbekannt": int(unb), "undokumentiert": len([n for n in namen if n not in doku])}


def _rdns(ip):
    import socket
    import threading
    res = [None]

    def look():
        try:
            res[0] = socket.gethostbyaddr(ip)[0][:255]
        except Exception:
            pass
    t = threading.Thread(target=look, daemon=True)
    t.start()
    t.join(2)
    return res[0]


def aufzeichnen(a):
    """Läuft jede Minute (cron): alle offenen ausgehenden Verbindungen aller Container in rl_outbound eintragen.
    Neue Ziele werden vom Discord-Bot dem Team gemeldet, sofern sie nicht schon (bei einem anderen Server) als bekannt markiert sind."""
    try: _auskunft_aufraeumen()           # abgelaufene Auskunft-Links löschen
    except Exception: pass
    try: _auskunft_log_pflegen()          # Auskunft-Protokoll: >3 Jahre löschen, nach Löschung anonymisieren
    except Exception: pass
    _outbound_spalten()
    live = live_alle()
    sni = _sni_map()
    if not live:
        return {"neu": 0, "nachgetragen": hosts_nachtragen()}
    bekannt = {}
    for h in rows("SELECT JSON_OBJECT('ip', ip, 'dienst', dienst) FROM rl_outbound WHERE bekannt=1"):
        bekannt.setdefault(h["ip"], h["dienst"])
    vorhanden = {(h["ip"], int(h["port"]), h["server"]) for h in rows("SELECT JSON_OBJECT('ip', ip, 'port', port, 'server', server) FROM rl_outbound")}
    neu = 0
    for c in live:
        key = (c["ip"], c["port"], c["server"][:64])
        if key in vorhanden:
            execute("UPDATE rl_outbound SET zuletzt=NOW(), anzahl=anzahl+IF(zuletzt < NOW() - INTERVAL 10 MINUTE, 1, 0) "
                    "WHERE ip=%s AND port=%d AND server=%s" % (q(key[0]), key[1], q(key[2])))
            continue
        dienst, ok = bekannt.get(c["ip"]), c["ip"] in bekannt
        fg = gr = None
        host = sni.get(c["ip"])
        info = _host_info(host)
        if info and info[0] == "LLM-Gateway" and c.get("container") not in LLM_CONTAINER:
            info = None   # LLM-Ziele nur für den eigenen LLM-Gateway automatisch freigeben, sonst unbekannt (Wächter meldet)
        elif info and c.get("container") == "ryvenlink-bot" and info[0] not in ("Discord", "KI", "Mojang"):
            info = None   # Bot darf nur Discord, KI und Mojang automatisch – alles andere meldet der Wächter
        if info:
            fg, gr = _freigabe(info)
        if not ok and info:
            dienst, ok = "%s (%s, automatisch)" % (info[1], host), True
        elif not dienst and host:
            dienst = host
        execute("INSERT IGNORE INTO rl_outbound (ip, port, server, dienst, rdns, bekannt, gemeldet, zuerst, zuletzt, anzahl, freigabe, grund) "
                "VALUES (%s, %d, %s, %s, %s, %d, %d, NOW(), NOW(), 1, %s, %s)" % (q(key[0]), key[1], q(key[2]), q(dienst), q(_rdns(c["ip"])), 1 if ok else 0, 1 if ok else 0, q(fg), q(gr)))
        neu += 1
    return {"neu": neu, "offen": len(live), "nachgetragen": hosts_nachtragen()}




# ───────────────────────────── Hostnamen (sni.py) und Wächter-Bericht ─────────────────────────────
SNI_FILE = "/var/lib/pxmx/sni.json"
# Hostname (Endung) → (Kategorie, Dienst). Alle stehen in der Datenschutzerklärung (Statistik-Dienste, Update-Prüfungen, Shop, Bedrock, Mojang, Discord).
HOST_KATALOG = [
    ("bstats.org", "Statistik", "bStats – anonyme Plugin-Statistik"),
    ("pstats.devs.beer", "Statistik", "pStats – Plugin-Statistik"),
    ("faststats.dev", "Statistik", "FastStats – Plugin-Statistik"),
    ("metrics.artillex-studios.com", "Statistik", "Artillex – Plugin-Statistik"),
    ("api.artillex-studios.com", "Lizenz/Update", "Artillex – Lizenz- und Update-Prüfung"),
    ("fancyanalytics.net", "Statistik", "FancyAnalytics – Plugin-Statistik"),
    ("luckperms.net", "Update", "LuckPerms – Update-Prüfung"),
    ("geysermc.org", "Bedrock", "GeyserMC – Bedrock-Unterstützung"),
    ("craftingstore.net", "Shop", "CraftingStore – Webshop-Auslieferung"),
    ("sessionserver.mojang.com", "Mojang", "Mojang – Skins/Profile"),
    ("api.mojang.com", "Mojang", "Mojang – Spielerprofile"),
    ("api.minecraftservices.com", "Mojang", "Microsoft/Mojang – Profile"),
    ("textures.minecraft.net", "Mojang", "Mojang – Skin-Texturen"),
    ("discord.com", "Discord", "Discord-API (eigener Bot)"),
    ("discord.gg", "Discord", "Discord-Gateway (eigener Bot)"),
    ("discordapp.com", "Discord", "Discord-CDN (eigener Bot)"),
    ("api.github.com", "Update", "GitHub – Update-Prüfung von Plugins"),
    ("api.spigotmc.org", "Update", "SpigotMC – Update-Prüfung"),
    ("api.modrinth.com", "Update", "Modrinth – Update-Prüfung"),
    ("hangar.papermc.io", "Update", "Hangar – Update-Prüfung"),
    ("api.papermc.io", "Update", "PaperMC – Update-Prüfung"),
    ("update.viaversion.com", "Update", "ViaVersion – Update-Prüfung"),
    ("api.loohpjames.com", "Update", "LOOHP (InteractionVisualizer) – Update-Prüfung"),
    ("api.anthropic.com", "KI", "Anthropic – KI-Support (eigener Bot)"),
    ("api.openai.com", "LLM-Gateway", "OpenAI – eigener LLM-Gateway"),
    ("api.groq.com", "LLM-Gateway", "Groq – eigener LLM-Gateway"),
    ("generativelanguage.googleapis.com", "LLM-Gateway", "Google Gemini – eigener LLM-Gateway"),
    ("api.mistral.ai", "LLM-Gateway", "Mistral – eigener LLM-Gateway"),
    ("openrouter.ai", "LLM-Gateway", "OpenRouter – eigener LLM-Gateway"),
    ("ollama.com", "LLM-Gateway", "Ollama – Modell-Downloads"),
    ("ollama.ai", "LLM-Gateway", "Ollama – Modell-Downloads"),
]
# Datenschutz-Freigabe je Kategorie: (Fundstelle in der Datenschutzerklärung, Grund/Rechtsgrundlage).
# Ändert sich die Erklärung, hier mitziehen – der Wächter trägt beides automatisch bei jedem bekannten Ziel ein.
FREIGABE = {
    "Statistik": ("Datenschutzerklärung F2, Abschn. 2 – Statistik-Dienste", "Art. 6 Abs. 1 lit. f DSGVO: technische Plugin-Statistik, Empfänger sieht nur die Server-IP"),
    "Lizenz/Update": ("Datenschutzerklärung F2, Abschn. 2 – Update-Prüfungen", "Art. 6 Abs. 1 lit. f DSGVO: Lizenz-/Versionsabfrage ohne Spielerdaten"),
    "Update": ("Datenschutzerklärung F2, Abschn. 2 – Update-Prüfungen", "Art. 6 Abs. 1 lit. f DSGVO: Versionsabfrage ohne Spielerdaten"),
    "Bedrock": ("Datenschutzerklärung F2, Abschn. 2 – Bedrock-Spieler", "Art. 6 Abs. 1 lit. b DSGVO: ohne GeyserMC kein Bedrock-Spiel (XUID, Gamertag, Skin)"),
    "Mojang": ("Datenschutzerklärung F2, Abschn. 2 – Skins / Abschn. 6 – Bestenlisten", "Art. 6 Abs. 1 lit. b/f DSGVO: Skin- und Profilabfrage über Name/UUID"),
    "Discord": ("Datenschutzerklärung F2, Abschn. 3 – Discord", "Art. 6 Abs. 1 lit. b DSGVO: eigener Bot (Verknüpfung, Rollen, Auskunft per DM)"),
    "KI": ("Datenschutzerklärung F2, Abschn. 4 – Support-Bot (KI)", "Art. 6 Abs. 1 lit. b/f DSGVO; Anthropic als Auftragsverarbeiter, EU-Standardvertragsklauseln"),
    "Shop": ("Keine – Store nicht in Betrieb (Abschn. 5)", "Nur Abfrage des CraftingStore-Plugins ohne Spielerdaten; Plugin entfernen, solange der Store geschlossen ist"),
    "LLM-Gateway": ("Kein Personenbezug – interner LLM-Gateway (/opt/llm)", "Interner LLM-Gateway (/opt/llm), keine Spielerdaten vorgesehen"),
}
LLM_CONTAINER = ("litellm", "ollama")   # nur diese Container dürfen LLM-Ziele automatisch freigegeben bekommen


def _freigabe(info):
    return FREIGABE.get(info[0], (None, None)) if info else (None, None)


_SPALTEN_OK = False


def _outbound_spalten():
    """Spalten für Datenschutz-Freigabe und Grund (einmalig anlegen)."""
    global _SPALTEN_OK
    if not _SPALTEN_OK:
        if not int(rows("SELECT JSON_OBJECT('n', COUNT(*)) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='rl_outbound' AND COLUMN_NAME='freigabe'")[0]["n"]):
            execute("ALTER TABLE rl_outbound ADD COLUMN IF NOT EXISTS freigabe VARCHAR(160) NULL, ADD COLUMN IF NOT EXISTS grund VARCHAR(255) NULL")
        _SPALTEN_OK = True
CF_NETZE = ("173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20",
            "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22")


def _cloudflare(ip):
    import ipaddress
    try:
        a = ipaddress.ip_address(ip)
        return any(a in ipaddress.ip_network(n) for n in CF_NETZE)
    except ValueError:
        return False


def _sni_map():
    """IP → zuletzt gesehener Hostname (aus sni.py)."""
    d = _json_load(SNI_FILE, {})
    return {ip: max(hs.items(), key=lambda kv: kv[1].get("zuletzt", 0))[0] for ip, hs in d.items() if hs}


def _host_info(host):
    h = (host or "").lower()
    for endung, kat, dienst in HOST_KATALOG:
        if h == endung or h.endswith("." + endung):
            return kat, dienst
    return None


def hosts_nachtragen():
    """Ungeprüfte Einträge, deren Hostname jetzt bekannt ist, automatisch einordnen."""
    sni = _sni_map()
    n = 0
    _outbound_spalten()
    for h in rows("SELECT JSON_OBJECT('ip', ip, 'port', port, 'server', server, 'bekannt', bekannt, 'dienst', dienst, 'grund', grund) FROM rl_outbound WHERE bekannt=0 OR freigabe IS NULL"):
        host = sni.get(h["ip"])
        info = _host_info(host)
        if info and info[0] == "LLM-Gateway":
            info = None   # LLM-Ziele nur über aufzeichnen() (dort ist der Container bekannt)
        if info and h.get("bekannt") and h.get("grund"):
            continue      # manuelle Entscheidung nicht überschreiben
        if info:
            fg, gr = _freigabe(info)
            dienst = h["dienst"] if h.get("bekannt") and h.get("dienst") else "%s (%s, automatisch)" % (info[1], host or "Hostname")
            execute("UPDATE rl_outbound SET bekannt=1, gemeldet=1, dienst=%s, freigabe=%s, grund=%s WHERE ip=%s AND port=%d AND server=%s"
                    % (q(dienst), q(fg), q(gr), q(h["ip"]), int(h["port"]), q(h["server"])))
            n += 1
        elif not h.get("bekannt") and host:
            execute("UPDATE rl_outbound SET dienst=%s WHERE ip=%s AND port=%d AND server=%s AND (dienst IS NULL OR dienst='')"
                    % (q(host), q(h["ip"]), int(h["port"]), q(h["server"])))
    return n


def waechter_bericht(a):
    """Auswertung: woher die Ziele stammen und warum ständig „neue“ auftauchen."""
    names = _crafty_names()
    sni_roh = _json_load(SNI_FILE, {})
    sni = _sni_map()
    hist = rows("SELECT %s FROM rl_outbound" % OUT_COLS)
    dienste, unklar = {}, []
    for h in hist:
        h["server_name"] = names.get(h["server"], h["server"])
        host = sni.get(h["ip"]) or ""
        info = _host_info(host)
        if not info and h.get("dienst"):
            info = next((i for i in (_host_info(x) for x in re.findall(r"[a-z0-9.\-]+\.[a-z]{2,}", h["dienst"].lower())) if i), None) \
                or (("Discord", "Discord-API (eigener Bot)") if "discord" in h["dienst"].lower() else None) \
                or ("Manuell geprüft" if h.get("bekannt") else None, h["dienst"])
        if not info or not info[0]:
            unklar.append({"ip": h["ip"], "port": h["port"], "server": h["server_name"], "host": host, "cloudflare": _cloudflare(h["ip"]),
                           "zuerst": h["zuerst"], "rdns": h.get("rdns")})
            continue
        key = info[1]
        d = dienste.setdefault(key, {"dienst": key, "kategorie": info[0], "ips": set(), "server": set(), "hosts": set(), "cloudflare": 0, "anzahl": 0})
        d["ips"].add(h["ip"])
        d["server"].add(h["server_name"])
        if host:
            d["hosts"].add(host)
        d["cloudflare"] += 1 if _cloudflare(h["ip"]) else 0
        d["anzahl"] += int(h.get("anzahl") or 0)
    liste = sorted(({**d, "ips": len(d["ips"]), "server": sorted(d["server"]), "hosts": sorted(d["hosts"])} for d in dienste.values()),
                   key=lambda d: -d["ips"])
    ips = {h["ip"] for h in hist}
    cf = {ip for ip in ips if _cloudflare(ip)}
    hosts_alle = {h for hs in sni_roh.values() for h in hs}
    votes = 0
    for f in glob.glob(SERVERS + "/*/logs/latest.log"):
        try:
            if glob.glob(os.path.dirname(os.path.dirname(f)) + "/plugins/*otifier*"):
                votes += sum(1 for ln in open(f, encoding="utf-8", errors="replace") if re.search(r"(?i)vote", ln))
        except OSError:
            pass
    kat = {}
    for d in liste:
        kat[d["kategorie"]] = kat.get(d["kategorie"], 0) + d["ips"]
    return {"eintraege": len(hist), "ips": len(ips), "cloudflare_ips": len(cf), "hosts": len(hosts_alle), "dienste": liste, "unklar": unklar,
            "kategorien": kat, "votes_heute": votes, "sni_aktiv": os.path.exists(SNI_FILE) and time.time() - os.path.getmtime(SNI_FILE) < 600,
            "zeit": int(time.time())}


# ───────────────────────────── Datenschutz: Löschanträge (Panel statt ingame) ─────────────────────────────
# Das Panel löst denselben Ablauf aus wie /datenschutz freigeben im Spiel: RyvenLink (Proxy) exportiert die Daten,
# löscht Datenbanken, schwärzt Logs und verteilt die Aufgaben an alle Backends; der Bot schickt den Export per Discord-DM.

DS_COLS = ("JSON_OBJECT('id', r.id, 'uuid', r.uuid, 'name', r.name, 'status', r.status, 'erstellt', r.created_at, 'handler', r.handler, "
           "'bearbeitet', r.handled_at, 'fertig', r.done_at, 'grund', r.reason, 'export', r.export_status, 'discord', r.discord_id IS NOT NULL, "
           "'jobs', (SELECT COUNT(*) FROM rl_privacy_jobs j WHERE j.request_id=r.id), "
           "'jobs_fertig', (SELECT COUNT(*) FROM rl_privacy_jobs j WHERE j.request_id=r.id AND j.status='erledigt'), "
           "'jobs_offen', (SELECT GROUP_CONCAT(j.server) FROM rl_privacy_jobs j WHERE j.request_id=r.id AND j.status<>'erledigt'))")


def ds_antraege(a):
    return {"antraege": rows("SELECT %s FROM rl_privacy_requests r ORDER BY FIELD(r.status,'offen','laeuft') DESC, r.id DESC LIMIT %d;" % (DS_COLS, limit(a, 200, 1000)))}


def _ds_spieler(wer):
    wer = str(wer or "").strip()
    if UUID_RX.match(wer):
        r = rows("SELECT JSON_OBJECT('uuid', uuid, 'name', name) FROM rl_players WHERE uuid=%s;" % q(wer))
    elif NAME_RX.match(wer):
        r = rows("SELECT JSON_OBJECT('uuid', uuid, 'name', name) FROM rl_players WHERE name=%s ORDER BY last_seen DESC LIMIT 1;" % q(wer))
    else:
        raise Fail("Bitte einen Spielernamen oder eine UUID angeben.")
    if not r:
        raise Fail("Spieler „%s“ war nie auf dem Netzwerk." % wer)
    return r[0]


def ds_vorschau(a):
    """Was passiert beim Löschen? (Spieler, Discord-Verknüpfung für den Export, laufende Anträge)"""
    s = _ds_spieler(a.get("spieler"))
    link = rows("SELECT JSON_OBJECT('discord', discord_id) FROM rl_links WHERE uuid=%s;" % q(s["uuid"]))
    offen = rows("SELECT JSON_OBJECT('id', id, 'status', status) FROM rl_privacy_requests WHERE uuid=%s AND status IN ('offen','laeuft') ORDER BY id DESC LIMIT 1;" % q(s["uuid"]))
    return {"spieler": s, "discord": bool(link), "antrag": offen[0] if offen else None}


def ds_loeschen(a):
    """Ein Klick: offenen Antrag freigeben – oder für einen Spieler direkt einen Antrag anlegen und freigeben."""
    actor = str(a.get("actor") or "Panel")[:24]
    pid, _ = proxy()
    if a.get("id"):
        rid = int(a["id"])
        r = rows("SELECT JSON_OBJECT('status', status, 'name', name) FROM rl_privacy_requests WHERE id=%d;" % rid)
        if not r:
            raise Fail("Antrag #%d gibt es nicht." % rid)
        if r[0]["status"] != "offen":
            raise Fail("Antrag #%d ist nicht offen (%s)." % (rid, r[0]["status"]))
        name = r[0]["name"]
    else:
        v = ds_vorschau(a)
        s = v["spieler"]
        if v["antrag"] and v["antrag"]["status"] == "laeuft":
            raise Fail("Für %s läuft bereits eine Löschung (#%s)." % (s["name"], v["antrag"]["id"]))
        if v["antrag"]:
            rid = int(v["antrag"]["id"])
        else:
            execute("INSERT INTO rl_privacy_requests (uuid, name, discord_id) SELECT %s, %s, (SELECT discord_id FROM rl_links WHERE uuid=%s);"
                    % (q(s["uuid"]), q(s["name"]), q(s["uuid"])))
            rid = int(rows("SELECT JSON_OBJECT('id', MAX(id)) FROM rl_privacy_requests WHERE uuid=%s AND status='offen';" % q(s["uuid"]))[0]["id"])
        name = s["name"]
    crafty_stdin(pid, "datenschutz freigeben #%d" % rid)
    # RyvenLink trägt „Konsole“ als Bearbeiter ein → durch den Team-Account aus dem Panel ersetzen
    for _ in range(10):
        time.sleep(1)
        st = rows("SELECT JSON_OBJECT('status', status) FROM rl_privacy_requests WHERE id=%d;" % rid)
        if st and st[0]["status"] != "offen":
            execute("UPDATE rl_privacy_requests SET handler=%s WHERE id=%d;" % (q(actor), rid))
            return {"id": rid, "name": name, "status": st[0]["status"]}
    raise Fail("Der Proxy hat Antrag #%d nicht übernommen – läuft RyvenLink? (Befehl wurde gesendet)" % rid)


def ds_ablehnen(a):
    rid = int(a.get("id") or 0)
    grund = re.sub(r"[\r\n\t]+", " ", str(a.get("grund") or "")).strip()[:200]
    r = rows("SELECT JSON_OBJECT('status', status) FROM rl_privacy_requests WHERE id=%d;" % rid)
    if not r or r[0]["status"] != "offen":
        raise Fail("Antrag #%d ist nicht offen." % rid)
    pid, _ = proxy()
    crafty_stdin(pid, ("datenschutz ablehnen #%d %s" % (rid, grund)).strip())
    for _ in range(8):
        time.sleep(1)
        st = rows("SELECT JSON_OBJECT('status', status) FROM rl_privacy_requests WHERE id=%d;" % rid)
        if st and st[0]["status"] == "abgelehnt":
            execute("UPDATE rl_privacy_requests SET handler=%s WHERE id=%d;" % (q(str(a.get("actor") or "Panel")[:24]), rid))
            return {"id": rid}
    raise Fail("Der Proxy hat die Ablehnung nicht übernommen.")



# ───────────────────────────── Datenschutz: Auskunft (Art. 15 DSGVO) per E-Mail oder Download ─────────────────────────────
# Nur lesen, nichts löschen. Gleiche Quellen wie der Export beim Löschen (RyvenLink loeschen-sql/export-sql, Dateien mit UUID im Namen,
# Server-Listen, Logs, CoreProtect). Versand über SMTP aus /etc/pxmx/mail.json (vom Owner angelegt, nie im Repo).

MAIL_CONF = "/etc/pxmx/mail.json"
AUSKUNFT_LOG = "/var/lib/pxmx/auskunft.log"
AUSKUNFT_MAX_MAIL = 18 * 1024 * 1024
AUSKUNFT_MAX_DATEI = 8 * 1024 * 1024
AUSKUNFT_SKIP = {"libraries", "cache", "versions", "region", "entities", "poi", "logs", "crash-reports", "backups", "libs", "bundler",
                 "web", "tiles", "maps", "timings", "debug", ".paper-remapped", "spark"}
EMAIL_RX = re.compile(r"^[^@\s<>\"',;]{1,64}@[A-Za-z0-9.-]{1,190}\.[A-Za-z]{2,24}$")


def _root_tsv(query):
    """Wie _mariadb_root, aber mit Spaltenköpfen (für CSV)."""
    pw = ""
    r = subprocess.run(["docker", "inspect", "mariadb", "-f", "{{range .Config.Env}}{{println .}}{{end}}"], capture_output=True, text=True, timeout=10)
    for ln in r.stdout.splitlines():
        if ln.startswith("MARIADB_ROOT_PASSWORD=") or ln.startswith("MYSQL_ROOT_PASSWORD="):
            pw = ln.split("=", 1)[1]
    r = subprocess.run(["docker", "exec", "-i", "-e", "MYSQL_PWD=" + pw, "mariadb", "sh", "-c", 'exec $(command -v mariadb || command -v mysql) -h127.0.0.1 -uroot -B'],
                       input=query.encode(), capture_output=True, timeout=60)
    if r.returncode != 0:
        raise Fail(r.stderr.decode(errors="replace").strip()[:200])
    z = r.stdout.decode(errors="replace").splitlines()
    return (z[0].split("\t"), [x.split("\t") for x in z[1:]]) if z else ([], [])


def _csv(kopf, zeilen):
    f = lambda v: "" if v in (None, "NULL") else ('"' + str(v).replace('"', '""') + '"' if any(c in str(v) for c in ';"\n\r') else str(v))
    return "﻿" + ";".join(map(f, kopf)) + "\n" + "\n".join(";".join(map(f, r)) for r in zeilen) + "\n"


def _ds_statements():
    """export-sql zuerst (umfassender), dann loeschen-sql – aus der Live-Config, sonst aus dem Standard im RyvenLink-Jar."""
    _, d = proxy()
    cfg = link_config().get("datenschutz") or {}
    exp, loe = list(cfg.get("export-sql") or []), list(cfg.get("loeschen-sql") or [])
    if not loe:
        for jar in glob.glob(d + "/plugins/RyvenLink*.jar"):
            with zipfile.ZipFile(jar) as zf:
                dd = (yaml.safe_load(zf.read("config.yml")) or {}).get("datenschutz") or {}
            exp, loe = exp or list(dd.get("export-sql") or []), list(dd.get("loeschen-sql") or [])
            break
    return exp + loe


AUSKUNFT_KATEGORIEN = [
    ("konto", "Konto & Stammdaten", "Name, UUID, Discord-Verknüpfung, Einwilligungen, Rechte, Skin, Whitelist/OP-Einträge"),
    ("wirtschaft", "Wirtschaft & Spielwährung", "Transaktionen, Kontostände, Punkte, Coinflip, Jobs"),
    ("moderation", "Moderation", "Strafen (Bans, Kicks, Mutes), Reports"),
    ("support", "KI-Support", "Unterhaltungen mit dem Support-Bot"),
    ("chat", "Chat & Befehle", "Chat- und Befehls-Logzeilen der letzten 31 Tage"),
    ("verbindungen", "Verbindungen (Login/IP)", "Login-/Logout- und IP-Logzeilen der letzten 31 Tage"),
    ("spielstand", "Spielstand & Plugin-Dateien", "Spielerdaten, Statistiken, Erfolge und Plugin-Dateien mit deiner UUID"),
    ("bauprotokoll", "Bauprotokoll (CoreProtect)", "Gesetzte/abgebaute Blöcke, Kisten, Chat und Befehle laut CoreProtect"),
    ("plots", "Plots", "PlotSquared-/CityBuildCore-Plotdaten"),
    ("sonstiges", "Sonstiges", "Übrige Datenbanktabellen (Crates, Rewards, Teams, Bestenlisten …)"),
]
AUSKUNFT_KAT_IDS = [k[0] for k in AUSKUNFT_KATEGORIEN]


class _Sammler:
    """Sammelt Auskunft-Einträge je Kategorie. zf=None: nur zählen (Kategorienliste); auswahl=None: alle Kategorien."""
    def __init__(self, zf=None, auswahl=None):
        self.zf, self.auswahl, self.inhalt = zf, auswahl, []
        self.stat = {k: [0, 0] for k in AUSKUNFT_KAT_IDS}

    def an(self, kat):
        return self.auswahl is None or kat in self.auswahl

    def add(self, kat, pfad, daten=None, quelle=None, anzahl=1, zusatz=""):
        if not self.an(kat): return
        if daten is not None:
            if isinstance(daten, str): daten = daten.encode("utf-8")
            groesse = len(daten)
        else:
            groesse = os.path.getsize(quelle)
        if self.zf is not None:
            if daten is not None: self.zf.writestr(pfad, daten)
            else: self.zf.write(quelle, pfad)
        self.stat[kat][0] += anzahl; self.stat[kat][1] += groesse
        self.inhalt.append("%s%s [%s]" % (pfad, zusatz, kat))


def _kat_db(basis):
    sch, tab = [x.lower() for x in basis.split("/", 1)]
    if "plot" in sch or "plot" in tab: return "plots"
    if tab in ("rl_punishments", "rl_reports") or (sch.endswith("bansystem") and tab != "bedrockplayer"): return "moderation"
    if tab == "rl_support": return "support"
    if tab == "eco_tx" or "playerpoints" in sch or "coinflip" in sch or sch.startswith(("jobs", "cb_jobs")): return "wirtschaft"
    if sch.startswith(("ryvendiscord", "luckperms", "skinsrestorer", "cb_skinsrestorer")) or tab == "bedrockplayer": return "konto"
    return "sonstiges"


def _ds_datenbanken(sm, uuid, nodash, name):
    schon = set()
    bind = lambda w: w.replace("{uuid}", q(uuid)).replace("{nodash}", q(nodash)).replace("{name}", q(name))
    for st in _ds_statements():
        abfragen = []
        m = re.match(r"^\s*([A-Za-z0-9_]+)\.\*\s*:\s*([A-Za-z0-9_]+)\s*$", st)
        if m:
            try: tabs = _mariadb_root("SELECT table_name FROM information_schema.columns WHERE table_schema=%s AND column_name=%s;" % (q(m[1]), q(m[2])))
            except Fail: tabs = []
            abfragen = [(m[1] + "/" + t[0], "SELECT * FROM `%s`.`%s` WHERE `%s` IN (%s,%s);" % (m[1], t[0], m[2], q(uuid), q(nodash))) for t in tabs]
        else:
            m = re.match(r"(?is)^\s*(?:DELETE\s+FROM|UPDATE)\s+([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)(?:\s+SET\s+.*?)?\s+(WHERE\s+.*)$", st)
            if m: abfragen = [(m[1] + "/" + m[2], "SELECT * FROM `%s`.`%s` %s;" % (m[1], m[2], bind(m[3])))]
        for basis, sql in abfragen:
            if basis in schon: continue
            if not sm.an(_kat_db(basis)): continue
            try: kopf, zeilen = _root_tsv(sql)
            except Fail: continue
            if not zeilen: continue
            schon.add(basis)
            sm.add(_kat_db(basis), "Datenbanken/%s.csv" % basis, _csv(kopf, zeilen), anzahl=len(zeilen), zusatz=" (%d Zeilen)" % len(zeilen))


def _ds_server(sm, uuid, nodash, name, bis):
    namen = _crafty_names()
    name_rx = re.compile(r"(?<![A-Za-z0-9_])" + re.escape(name) + r"(?![A-Za-z0-9_])")
    for sd in sorted(glob.glob(SERVERS + "/*/")):
        sid = os.path.basename(sd.rstrip("/"))
        ordner = re.sub(r"[^A-Za-z0-9_-]", "_", namen.get(sid[:8], sid[:8]))
        # Dateien mit der UUID im Namen (Spielstand, Statistiken, Erfolge, Plugin-Daten)
        for root, dirs, files in os.walk(sd):
            if time.time() > bis: sm.inhalt.append("%s: Dateisuche aus Zeitgründen gekürzt" % ordner); break
            dirs[:] = [x for x in dirs if x.lower() not in AUSKUNFT_SKIP and not x.startswith(".")]
            for f in files:
                fl = f.lower()
                if uuid in fl or nodash in fl:
                    p = os.path.join(root, f)
                    try:
                        if os.path.getsize(p) <= AUSKUNFT_MAX_DATEI:
                            rel = os.path.relpath(p, sd)
                            sm.add("plots" if re.search(r"plotsquared|citybuildcore|plots", rel.lower()) else "spielstand", "%s/Dateien/%s" % (ordner, rel), quelle=p)
                    except OSError: pass
        # Server-Listen
        for lf in ("whitelist.json", "ops.json", "banned-players.json", "usercache.json"):
            try:
                e = [x for x in json.load(open(sd + lf, encoding="utf-8")) if str(x.get("uuid", "")).lower() in (uuid, nodash)]
                if e: sm.add("konto", "%s/Server-Listen/%s" % (ordner, lf), json.dumps(e, indent=2, ensure_ascii=False))
            except (OSError, ValueError): pass
        # Logs (letzte 30 Tage – älter wird ohnehin gelöscht): Zeilen mit Name oder UUID
        treffer, verb, groesse = [], [], 0
        for lf in (sorted(glob.glob(sd + "logs/*.log") + glob.glob(sd + "logs/*.log.gz")) if (sm.an("chat") or sm.an("verbindungen")) else []):
            if time.time() > bis or groesse > 5 * 1024 * 1024: break
            try:
                if time.time() - os.path.getmtime(lf) > 31 * 86400: continue
                fh = gzip.open(lf, "rt", encoding="utf-8", errors="replace") if lf.endswith(".gz") else open(lf, encoding="utf-8", errors="replace")
                with fh:
                    for z in fh:
                        if uuid in z or name_rx.search(z):
                            (verb if LOG_VERB_RX.search(z) else treffer).append(os.path.basename(lf) + ": " + z.rstrip()); groesse += len(z)
            except OSError: pass
        if treffer:
            sm.add("chat", "%s/Logs-Chat-Befehle.txt" % ordner, "\n".join(treffer) + "\n", anzahl=len(treffer), zusatz=" (%d Zeilen)" % len(treffer))
        if verb:
            sm.add("verbindungen", "%s/Logs-Verbindungen.txt" % ordner, "\n".join(verb) + "\n", anzahl=len(verb), zusatz=" (%d Zeilen)" % len(verb))
        # CoreProtect (Block-, Kisten-, Chat- und Befehlsprotokoll)
        db = sd + "plugins/CoreProtect/database.db"
        if os.path.isfile(db) and time.time() < bis and sm.an("bauprotokoll"):
            try:
                con = sqlite3.connect("file:%s?mode=ro" % db, uri=True, timeout=5)
                u = con.execute("SELECT rowid FROM co_user WHERE uuid IN (?,?)", (uuid, nodash)).fetchone()
                if u:
                    for (t,) in con.execute("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'co_%'").fetchall():
                        spalten = [c[1] for c in con.execute("PRAGMA table_info(%s)" % t)]
                        if "user" not in spalten or t == "co_user": continue
                        zeilen = con.execute("SELECT * FROM %s WHERE user=? LIMIT 100000" % t, (u[0],)).fetchall()
                        if zeilen:
                            sm.add("bauprotokoll", "%s/CoreProtect/%s.csv" % (ordner, t), _csv(spalten, zeilen), anzahl=len(zeilen), zusatz=" (%d Zeilen)" % len(zeilen))
                con.close()
            except sqlite3.Error as e:
                sm.inhalt.append("%s/CoreProtect: nicht lesbar (%s)" % (ordner, str(e)[:60]))


LOG_VERB_RX = re.compile(r"logged in with entity|lost connection|left the game|joined the game|UUID of player|disconnect|\[/\d+\.\d+\.\d+\.\d+|\bIP\b", re.I)


def _ds_auskunft_zip(s, auswahl=None):
    uuid, name = s["uuid"].lower(), s["name"]
    nodash = uuid.replace("-", "")
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
        sm = _Sammler(zf, auswahl)
        _ds_datenbanken(sm, uuid, nodash, name)
        _ds_server(sm, uuid, nodash, name, time.time() + 120)
        inhalt = sm.inhalt
        titel = {k[0]: k[1] for k in AUSKUNFT_KATEGORIEN}
        zf.writestr("LIESMICH.txt", (
            "Auskunft über deine gespeicherten Daten (Art. 15 DSGVO) – RyvenMC\n"
            "Erstellt: %s · Spieler: %s · UUID: %s\n"
            "Enthaltene Kategorien: %s\n\n"
            "Inhalt:\n  Datenbanken/   Einträge aus unseren Datenbanken (CSV, mit Excel/LibreOffice lesbar)\n"
            "  <Server>/Dateien/   Spielstand, Statistiken, Erfolge und Plugin-Daten (.dat = Minecraft-NBT)\n"
            "  <Server>/Logs-Chat-Befehle.txt, Logs-Verbindungen.txt   Log-Zeilen mit deinem Namen/deiner UUID (wir speichern Logs höchstens 30 Tage)\n"
            "  <Server>/CoreProtect/   Protokoll gesetzter/abgebauter Blöcke, Kisten, Chat und Befehle\n\n"
            "Zwecke, Rechtsgrundlagen, Empfänger und Speicherdauer stehen in unserer Datenschutzerklärung:\n"
            "https://ryvenmc.de/datenschutz\n\nLöschen lassen kannst du alles mit /datenschutz loeschen oder über das Team.\n\n"
            "Dateiliste (in eckigen Klammern die Kategorie):\n" % (time.strftime("%d.%m.%Y %H:%M"), name, uuid,
                                                                  "alle" if auswahl is None else ", ".join(titel[k] for k in AUSKUNFT_KAT_IDS if k in auswahl)) + "\n".join("  " + x for x in inhalt) + "\n"))
    return buf.getvalue(), inhalt


def _mail_conf():
    try:
        c = json.load(open(MAIL_CONF, encoding="utf-8"))
        return c if c.get("host") and c.get("from") else None
    except (OSError, ValueError):
        return None


def _auskunft_log(eintrag):
    os.makedirs(os.path.dirname(AUSKUNFT_LOG), exist_ok=True)
    with open(AUSKUNFT_LOG, "a", encoding="utf-8") as f:
        f.write(json.dumps(eintrag, ensure_ascii=False) + "\n")


AUSKUNFT_AUFBEWAHRUNG = 3 * 365 * 86400      # Nachweis (Art. 5 Abs. 2 DSGVO): Auskunft-Protokoll max. 3 Jahre


def _auskunft_log_pflegen():
    """Auskunft-Protokoll pflegen: Einträge >3 Jahre entfernen; nach erledigter Löschung nur Datum, Versandweg, UUID behalten
    (Name/E-Mail/Rest -> "gelöscht"). Schreibt nur bei Änderung, atomar (Temp-Datei + os.replace), Modus 600."""
    pfad = os.environ.get("AUSKUNFT_LOG_TEST") or AUSKUNFT_LOG
    try:
        zeilen = open(pfad, encoding="utf-8").read().splitlines()
    except OSError:
        return 0
    if not zeilen:
        return 0
    try:
        geloescht = {r["uuid"] for r in rows("SELECT JSON_OBJECT('uuid', uuid) FROM rl_privacy_requests WHERE status='erledigt' AND uuid IS NOT NULL;")}
    except Exception:
        return 0     # ohne DB-Antwort nichts anfassen
    grenze = time.time() - AUSKUNFT_AUFBEWAHRUNG
    neu, geaendert = [], 0
    for z in zeilen:
        try:
            e = json.loads(z)
        except ValueError:
            neu.append(z); continue
        if not isinstance(e, dict):
            neu.append(z); continue
        if (e.get("zeit") or 0) < grenze:
            geaendert += 1; continue
        if e.get("uuid") in geloescht and not e.get("geloescht"):
            e = {"zeit": e.get("zeit"), "modus": e.get("modus"), "uuid": e.get("uuid"),
                 "spieler": "gelöscht", "email": "gelöscht", "von": "gelöscht", "geloescht": True}
            geaendert += 1
            z = json.dumps(e, ensure_ascii=False)
        neu.append(z)
    if not geaendert:
        return 0
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(pfad), prefix=".auskunft-")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write("".join(x + chr(10) for x in neu))
        os.chmod(tmp, 0o600)
        os.replace(tmp, pfad)
    except Exception:
        try: os.unlink(tmp)
        except OSError: pass
        raise
    return geaendert


# ── Auskunft als befristeter Download-Link, per Discord-DM vom Bot (Identität über die /link-Verknüpfung belegt)
AUSKUNFT_WEB = "/opt/ryvenmc-web/site/auskunft"
AUSKUNFT_URL = "https://ryvenmc.de/auskunft/"
AUSKUNFT_TAGE = 7
AUSKUNFT_LINKS = "/var/lib/pxmx/auskunft-links.json"
BOT_CONF = "/opt/ryvenlink-bot/config.json"


def _discord(method, path, body=None):
    tok = json.load(open(BOT_CONF, encoding="utf-8"))["token"]
    req = urllib.request.Request("https://discord.com/api/v10" + path, method=method, data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Authorization": "Bot " + tok, "Content-Type": "application/json", "User-Agent": "RyvenPanel (https://ryvenmc.de, 1)"})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        raise Fail("Discord: %s %s" % (e.code, e.read().decode(errors="replace")[:150]))


def _auskunft_aufraeumen():
    """Abgelaufene Links löschen (läuft jede Minute über mod.py aufzeichnen)."""
    links = _json_load(AUSKUNFT_LINKS, {})
    jetzt, geaendert = int(time.time()), False
    for tok, l in list(links.items()):
        if l.get("ablauf", 0) < jetzt:
            shutil.rmtree(os.path.join(AUSKUNFT_WEB, tok), ignore_errors=True)
            l["geloescht"] = jetzt; links[tok] = l; geaendert = True
            if jetzt - l.get("ablauf", 0) > 90 * 86400: del links[tok]   # Nachweis 90 Tage aufheben
    if os.path.isdir(AUSKUNFT_WEB):   # Reste ohne Eintrag (z. B. abgebrochen) nach 8 Tagen weg
        for d in os.listdir(AUSKUNFT_WEB):
            p = os.path.join(AUSKUNFT_WEB, d)
            if d not in links and jetzt - os.path.getmtime(p) > (AUSKUNFT_TAGE + 1) * 86400:
                shutil.rmtree(p, ignore_errors=True)
    if geaendert:
        _json_save(AUSKUNFT_LINKS, links)


def _auskunft_discord(s, daten, datei, actor):
    link = rows("SELECT JSON_OBJECT('discord', CAST(discord_id AS CHAR)) FROM rl_links WHERE uuid=%s;" % q(s["uuid"]))
    if not link:
        raise Fail("%s hat keinen Discord verknüpft – nutze E-Mail oder Herunterladen." % s["name"])
    did = link[0]["discord"]
    tok = secrets.token_urlsafe(32)
    ordner = os.path.join(AUSKUNFT_WEB, tok)
    os.makedirs(ordner, mode=0o755)
    with open(os.path.join(ordner, datei), "wb") as f:
        f.write(daten)
    os.chmod(os.path.join(ordner, datei), 0o644)
    url = AUSKUNFT_URL + tok + "/" + urllib.parse.quote(datei)
    ablauf = int(time.time()) + AUSKUNFT_TAGE * 86400
    try:
        ch = _discord("POST", "/users/@me/channels", {"recipient_id": did})
        _discord("POST", "/channels/%s/messages" % ch["id"], {"embeds": [{
            "title": "📦 Deine Datenauskunft von RyvenMC",
            "color": 0xA855F7,
            "description": ("Hallo **%s**,\n\ndu hast eine Auskunft über deine bei RyvenMC gespeicherten Daten angefragt (Art. 15 DSGVO).\n\n"
                            "**[⬇️ Daten herunterladen (ZIP, %.1f MB)](%s)**\n\nDer Link ist **bis %s** gültig und nur für dich – bitte nicht weitergeben. "
                            "Eine Erklärung steht in der Datei `LIESMICH.txt`.\n\nZwecke, Rechtsgrundlagen und Speicherdauer: [Datenschutzerklärung](https://ryvenmc.de/datenschutz)\n"
                            "Löschen lassen: ingame `/datenschutz loeschen`")
                           % (s["name"], len(daten) / 1048576, url, time.strftime("%d.%m.%Y %H:%M", time.localtime(ablauf))),
            "footer": {"text": "RyvenMC · Datenschutz"}}]})
    except Fail as e:
        shutil.rmtree(ordner, ignore_errors=True)
        raise Fail("DM an %s nicht möglich (Discord-DMs geschlossen oder Bot blockiert?) – %s" % (s["name"], str(e)[:120]))
    links = _json_load(AUSKUNFT_LINKS, {})
    links[tok] = {"uuid": s["uuid"], "spieler": s["name"], "discord": did, "erstellt": int(time.time()), "ablauf": ablauf, "von": actor, "datei": datei, "groesse": len(daten)}
    _json_save(AUSKUNFT_LINKS, links)
    return ablauf



def ds_auskunft_status(a):
    c = _mail_conf()
    log = []
    try:
        log = [json.loads(z) for z in open(AUSKUNFT_LOG, encoding="utf-8").read().splitlines()[-100:]][::-1]
    except OSError: pass
    return {"mail": bool(c), "absender": c.get("from") if c else None, "verlauf": log}


def _kat_auswahl(a):
    k = a.get("kategorien")
    if k is None: return None
    if not isinstance(k, list) or not k or not all(isinstance(x, str) and x in AUSKUNFT_KAT_IDS for x in k):
        raise Fail("Ungültige Kategorien (erlaubt: %s)." % ", ".join(AUSKUNFT_KAT_IDS))
    return set(k)


def ds_auskunft_kategorien(a):
    s = _ds_spieler(a.get("spieler"))
    uuid = s["uuid"].lower()
    sm = _Sammler(None, None)
    _ds_datenbanken(sm, uuid, uuid.replace("-", ""), s["name"])
    _ds_server(sm, uuid, uuid.replace("-", ""), s["name"], time.time() + 120)
    return {"spieler": s["name"], "uuid": s["uuid"], "kategorien": [
        {"id": i, "titel": t, "beschreibung": b, "anzahl": sm.stat[i][0], "groesse": sm.stat[i][1]}
        for i, t, b in AUSKUNFT_KATEGORIEN if sm.stat[i][0]]}


def ds_auskunft(a):
    auswahl = _kat_auswahl(a)
    modus = str(a.get("modus") or "download")
    s = _ds_spieler(a.get("spieler"))
    email = str(a.get("email") or "").strip()
    if modus == "senden":
        if not EMAIL_RX.match(email): raise Fail("Bitte eine gültige E-Mail-Adresse angeben.")
        c = _mail_conf()
        if not c: raise Fail("Der Mailversand ist noch nicht eingerichtet (%s fehlt) – nutze vorerst „Herunterladen“." % MAIL_CONF)
    daten, inhalt = _ds_auskunft_zip(s, auswahl)
    datei = "RyvenMC-Datenauskunft-%s-%s.zip" % (re.sub(r"[^A-Za-z0-9_]", "", s["name"]), time.strftime("%Y-%m-%d"))
    eintrag = {"zeit": int(time.time()), "von": str(a.get("actor") or "Panel"), "spieler": s["name"], "uuid": s["uuid"], "modus": modus,
               "email": email if modus == "senden" else None, "groesse": len(daten), "dateien": len(inhalt), "sha256": hashlib.sha256(daten).hexdigest(),
               "kategorien": sorted(auswahl, key=AUSKUNFT_KAT_IDS.index) if auswahl is not None else "alle"}
    if modus == "discord":
        ablauf = _auskunft_discord(s, daten, datei, eintrag["von"])
        _auskunft_log(dict(eintrag, ablauf=ablauf))
        return {"gesendet": True, "discord": True, "spieler": s["name"], "groesse": len(daten), "dateien": len(inhalt), "ablauf": ablauf}
    if modus == "senden":
        if len(daten) > AUSKUNFT_MAX_MAIL:
            raise Fail("Die Auskunft ist %.1f MB groß – zu groß für eine Mail. Bitte herunterladen und anders übermitteln." % (len(daten) / 1048576))
        msg = EmailMessage()
        msg["From"] = c["from"]; msg["To"] = email; msg["Reply-To"] = c.get("reply_to") or c["from"]
        msg["Subject"] = "Deine Datenauskunft von RyvenMC (%s)" % s["name"]
        msg.set_content("Hallo %s,\n\ndu hast eine Auskunft über deine bei RyvenMC gespeicherten Daten angefragt (Art. 15 DSGVO).\n"
                        "Im Anhang findest du alle Daten zu deinem Minecraft-Account (UUID %s) als ZIP-Datei – eine Erklärung steht in LIESMICH.txt.\n\n"
                        "Zwecke, Rechtsgrundlagen, Empfänger und Speicherdauer findest du in unserer Datenschutzerklärung: https://ryvenmc.de/datenschutz\n"
                        "Wenn du deine Daten löschen lassen möchtest, antworte einfach auf diese Mail oder nutze ingame /datenschutz loeschen.\n\n"
                        "Viele Grüße\nDein RyvenMC-Team\n" % (s["name"], s["uuid"]))
        msg.add_attachment(daten, maintype="application", subtype="zip", filename=datei)
        try:
            if str(c.get("tls", "starttls")).lower() == "ssl":
                smtp = smtplib.SMTP_SSL(c["host"], int(c.get("port", 465)), timeout=30)
            else:
                smtp = smtplib.SMTP(c["host"], int(c.get("port", 587)), timeout=30); smtp.starttls()
            if c.get("user"): smtp.login(c["user"], c.get("password", ""))
            smtp.send_message(msg); smtp.quit()
        except (smtplib.SMTPException, OSError) as e:
            _auskunft_log(dict(eintrag, fehler=str(e)[:200]))
            raise Fail("Mailversand fehlgeschlagen: %s" % str(e)[:200])
        _auskunft_log(eintrag)
        return {"gesendet": True, "email": email, "spieler": s["name"], "groesse": len(daten), "dateien": len(inhalt)}
    _auskunft_log(eintrag)
    return {"dateiname": datei, "zip": base64.b64encode(daten).decode(), "groesse": len(daten), "dateien": len(inhalt), "spieler": s["name"]}



# ───────────────────────────── KI-Support ─────────────────────────────
KI_DIR = os.environ.get("RP_KI_DIR", "/opt/ryvenlink-bot")
KI_CONTAINER = os.environ.get("RP_KI_CONTAINER", "ryvenlink-bot")
KI_NAME_RX = re.compile(r"[A-Za-z0-9_-]{1,60}\.md")
KI_MAX = 100 * 1024
KI_LIMITS = {"cooldown_seconds": (0, 3600), "daily_limit": (1, 1000)}


def ki_status():
    """(läuft, API-Key gesetzt) – Key wird nie ausgegeben."""
    try:
        r = subprocess.run(["docker", "inspect", KI_CONTAINER, "--format", "{{.State.Running}}|{{range .Config.Env}}{{println .}}{{end}}"],
                           capture_output=True, timeout=15)
        out = r.stdout.decode(errors="replace")
        head, _, env = out.partition("|")
        return head == "true", any(l.startswith("ANTHROPIC_API_KEY=") and len(l) > 20 for l in env.splitlines())
    except (OSError, subprocess.TimeoutExpired):
        return False, False


def ki_overview(a):
    r = rows("SELECT JSON_OBJECT("
             "'heute', (SELECT COUNT(*) FROM rl_support WHERE created_at > NOW() - INTERVAL 1 DAY), "
             "'tage7', (SELECT COUNT(*) FROM rl_support WHERE created_at > NOW() - INTERVAL 7 DAY), "
             "'gesamt', (SELECT COUNT(*) FROM rl_support), "
             "'eskaliert7', (SELECT COUNT(*) FROM rl_support WHERE escalated > 0 AND created_at > NOW() - INTERVAL 7 DAY), "
             "'offen', (SELECT COUNT(*) FROM rl_support WHERE escalated = 1), "
             "'fehler7', (SELECT COUNT(*) FROM rl_support WHERE status = 'failed' AND created_at > NOW() - INTERVAL 7 DAY), "
             "'wartend', (SELECT COUNT(*) FROM rl_support WHERE status IN ('new','working')), "
             "'discord7', (SELECT COUNT(*) FROM rl_support WHERE source='discord' AND created_at > NOW() - INTERVAL 7 DAY), "
             "'ingame7', (SELECT COUNT(*) FROM rl_support WHERE source='ingame' AND created_at > NOW() - INTERVAL 7 DAY))")
    d = r[0] if r else {}
    d["eskalationsrate"] = round(100.0 * int(d.get("eskaliert7") or 0) / int(d["tage7"]), 1) if int(d.get("tage7") or 0) else 0.0
    laeuft, key = ki_status()
    d["laeuft"] = laeuft
    d["aktiv"] = laeuft and key
    d["api_key"] = key
    return d


def ki_verlauf(a):
    where = []
    f = str(a.get("filter") or "alle")
    if f == "eskaliert":
        where.append("escalated > 0")
    elif f == "offen":
        where.append("escalated = 1")
    elif f == "erledigt":
        where.append("escalated = 2")
    src = str(a.get("source") or "")
    if src in ("ingame", "discord"):
        where.append("source=" + q(src))
    if a.get("q"):
        s = like(a["q"])
        where.append("(user_name LIKE %s OR question LIKE %s OR answer LIKE %s)" % (s, s, s))
    try:
        off = max(0, int(a.get("offset") or 0))
    except (TypeError, ValueError):
        off = 0
    lim = limit(a, 50, 200)
    w = ("WHERE " + " AND ".join(where)) if where else ""
    items = rows("SELECT JSON_OBJECT('id', id, 'source', source, 'user', user_name, 'question', question, 'answer', answer, 'status', status, "
                 "'escalated', escalated, 'created', created_at, 'answered', answered_at) FROM rl_support %s ORDER BY id DESC LIMIT %d OFFSET %d" % (w, lim + 1, off))
    return {"items": items[:lim], "mehr": len(items) > lim, "offset": off, "limit": lim}


def ki_set_status(a):
    try:
        i = int(a.get("id"))
    except (TypeError, ValueError):
        raise Fail("Ungültige ID.")
    if str(a.get("status") or "done") not in ("done", "open"):
        raise Fail("Status: done oder open.")
    done = str(a.get("status") or "done") == "done"
    execute("UPDATE rl_support SET escalated=%d WHERE id=%d AND escalated IN (1,2)" % (2 if done else 1, i))
    return {"id": i, "erledigt": done}


def ki_wissen_pfad(name):
    name = str(name or "")
    if not KI_NAME_RX.fullmatch(name):
        raise Fail("Ungültiger Dateiname (nur A-Z a-z 0-9 _ - und .md).")
    return os.path.join(KI_DIR, "knowledge", name)


def ki_wissen(a):
    d = os.path.join(KI_DIR, "knowledge")
    if not os.path.isdir(d):
        raise Fail("Wissensordner nicht gefunden.")
    out = []
    for n in sorted(os.listdir(d)):
        p = os.path.join(d, n)
        if KI_NAME_RX.fullmatch(n) and os.path.isfile(p):
            st = os.stat(p)
            out.append({"name": n, "groesse": st.st_size, "geaendert": int(st.st_mtime), "auto": n == "auto.md"})
    return {"dateien": out}


def ki_wissen_lesen(a):
    p = ki_wissen_pfad(a.get("name"))
    if not os.path.isfile(p):
        raise Fail("Datei nicht gefunden.")
    return {"name": os.path.basename(p), "text": open(p, encoding="utf-8").read(), "auto": os.path.basename(p) == "auto.md"}


def ki_wissen_speichern(a):
    p = ki_wissen_pfad(a.get("name"))
    text = str(a.get("text") if a.get("text") is not None else "")
    if len(text.encode("utf-8")) > KI_MAX:
        raise Fail("Datei zu groß (max. 100 KB).")
    if os.path.basename(p) == "auto.md":
        raise Fail("auto.md wird täglich automatisch erzeugt und überschrieben – bitte core.md oder faq.md bearbeiten.")
    bak = None
    if os.path.isfile(p):
        bd = os.path.join(KI_DIR, "knowledge-backups")
        os.makedirs(bd, mode=0o755, exist_ok=True)
        bak = os.path.join(bd, "%s.%s" % (os.path.basename(p), time.strftime("%Y%m%d-%H%M%S-") + "%06d" % datetime.datetime.now().microsecond))
        shutil.copy2(p, bak)
        old = sorted(glob.glob(os.path.join(bd, os.path.basename(p) + ".*")))
        for o in old[:-30]:
            os.remove(o)
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(p), prefix=".tmp-", suffix=".part")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        os.chmod(tmp, 0o644)
        os.replace(tmp, p)
    except BaseException:
        if os.path.exists(tmp):
            os.remove(tmp)
        raise
    return {"name": os.path.basename(p), "gesichert": bool(bak),
            "hinweis": "Die KI lädt das Wissen automatisch stündlich neu (spätestens in 60 Minuten aktiv)."}


def ki_cfg_path():
    return os.path.join(KI_DIR, "config.json")


def ki_config(a):
    c = json.load(open(ki_cfg_path(), encoding="utf-8")).get("ai") or {}
    _, api_key_set = ki_status()
    return {"cooldown_seconds": int(c.get("cooldown_seconds", 15)), "daily_limit": int(c.get("daily_limit", 40)),
            "api_key_set": api_key_set, "grenzen": {k: list(v) for k, v in KI_LIMITS.items()}}


def ki_config_set(a):
    new = {}
    for k, (lo, hi) in KI_LIMITS.items():
        if a.get(k) is None:
            continue
        try:
            v = int(a[k])
        except (TypeError, ValueError):
            raise Fail("%s: Zahl erwartet." % k)
        if not lo <= v <= hi:
            raise Fail("%s muss zwischen %d und %d liegen." % (k, lo, hi))
        new[k] = v
    if not new:
        raise Fail("Nichts zu ändern.")
    p = ki_cfg_path()
    cfg = json.load(open(p, encoding="utf-8"))
    if not isinstance(cfg.get("ai", {}), dict):
        raise Fail("config.json: Abschnitt ai ist kein Objekt.")
    shutil.copy2(p, p + ".bak-panel")
    os.chmod(p + ".bak-panel", 0o600)
    cfg.setdefault("ai", {}).update(new)
    out = json.dumps(cfg, indent=2, ensure_ascii=False)   # erst serialisieren, dann schreiben
    with open(p, "w", encoding="utf-8") as f:   # an Ort und Stelle schreiben: Besitzer/Rechte (Bot-Benutzer, 0400) bleiben erhalten
        f.write(out)
    return dict(ki_config({}), neustart_noetig=True,
                hinweis="Der Bot liest die Einstellungen nur beim Start – der Container %s muss neu gestartet werden." % KI_CONTAINER)


def ki_api_key_set(a):
    api_key = a.get("api_key")
    if not isinstance(api_key, str):
        raise Fail("API-Key muss als Text übergeben werden.")
    if len(api_key) > 4096 or any(c in api_key for c in ("\0", "\r", "\n")):
        raise Fail("API-Key ist ungültig oder zu lang.")
    if api_key and len(api_key) < 20:
        raise Fail("API-Key ist zu kurz.")

    r = subprocess.run(["docker", "inspect", "--format", "{{json .Config.Labels}}", KI_CONTAINER],
                       capture_output=True, timeout=15)
    if r.returncode != 0:
        err = r.stderr.decode(errors="replace").strip()
        raise Fail("Docker-Inspect für den KI-Container '%s' fehlgeschlagen: %s" %
                   (KI_CONTAINER, err[:160] if err else "Container nicht gefunden oder keine Docker-Berechtigung."))
    try:
        labels = json.loads(r.stdout.decode(errors="replace") or "{}") or {}
    except ValueError:
        raise Fail("Docker lieferte ungültige Container-Metadaten.")
    workdir = labels.get("com.docker.compose.project.working_dir")
    config_files = labels.get("com.docker.compose.project.config_files")
    project = labels.get("com.docker.compose.project")
    service = labels.get("com.docker.compose.service")
    if not workdir or not config_files or not project or not service:
        raise Fail("Der KI-Container muss über Docker Compose verwaltet werden; es wurde nichts geändert.")
    if not os.path.isdir(workdir) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", project) or \
       not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", service):
        raise Fail("Docker-Compose-Projekt oder Service-Name ist ungültig.")

    compose_files = []
    for path in config_files.split(","):
        path = path.strip()
        if not path:
            continue
        if not os.path.isabs(path):
            path = os.path.join(workdir, path)
        if not os.path.isfile(path):
            raise Fail("Docker-Compose-Datei nicht gefunden: %s" % path)
        compose_files.append(os.path.abspath(path))
    if not compose_files:
        raise Fail("Docker meldet keine Docker-Compose-Datei für den KI-Container.")

    override = os.path.join(workdir, "docker-compose.pxmx-ai.override.yml")
    marker = "# PXMX-managed AI key"
    old = None
    if os.path.exists(override):
        with open(override, "r", encoding="utf-8") as f:
            old = f.read()
        if not old.startswith(marker):
            raise Fail("Die Datei docker-compose.pxmx-ai.override.yml existiert bereits und wird nicht überschrieben.")

    yaml_key = json.dumps(api_key, ensure_ascii=True).replace("$", "$$")
    content = "%s\nservices:\n  %s:\n    environment:\n      ANTHROPIC_API_KEY: %s\n" % (
        marker, service, yaml_key)
    if old == content:
        return {"api_key_set": bool(api_key), "geaendert": False, "neustart_noetig": False}

    fd, tmp = tempfile.mkstemp(prefix=".pxmx-ai-", dir=workdir)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
        os.chmod(tmp, 0o600)
        os.replace(tmp, override)
    except BaseException:
        if os.path.exists(tmp):
            os.remove(tmp)
        raise

    compose = shutil.which("docker-compose")
    command = [compose] if compose else ["docker", "compose"]
    command.extend(["-p", project])
    for path in compose_files:
        command.extend(["-f", path])
    command.extend(["-f", override, "up", "-d", "--no-deps", service])
    try:
        result = subprocess.run(command, cwd=workdir, capture_output=True, timeout=180)
    except (OSError, subprocess.TimeoutExpired) as e:
        if old is None:
            os.remove(override)
        else:
            fd, restore = tempfile.mkstemp(prefix=".pxmx-ai-restore-", dir=workdir)
            with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
                f.write(old)
            os.chmod(restore, 0o600)
            os.replace(restore, override)
        raise Fail("Docker Compose konnte nicht ausgeführt werden: %s" % str(e)[:180])
    if result.returncode != 0:
        if old is None:
            os.remove(override)
        else:
            fd, restore = tempfile.mkstemp(prefix=".pxmx-ai-restore-", dir=workdir)
            with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
                f.write(old)
            os.chmod(restore, 0o600)
            os.replace(restore, override)
        err = (result.stderr or result.stdout).decode(errors="replace").strip()
        if api_key:
            err = err.replace(api_key, "[redacted]")
            err = err.replace(api_key.replace("$", "$$"), "[redacted]")
        raise Fail("KI-Container konnte nicht mit der neuen Einstellung gestartet werden: %s" % err[:180])
    return {"api_key_set": bool(api_key), "geaendert": True, "neustart_noetig": False}


CMDS = {"overview": overview, "reports": reports, "report": report, "report_set": report_set, "punishments": punishments,
        "links": links, "player": player, "templates": templates, "act": act,
        "verbindungen": verbindungen, "verbindung_ok": verbindung_ok,
        "nutzer": nutzer, "nutzer_detail": nutzer_detail, "rang_detail": rang_detail, "nutzer_log": nutzer_log, "nutzer_act": nutzer_act,
        "plugins": plugins, "datenbanken": datenbanken, "plugin_doku": plugin_doku, "ds_status": ds_status, "aufzeichnen": aufzeichnen,
        "waechter_bericht": waechter_bericht, "ds_antraege": ds_antraege, "ds_vorschau": ds_vorschau, "ds_loeschen": ds_loeschen, "ds_ablehnen": ds_ablehnen,
        "ds_auskunft_status": ds_auskunft_status, "ds_auskunft": ds_auskunft, "ds_auskunft_kategorien": ds_auskunft_kategorien,
        "ki_overview": ki_overview, "ki_verlauf": ki_verlauf, "ki_set_status": ki_set_status, "ki_wissen": ki_wissen, "ki_wissen_lesen": ki_wissen_lesen,
        "ki_wissen_speichern": ki_wissen_speichern, "ki_config": ki_config, "ki_config_set": ki_config_set,
        "ki_api_key_set": ki_api_key_set}


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in CMDS:
        print(json.dumps({"status": "error", "error": "Befehle: " + ", ".join(CMDS)}))
        return
    try:
        if len(sys.argv) > 2 and sys.argv[2] == "-":   # große Argumente: JSON über stdin (Gateway)
            args = json.loads(sys.stdin.buffer.read().decode() or "{}")
        else:
            args = json.loads(base64.b64decode(sys.argv[2]).decode()) if len(sys.argv) > 2 and sys.argv[2] else {}
        print(json.dumps({"status": "ok", "data": CMDS[sys.argv[1]](args)}, ensure_ascii=False, default=str))
    except Fail as e:
        print(json.dumps({"status": "error", "error": str(e)}, ensure_ascii=False))
    except Exception as e:
        print(json.dumps({"status": "error", "error": "%s: %s" % (type(e).__name__, str(e)[:300])}, ensure_ascii=False))


if __name__ == "__main__":
    main()
