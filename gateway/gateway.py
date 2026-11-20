#!/usr/bin/env python3
"""
RyvenPanel Team-Gateway

* Login: username + password. Accounts are created by the owner and stored only on this server
  (/var/lib/pxmx/gateway.db, passwords as salted PBKDF2-SHA256 hashes).
* Rights: either the LuckPerms rank of a linked Minecraft player (incl. inheritance and temporary groups)
  or a fixed rank chosen by the owner – mapped to panel permissions configured by the owner.
* IP binding: an account is bound to the IP of its first login; other IPs are rejected and queued for the owner.
* Proxy: only permitted Crafty API calls are forwarded (with the owner's Crafty token, never handed out).

Owner administration happens locally via SSH:  python3 gateway.py admin <command> [base64-json | - (stdin)]
"""
import base64, hashlib, hmac, http.server, ipaddress, json, os, re, secrets, socketserver, sqlite3, ssl, subprocess, sys, threading, time
import urllib.error, urllib.request

CONF = "/etc/pxmx/gateway.json"
DB = "/var/lib/pxmx/gateway.db"
VERSION = "2.22"
SCHEMA = 3

PERMS = [
    ("servers.view", "Server & Status sehen"),
    ("console.view", "Konsole lesen"),
    ("console.command", "Konsolenbefehle senden (mit Sperrliste)"),
    ("console.command.unrestricted", "Alle Konsolenbefehle (ohne Sperrliste)"),
    ("store.fulfill", "Webshop-Pakete ausliefern (nur feste Produktliste)"),
    ("server.control", "Server starten / stoppen / neu starten"),
    ("server.kill", "Server hart beenden (Kill)"),
    ("server.backup", "Backups starten"),
    ("files.view", "Server-Dateien ansehen"),
    ("files.edit", "Server-Dateien bearbeiten / hochladen / löschen"),
    ("economy.view", "Wirtschaft ansehen (Geldfluss, Shop, Crates)"),
    ("economy.edit", "Shop-Preise ändern / Items hinzufügen"),
    ("moderation.view", "Moderation ansehen (Reports, Strafen, Verify)"),
    ("moderation.reports", "Reports bearbeiten (übernehmen / erledigen / ablehnen)"),
    ("moderation.punish", "Verwarnen, Kicken, Muten / Entmuten"),
    ("moderation.ban", "Bannen / Entbannen"),
    ("datenschutz.view", "Datenschutz: ausgehende Verbindungen live sehen (Wächter)"),
    ("datenschutz.edit", "Datenschutz: Verbindungen als geprüft markieren"),
    ("datenschutz.loeschen", "Datenschutz: Spielerdaten löschen (DSGVO) – Anträge freigeben/ablehnen, Löschung direkt starten"),
    ("datenschutz.auskunft", "Datenschutz: Datenauskunft (Art. 15) erstellen – herunterladen oder per E-Mail an den Spieler senden"),
    ("netzwerk.view", "Netzwerk-Monitor: Ping, Paketverlust, RX/TX, Ausfälle und Export (Nachweis für den Hoster)"),
    ("ki.view", "KI-Support ansehen (Statistik, Verlauf, Wissen, Einstellungen)"),
    ("ki.edit", "KI-Support bearbeiten (Eskalationen erledigen, Wissen, Einstellungen)"),
    ("users.view", "Nutzer & Ränge ansehen (Spieler, Ränge, Money, Coins, Protokoll)"),
    ("users.economy", "Money und Coins von Spielern ändern"),
    ("users.ranks", "Ränge und Permissions von Spielern und Rängen ändern (LuckPerms)"),
    ("finanzen.view", "Finanzen ansehen (Buchungen, Übersicht, Belege, Export)"),
    ("finanzen.edit", "Finanzen bearbeiten (buchen, stornieren, Kategorien)"),
    ("team.manage", "Team & Zugänge im Web-Panel: Accounts, Passwörter, IP-Freigaben, Rechte pro Rang"),
]
ECO_PY = "/opt/pxmx/eco.py"
MOD_PY = "/opt/pxmx/mod.py"
NETMON_PY = "/opt/pxmx/netmon.py"
FINANZEN_PY = "/opt/pxmx/finanzen.py"
FINANZEN_CMDS = {"uebersicht": "finanzen.view", "liste": "finanzen.view", "beleg": "finanzen.view", "kategorien": "finanzen.view", "export": "finanzen.view",
                 "buchen": "finanzen.edit", "stornieren": "finanzen.edit", "kategorie_neu": "finanzen.edit", "wiederkehrend_ausfuehren": "finanzen.edit"}
NETMON_CMDS = ("status", "verlauf", "ereignisse", "export", "pruefen")
MOD_VIEW = ("overview", "reports", "report", "punishments", "links", "player", "templates")
MOD_ACT = {"warn": "moderation.punish", "kick": "moderation.punish", "mute": "moderation.punish", "unmute": "moderation.punish",
           "ban": "moderation.ban", "ipban": "moderation.ban", "unban": "moderation.ban"}
DIST = "/opt/pxmx/dist"  # PXMX releases (PXMX.exe + version.json)
ECO_CMDS = {"summary": "economy.view", "items": "economy.view", "setprice": "economy.edit", "additem": "economy.edit", "reload": "economy.edit", "settax": "economy.edit"}
PERM_KEYS = [p[0] for p in PERMS]
API_PREFIX = "rpk_"                                           # API-Schlüssel für Folgeprojekte
API_PERMS = [p for p in PERM_KEYS if p not in ("team.manage",)]   # Team-Verwaltung nie per Schlüssel
API_NAME_RX = re.compile(r"^[a-z0-9_-]{3,32}$")

DEFAULT_BLOCKED = [
    "op", "deop", "stop", "restart", "reload", "rl", "lp", "luckperms", "lpb", "lpv", "perm", "perms", "pex",
    "eco", "economy", "points", "p", "give", "giveall", "i", "item", "whitelist", "ban-ip", "pardon-ip", "sudo",
    "plugman", "execute", "save-off", "kill", "killall", "butcher", "worldedit", "we", "/", "//", "sk", "skript",
    "gamemode", "gm", "gmc", "gms", "fly", "god", "vanish", "v", "money", "pay", "mobcoins", "crate", "key",
]
SHOP_PRODUCTS = {
    "coins_1000": ("coins", 1000),
    "coins_2750": ("coins", 2750),
    "coins_6000": ("coins", 6000),
    "coins_13000": ("coins", 13000),
    "coins_30000": ("coins", 30000),
    "rank_gold": ("rank", "gold"),
    "rank_emerald": ("rank", "emerald"),
    "rank_diamond": ("rank", "diamond"),
    "rank_netherite": ("rank", "netherite"),
    "rank_obsidian": ("rank", "obsidian"),
}
SHOP_ORDER_RX = re.compile(r"^[0-9a-f]{32}$")
SHOP_PLAYER_RX = re.compile(r"^\.?[A-Za-z0-9_]{3,16}$")

DEFAULT_ROLES = {
    "owner": PERM_KEYS[:],
    "admin": ["servers.view", "console.view"],
}

UUID_RX = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
SID_RX = re.compile(r"^/api/v2/servers/([0-9a-f-]{36})(/.*)?$")
USER_RX = re.compile(r"^[a-z0-9_.\-]{3,32}$")
MC_RX = re.compile(r"^[A-Za-z0-9_]{3,16}$")
ROLE_RX = re.compile(r"^[a-z0-9_\-]{1,36}$")

PW_ITER = 310000
MAX_FAILS = 5          # wrong passwords before the account is locked
LOCK_SECONDS = 900     # lock duration

lock = threading.RLock()
_conf = None
_conf_mtime = 0
login_hits = {}


# ───────────────────────────── config / db ─────────────────────────────
def conf():
    global _conf, _conf_mtime
    with lock:
        try:
            m = os.path.getmtime(CONF)
        except OSError:
            m = 0
        if _conf is None or m != _conf_mtime:
            try:
                with open(CONF, encoding="utf-8") as f:
                    _conf = json.load(f)
            except (OSError, ValueError):
                _conf = {}
            _conf_mtime = m
            _conf.setdefault("port", 8444)
            _conf.setdefault("roles", DEFAULT_ROLES)
            _conf.setdefault("blocked_commands", DEFAULT_BLOCKED)
            _conf.setdefault("session_hours", 12)
            _conf.setdefault("crafty_url", "https://127.0.0.1:8443")
            _conf.pop("ms_client_id", None)
        return _conf


def save_conf(c):
    global _conf
    with lock:
        c = dict(c)
        c.pop("ms_client_id", None)
        tmp = CONF + ".tmp"
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(c, f, indent=2, ensure_ascii=False)
        os.replace(tmp, CONF)
        _conf = None


def db():
    con = sqlite3.connect(DB, timeout=15)
    con.row_factory = sqlite3.Row
    return con


def init_db():
    os.makedirs(os.path.dirname(DB), exist_ok=True)
    with db() as con:
        ver = con.execute("pragma user_version").fetchone()[0]
        if ver < 2:
            # v1 used Microsoft/Minecraft logins keyed by UUID – those accounts and sessions are obsolete.
            con.executescript("drop table if exists accounts; drop table if exists sessions; drop table if exists ip_requests;")
        con.executescript("""
        create table if not exists users (username text primary key, pw text, mc_name text default '', mc_uuid text default '',
            role text default '', ips text default '[]', frozen int default 0, must_change int default 1, fails int default 0,
            locked_until real default 0, created real, last_login real default 0, last_ip text default '', rank text default '',
            note text default '');
        create table if not exists ip_requests (id integer primary key autoincrement, username text, ip text, time real,
            status text default 'pending');
        create table if not exists sessions (token_hash text primary key, username text, rank text, ip text, perms text,
            created real, expires real, last_seen real);
        create table if not exists audit (id integer primary key autoincrement, time real, uuid text, name text, ip text,
            action text, detail text, result text);
        create table if not exists api_keys (name text primary key, token_hash text unique, prefix text, perms text default '[]',
            ips text default '[]', created real, created_by text, expires real default 0, last_used real default 0,
            last_ip text default '', uses int default 0, revoked int default 0, note text default '');
        create table if not exists shop_fulfillments (order_id text primary key, product_id text not null, player text not null,
            status text not null, created real not null, updated real not null, detail text default '');
        """)
        con.execute("pragma user_version=%d" % SCHEMA)
        for col, typ in (("totp_secret", "text default ''"), ("totp_on", "int default 0"), ("totp_last", "int default 0"),
                         ("recovery", "text default '[]'")):
            try:
                con.execute("alter table users add column %s %s" % (col, typ))
            except sqlite3.OperationalError:
                pass   # exists
        # API-Schlüssel: Rechte (= Scopes) explizit – nie team.manage, nur bekannte Rechte; bestehende Schlüssel behalten den Rest
        for k in con.execute("select name, perms from api_keys").fetchall():
            try:
                old = json.loads(k["perms"] or "[]")
            except ValueError:
                old = []
            new = [p for p in old if p in API_PERMS]
            if new != old:
                con.execute("update api_keys set perms=? where name=?", (json.dumps(new), k["name"]))
                audit("system", "-", "API-Schlüssel migriert", "%s: %s -> %s" % (k["name"], ",".join(old), ",".join(new)), con=con)
    try:
        os.chmod(DB, 0o600)
    except OSError:
        pass


def audit(user, ip, action, detail="", result="ok", con=None):
    """Writes an audit entry. Pass the open connection when called inside a write transaction
    (a second connection would wait for that transaction's lock)."""
    def write(c):
        c.execute("insert into audit (time, uuid, name, ip, action, detail, result) values (?,?,?,?,?,?,?)",
                  (time.time(), user or "", user or "", ip, action, (detail or "")[:500], result))
        c.execute("delete from audit where id < (select max(id) - 20000 from audit)")
    try:
        if con is not None:
            write(con)
        else:
            with db() as c:
                write(c)
    except sqlite3.Error:
        pass


def thash(token):
    return hashlib.sha256(token.encode()).hexdigest()


# ───────────────────────────── passwords ─────────────────────────────
def hash_pw(pw):
    salt = secrets.token_bytes(16)
    h = hashlib.pbkdf2_hmac("sha256", pw.encode("utf-8"), salt, PW_ITER)
    return "pbkdf2_sha256$%d$%s$%s" % (PW_ITER, salt.hex(), h.hex())


def check_pw(pw, stored):
    try:
        algo, it, salt, h = (stored or "").split("$")
        if algo != "pbkdf2_sha256":
            return False
        calc = hashlib.pbkdf2_hmac("sha256", pw.encode("utf-8"), bytes.fromhex(salt), int(it))
        return hmac.compare_digest(calc.hex(), h)
    except (ValueError, TypeError):
        return False


_DUMMY = None


def dummy_hash():
    """Hash used for unknown usernames so the response time does not reveal whether an account exists."""
    global _DUMMY
    if _DUMMY is None:
        _DUMMY = hash_pw(secrets.token_hex(8))
    return _DUMMY


def pw_problem(pw):
    if not isinstance(pw, str) or len(pw) < 8:
        return "Das Passwort muss mindestens 8 Zeichen lang sein."
    if len(pw) > 200:
        return "Das Passwort ist zu lang."
    if pw.lower() == pw or pw.upper() == pw or not re.search(r"\d", pw):
        if len(pw) < 14:
            return "Das Passwort braucht Groß- und Kleinbuchstaben und eine Zahl (oder mindestens 14 Zeichen)."
    return None


# ───────────────────────────── TOTP 2FA (RFC 6238) ─────────────────────────────
tfa_tickets = {}       # ticket -> {"username", "ip", "exp", "fails"}  (in memory, 5 minutes)
TFA_TICKET_SECONDS = 300
TFA_MAX_FAILS = 5


def b32_secret():
    return base64.b32encode(secrets.token_bytes(20)).decode().rstrip("=")


def totp_at(secret, step):
    key = base64.b32decode(secret + "=" * (-len(secret) % 8), casefold=True)
    mac = hmac.new(key, step.to_bytes(8, "big"), hashlib.sha1).digest()
    o = mac[-1] & 15
    return "%06d" % ((int.from_bytes(mac[o:o + 4], "big") & 0x7fffffff) % 1000000)


def totp_verify(secret, code, last_step=0, now=None):
    """Returns the matched time step (> last_step, ±1 step tolerance) or None. A step can be used only once."""
    code = re.sub(r"\s", "", str(code or ""))
    if not re.match(r"^\d{6}$", code):
        return None
    cur = int((time.time() if now is None else now) // 30)
    for st in (cur - 1, cur, cur + 1):
        if st > (last_step or 0) and hmac.compare_digest(totp_at(secret, st), code):
            return st
    return None


def new_recovery_codes():
    codes = ["%s-%s" % (secrets.token_hex(3), secrets.token_hex(3)) for _ in range(10)]
    return codes, [thash(c) for c in codes]


def tfa_required(rank):
    return bool(rank) and rank in (conf().get("tfa_required_ranks") or [])


def tfa_state(username):
    with db() as con:
        u = con.execute("select totp_on from users where username=?", (username,)).fetchone()
    return bool(u and u["totp_on"])


def tfa_setup_blocked(s):
    """True if the rank requires 2FA and the account has not enrolled yet (only the 2FA setup is reachable then)."""
    if isinstance(s, dict) and s.get("api"):
        return False
    return tfa_required(s["rank"]) and not tfa_state(s["username"])


# ───────────────────────────── LuckPerms ─────────────────────────────
def lp_settings():
    path = conf().get("luckperms_config")
    with open(path, encoding="utf-8") as f:
        text = f.read()
    m = re.search(r"^data:[^\n]*\n((?:[ \t#][^\n]*\n|[ \t]*\n)*)", text, re.M)
    sec = m.group(1) if m else ""

    def v(key):
        mm = re.search(r"^[ \t]+" + re.escape(key) + r":[ \t]*['\"]?([^'\"\n#]*)['\"]?", sec, re.M)
        return mm.group(1).strip() if mm else ""

    host = v("address").split(":")[0]
    names = subprocess.run(["docker", "ps", "--format", "{{.Names}}"], capture_output=True, text=True, timeout=20).stdout.split()
    container = host if host in names else next((n for n in names if re.search("mariadb|mysql", n, re.I)), None)
    if not container:
        raise RuntimeError("Datenbank-Container nicht gefunden")
    return container, v("username"), v("password"), v("database"), (v("table-prefix") or "luckperms_")


def lp_query(sql):
    container, user, pw, database, prefix = lp_settings()
    sql = sql.replace("{p}", prefix)
    r = subprocess.run(["docker", "exec", "-i", "-e", "MYSQL_PWD=" + pw, container, "sh", "-c",
                        'exec $(command -v mariadb || command -v mysql) -u"$0" -D "$1" -N -B', user, database],
                       input=sql, capture_output=True, text=True, timeout=30)
    if r.returncode != 0:
        raise RuntimeError("LuckPerms-Abfrage fehlgeschlagen: " + r.stderr.strip()[-200:])
    return [line.split("\t") for line in r.stdout.splitlines() if line]


def lp_groups():
    rows = lp_query("select 'R', name, '' from {p}groups;\n"
                    "select 'W', name, substring(permission,8) from {p}group_permissions where permission like 'weight.%';")
    groups = {}
    for r in rows:
        kind, name, val = (r + ["", "", ""])[:3]
        groups.setdefault(name, 0)
        if kind == "W":
            try:
                groups[name] = int(val)
            except ValueError:
                pass
    return groups


def lp_player(name):
    """Minecraft name -> (uuid, name) from the LuckPerms player table (player must have joined once)."""
    if not MC_RX.match(name or ""):
        return None
    rows = lp_query("select uuid, username from {p}players where username='%s' limit 1;" % name.lower())
    if not rows or not UUID_RX.match(rows[0][0]):
        return None
    return rows[0][0], (rows[0][1] if len(rows[0]) > 1 else name)


def lp_rank(uuid):
    """Returns (rank, perms) – rank = highest-weight group of the player that has a panel role."""
    if not UUID_RX.match(uuid):
        return None, []
    now = int(time.time())
    rows = lp_query(
        "select 'P', primary_group, 0 from {p}players where uuid='%s';\n"
        "select 'G', substring(permission,7), expiry from {p}user_permissions where uuid='%s' and permission like 'group.%%' "
        "and value=1 and server in ('global','') and world in ('global','');\n"
        "select 'W', name, substring(permission,8) from {p}group_permissions where permission like 'weight.%%';\n"
        "select 'I', name, substring(permission,7) from {p}group_permissions where permission like 'group.%%' and value=1;"
        % (uuid, uuid))
    mine, weights, parents = {"default"}, {}, {}
    for r in rows:
        r = (r + ["", "", ""])[:3]
        if r[0] == "P" and r[1]:
            mine.add(r[1])
        elif r[0] == "G":
            try:
                exp = int(r[2] or 0)
            except ValueError:
                exp = 0
            if exp == 0 or exp > now:
                mine.add(r[1])
        elif r[0] == "W":
            try:
                weights[r[1]] = int(r[2])
            except ValueError:
                pass
        elif r[0] == "I":
            parents.setdefault(r[1], set()).add(r[2])
    todo, seen = list(mine), set()
    while todo:
        g = todo.pop()
        if g in seen:
            continue
        seen.add(g)
        todo.extend(parents.get(g, ()))
    roles = conf().get("roles", {})
    best = None
    for g in seen:
        if roles.get(g) and (best is None or weights.get(g, 0) > weights.get(best, 0)):
            best = g
    if best is None:
        return None, []
    return best, [p for p in roles[best] if p in PERM_KEYS]


def resolve_rank(u):
    """Rank + perms of an account: LuckPerms rank of the linked player, otherwise the fixed rank."""
    if u["mc_uuid"]:
        return lp_rank(u["mc_uuid"])
    role = u["role"] or ""
    ps = conf().get("roles", {}).get(role)
    if not ps:
        return None, []
    return role, [p for p in ps if p in PERM_KEYS]


def refresh_session(token_hash, username, old_rank, old_perms):
    """Re-reads rank + rights of a running session (rights edited by the owner, LuckPerms rank changed) and stores them.
    Falls back to the stored values if LuckPerms cannot be read right now."""
    try:
        with db() as con:
            u = con.execute("select * from users where username=?", (username,)).fetchone()
        if u is None:
            return old_rank, old_perms
        rank, perms = resolve_rank(u)
    except Exception:
        return old_rank, old_perms
    rank = rank or ""
    if rank != old_rank or perms != old_perms:
        with db() as con:
            con.execute("update sessions set rank=?, perms=? where token_hash=?", (rank, json.dumps(perms), token_hash))
            if rank:
                con.execute("update users set rank=? where username=?", (rank, username))
    return rank, perms


# ───────────────────────────── permission mapping ─────────────────────────────
def required_perm(method, path):
    base = path.split("?", 1)[0]
    if base == "/api/v2/servers":
        return "servers.view" if method == "GET" else None
    m = SID_RX.match(base)
    if not m:
        return None
    rest = m.group(2) or ""
    if rest in ("", "/stats", "/history") and method == "GET":
        return "servers.view"
    if rest == "/logs" and method == "GET":
        return "console.view"
    if rest == "/stdin" and method == "POST":
        return "console.command"
    if rest.startswith("/action/") and method == "POST":
        act = rest.split("/")[2]
        if act in ("start_server", "stop_server", "restart_server"):
            return "server.control"
        if act == "kill_server":
            return "server.kill"
        if act == "backup_server":
            return "server.backup"
        return None
    if rest == "/backups" and method == "GET":
        return "server.backup"
    if rest == "/files":
        return "files.view" if method == "POST" else ("files.edit" if method in ("PATCH", "DELETE", "PUT") else None)
    if rest.startswith("/files/") and method in ("PUT", "PATCH", "POST"):
        return "files.edit"
    return None


def command_allowed(cmd, perms):
    if "\n" in cmd or "\r" in cmd:
        return False
    if "console.command.unrestricted" in perms:
        return True
    first = cmd.strip().lstrip("/").split(" ", 1)[0].lower()
    if ":" in first:
        first = first.split(":", 1)[1]
    return first not in [b.lower() for b in conf().get("blocked_commands", [])]


def shop_command(product_id, player):
    product = SHOP_PRODUCTS.get(product_id)
    if not product or not SHOP_PLAYER_RX.fullmatch(player):
        raise ValueError("invalid shop product or player")
    kind, value = product
    return "points give %s %d" % (player, value) if kind == "coins" else "lp user %s parent add %s" % (player, value)


# ───────────────────────────── HTTP ─────────────────────────────
class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "PXMXGateway/" + VERSION
    sys_version = ""

    def log_message(self, fmt, *args):
        pass

    @property
    def ip(self):
        ip = self.client_address[0]
        ip = ip[7:] if ip.startswith("::ffff:") else ip
        # Web-Panel (secure.ryvenmc.de): nur aus dem eigenen Docker-Netz wird die weitergereichte echte Client-IP übernommen
        fwd = self.headers.get("X-Panel-Client-IP", "").strip()
        if fwd and trusted_proxy(ip):
            try:
                return str(ipaddress.ip_address(fwd))
            except ValueError:
                pass
        return ip

    def body(self, limit=300 * 1024 * 1024):
        n = int(self.headers.get("Content-Length") or 0)
        if n > limit:
            raise ValueError("Anfrage zu groß")
        self._body_read = True
        return self.rfile.read(n) if n > 0 else b""

    def drain(self):
        """Reads an unread (small) request body – closing with unread data makes the client see a reset instead of our error."""
        if getattr(self, "_body_read", False):
            return
        try:
            if int(self.headers.get("Content-Length") or 0) <= 4 * 1024 * 1024:
                self.body()
        except Exception:
            pass

    def send_json(self, code, obj, extra=None):
        data = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def send_file(self, fp):
        try:
            with open(fp, "rb") as f:
                data = f.read()
        except OSError:
            return self.err(404, "NOT_FOUND", "Datei nicht gefunden")
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def err(self, code, error, msg):
        self.drain()
        self.send_json(code, {"status": "error", "error": error, "error_data": msg})

    def do_GET(self): self.route("GET")
    def do_POST(self): self.route("POST")
    def do_PUT(self): self.route("PUT")
    def do_PATCH(self): self.route("PATCH")
    def do_DELETE(self): self.route("DELETE")

    def rate_limited(self, kind="login"):
        now = time.time()
        key = kind + "|" + self.ip
        with lock:
            hits = [t for t in login_hits.get(key, []) if t > now - 300]
            hits.append(now)
            login_hits[key] = hits
            if len(login_hits) > 5000:  # forget idle entries
                for k in [k for k, v in login_hits.items() if not v or v[-1] < now - 300]:
                    del login_hits[k]
        return len(hits) > 10

    def session(self):
        auth = self.headers.get("Authorization", "")
        if not auth.startswith("Bearer "):
            return None
        h = thash(auth[7:].strip())
        now = time.time()
        if auth[7:].strip().startswith(API_PREFIX):
            return self.api_key_session(h, now)
        with db() as con:
            s = con.execute("select s.*, u.frozen, u.must_change from sessions s join users u on u.username = s.username "
                            "where s.token_hash=?", (h,)).fetchone()
            if not s or s["expires"] < now:
                return None
            if s["ip"] != self.ip and not ip_ok(self.ip, json.loads((con.execute("select ips from users where username=?", (s["username"],)).fetchone() or {"ips": "[]"})["ips"] or "[]") + [s["ip"]]):
                con.execute("delete from sessions where token_hash=?", (h,))
                audit(s["username"], self.ip, "Sitzung von fremder IP benutzt", "gebunden an " + s["ip"], "blockiert", con=con)
                return None
            if s["frozen"]:
                return None
            con.execute("update sessions set last_seen=? where token_hash=?", (now, h))
        return s

    def api_key_session(self, h, now):
        """API-Schlüssel für Folgeprojekte: eigene Rechte, nur von freigegebenen IPs, kein Passwortwechsel, keine Team-Verwaltung."""
        with db() as con:
            k = con.execute("select * from api_keys where token_hash=?", (h,)).fetchone()
            if not k or k["revoked"] or (k["expires"] and k["expires"] < now):
                return None
            if trusted_proxy(self.ip) or not ip_ok(self.ip, json.loads(k["ips"] or "[]")):
                audit("api:" + k["name"], self.ip, "API-Schlüssel von fremder IP", "erlaubt: " + k["ips"], "blockiert", con=con)
                return None
            con.execute("update api_keys set last_used=?, last_ip=?, uses=uses+1 where name=?", (now, self.ip, k["name"]))
        perms = [p for p in json.loads(k["perms"] or "[]") if p in API_PERMS]
        return {"token_hash": h, "username": "api:" + k["name"], "rank": "api", "perms": json.dumps(perms), "must_change": 0,
                "frozen": 0, "ip": self.ip, "api": True}

    def me(self, s, perms, rank=None):
        r = rank if rank is not None else s["rank"]
        api_key = isinstance(s, dict) and bool(s.get("api"))
        on = False if api_key else tfa_state(s["username"])
        return {"username": s["username"], "uuid": s["username"], "rank": r, "perms": perms,
                "all_perms": PERMS, "must_change": bool(s["must_change"]), "tfa_on": on,
                "tfa_setup_required": (not api_key) and tfa_required(r) and not on}

    def route(self, method):
        try:
            path = self.path
            if path == "/panel/config" and method == "GET":
                return self.send_json(200, {"status": "ok", "data": {"auth": "password", "version": VERSION}})
            if path == "/panel/login/2fa" and method == "POST":
                return self.login_2fa()
            if path == "/panel/login" and method == "POST":
                return self.login()
            # panel updates (published by the owner into DIST) – needed before login, so no session required
            if path == "/panel/update" and method == "GET":
                try:
                    with open(os.path.join(DIST, "version.json"), encoding="utf-8") as f:
                        info = json.load(f)
                except (OSError, ValueError):
                    return self.err(404, "NO_UPDATE", "Keine Version veröffentlicht.")
                return self.send_json(200, {"status": "ok", "data": info})
            if path == "/panel/update/PXMX.exe" and method == "GET":
                return self.send_file(os.path.join(DIST, "PXMX.exe"))
            s = self.session()
            if s is None:
                return self.err(401, "ACCESS_DENIED", "Sitzung ungültig oder abgelaufen – bitte neu anmelden.")
            perms = json.loads(s["perms"])
            if path == "/panel/logout" and method == "POST":
                with db() as con:
                    con.execute("delete from sessions where token_hash=?", (s["token_hash"],))
                audit(s["username"], self.ip, "Abmeldung")
                return self.send_json(200, {"status": "ok"})
            if path in ("/panel/me", "/api/v2/users/@me") and method == "GET":
                # the panel polls this – always answer with the current rights, not the ones from login time
                rank, perms = refresh_session(s["token_hash"], s["username"], s["rank"], perms)
                return self.send_json(200, {"status": "ok", "data": self.me(s, perms, rank)})
            if isinstance(s, dict) and s.get("api") and (path.startswith("/panel/team") or path in ("/panel/password", "/panel/2fa")):
                return self.err(403, "API_KEY_FORBIDDEN", "Mit einem API-Schlüssel nicht erlaubt.")
            if path == "/panel/2fa" and method == "POST":
                return self.tfa(s)
            if path == "/panel/password" and method == "POST":
                return self.change_password(s)
            if tfa_setup_blocked(s):
                return self.err(403, "TFA_SETUP", "Für deinen Rang ist 2FA Pflicht – bitte zuerst 2FA einrichten.")
            if s["must_change"]:
                return self.err(403, "PASSWORD_CHANGE", "Bitte zuerst ein eigenes Passwort festlegen.")
            if path == "/panel/eco" and method == "POST":
                return self.eco(s, perms)
            if path == "/panel/finanzen" and method == "POST":
                return self.finanzen(s, perms)
            if path == "/panel/mod" and method == "POST":
                return self.mod(s, perms)
            if path == "/panel/netz" and method == "POST":
                return self.netz(s, perms)
            if path == "/panel/team" and method == "POST":
                return self.team(s, perms)
            if path == "/panel/store" and method == "POST":
                return self.store_fulfill(s, perms)
            if path.startswith("/api/v2/"):
                return self.proxy(method, s, perms)
            return self.err(404, "NOT_FOUND", "Unbekannter Pfad")
        except Exception as e:  # never leak a stack trace
            try:
                self.err(500, "GATEWAY_ERROR", str(e)[:300])
            except Exception:
                pass

    def login(self):
        ip = self.ip
        now = time.time()
        if trusted_proxy(ip):   # Anfrage vom Web-Panel ohne echte Besucher-IP – niemals an die Container-IP binden
            return self.err(400, "NO_CLIENT_IP", "Deine IP-Adresse konnte nicht ermittelt werden – bitte die Seite neu laden.")
        if self.rate_limited():
            return self.err(429, "RATE_LIMIT", "Zu viele Anmeldeversuche – bitte 5 Minuten warten.")
        try:
            data = json.loads(self.body(65536) or b"{}")
        except ValueError:
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage.")
        username = str(data.get("username", "")).strip().lower()
        password = str(data.get("password", ""))
        bad = "Benutzername oder Passwort falsch."
        with db() as con:
            u = con.execute("select * from users where username=?", (username,)).fetchone() if USER_RX.match(username) else None
            if u is None:
                check_pw(password, dummy_hash())
                audit(username[:40], ip, "Login fehlgeschlagen", "unbekannter Benutzer", "abgelehnt", con=con)
                return self.err(403, "LOGIN_FAILED", bad)
            if u["locked_until"] and u["locked_until"] > now:
                audit(username, ip, "Login eines vorübergehend gesperrten Accounts", "", "abgelehnt", con=con)
                return self.err(403, "LOCKED", "Zu viele falsche Passwörter – der Account ist für %d Minuten gesperrt."
                                % max(1, int((u["locked_until"] - now) / 60 + 0.99)))
            if not check_pw(password, u["pw"]):
                fails = (u["fails"] or 0) + 1
                until = now + LOCK_SECONDS if fails >= MAX_FAILS else 0
                con.execute("update users set fails=?, locked_until=? where username=?", (0 if until else fails, until, username))
                audit(username, ip, "Falsches Passwort", "Account für 15 Min gesperrt" if until else "Versuch %d/%d" % (fails, MAX_FAILS), "abgelehnt", con=con)
                return self.err(403, "LOGIN_FAILED", bad)
            con.execute("update users set fails=0, locked_until=0 where username=?", (username,))
            if u["frozen"]:
                audit(username, ip, "Login eines gesperrten Accounts", "", "abgelehnt", con=con)
                return self.err(403, "FROZEN", "Dein Panel-Zugang ist gesperrt. Wende dich an den Owner.")
        try:
            rank, perms = resolve_rank(u)
        except Exception as e:
            return self.err(500, "LUCKPERMS", "Rang konnte nicht gelesen werden: " + str(e)[:200])
        if not rank:
            audit(username, ip, "Login ohne Team-Rang", u["mc_name"] or u["role"], "abgelehnt")
            return self.err(403, "NO_RANK", "Dein Rang hat keinen Zugang zum Panel.")
        with db() as con:
            ips = json.loads(u["ips"] or "[]")
            if not ips:
                ips = [ip]
                con.execute("update users set ips=? where username=?", (json.dumps(ips), username))
                audit(username, ip, "Erster Login – IP gebunden", ip, con=con)
            elif not ip_ok(ip, ips):
                pend = con.execute("select id from ip_requests where username=? and ip=? and status='pending'", (username, ip)).fetchone()
                if not pend:
                    con.execute("insert into ip_requests (username, ip, time) values (?,?,?)", (username, ip, now))
                audit(username, ip, "Login von nicht freigegebener IP", "erlaubt: " + ", ".join(ips), "abgelehnt", con=con)
                return self.err(403, "IP_NOT_ALLOWED",
                                "Diese IP-Adresse (%s) ist für deinen Account nicht freigegeben. "
                                "Der Owner hat eine Freigabe-Anfrage erhalten." % ip)
            if u["totp_on"]:
                with lock:
                    for k in [k for k, v in tfa_tickets.items() if v["exp"] < now]:
                        del tfa_tickets[k]
                    ticket = secrets.token_urlsafe(24)
                    tfa_tickets[ticket] = {"username": username, "ip": ip, "exp": now + TFA_TICKET_SECONDS, "fails": 0}
                return self.send_json(200, {"status": "ok", "data": {"need_2fa": True, "ticket": ticket}})
        return self.issue_session(u, rank, perms)

    def issue_session(self, u, rank, perms):
        ip, now, username = self.ip, time.time(), u["username"]
        with db() as con:
            con.execute("update users set last_login=?, last_ip=?, rank=? where username=?", (now, ip, rank, username))
            # höchstens 3 Sitzungen pro Account (App, Browser, Handy) – die ältesten fliegen raus
            con.execute("delete from sessions where username=? and token_hash not in (select token_hash from sessions where username=? order by created desc limit 2)", (username, username))
            token = secrets.token_urlsafe(32)
            exp = now + 3600 * float(conf().get("session_hours", 12))
            con.execute("insert into sessions (token_hash, username, rank, ip, perms, created, expires, last_seen) values (?,?,?,?,?,?,?,?)",
                        (thash(token), username, rank, ip, json.dumps(perms), now, exp, now))
        audit(username, ip, "Login", "Rang " + rank)
        self.send_json(200, {"status": "ok", "data": {"token": token, "username": username, "uuid": username, "rank": rank,
                                                      "perms": perms, "all_perms": PERMS, "expires": exp,
                                                      "must_change": bool(u["must_change"]), "tfa_on": bool(u["totp_on"]),
                                                      "tfa_setup_required": tfa_required(rank) and not u["totp_on"]}})

    def login_2fa(self):
        ip, now = self.ip, time.time()
        if trusted_proxy(ip):
            return self.err(400, "NO_CLIENT_IP", "Deine IP-Adresse konnte nicht ermittelt werden – bitte die Seite neu laden.")
        if self.rate_limited("tfa"):
            return self.err(429, "RATE_LIMIT", "Zu viele Versuche – bitte 5 Minuten warten.")
        try:
            data = json.loads(self.body(65536) or b"{}")
        except ValueError:
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage.")
        tk = str(data.get("ticket", ""))
        code = str(data.get("code", "")).strip()
        with lock:
            t = tfa_tickets.get(tk)
            if not t or t["exp"] < now or t["ip"] != ip:
                tfa_tickets.pop(tk, None)
                return self.err(403, "TICKET", "Die Anmeldung ist abgelaufen – bitte erneut mit Passwort anmelden.")
        username = t["username"]
        with db() as con:
            u = con.execute("select * from users where username=?", (username,)).fetchone()
            if not u or not u["totp_on"] or u["frozen"]:
                return self.err(403, "LOGIN_FAILED", "Anmeldung nicht möglich.")
            if u["locked_until"] and u["locked_until"] > now:
                return self.err(403, "LOCKED", "Zu viele falsche Codes – der Account ist vorübergehend gesperrt.")
            ok, used = False, ""
            step = totp_verify(u["totp_secret"], code, u["totp_last"])
            if step:
                con.execute("update users set totp_last=? where username=?", (step, username))
                ok, used = True, "Code"
            else:
                norm = code.lower().replace(" ", "")
                hashes = json.loads(u["recovery"] or "[]")
                h = thash(norm)
                if len(norm) == 13 and h in hashes:
                    hashes.remove(h)
                    con.execute("update users set recovery=? where username=?", (json.dumps(hashes), username))
                    ok, used = True, "Wiederherstellungscode (%d übrig)" % len(hashes)
            if not ok:
                with lock:
                    t["fails"] += 1
                    dead = t["fails"] >= TFA_MAX_FAILS
                    if dead:
                        tfa_tickets.pop(tk, None)
                if dead:
                    con.execute("update users set locked_until=? where username=?", (now + LOCK_SECONDS, username))
                audit(username, ip, "Falscher 2FA-Code", "Account für 15 Min gesperrt" if dead else "Versuch %d/%d" % (t["fails"], TFA_MAX_FAILS), "abgelehnt", con=con)
                return self.err(403, "TFA_FAILED", "Code falsch oder abgelaufen." if not dead else "Zu viele falsche Codes – Account 15 Minuten gesperrt.")
            audit(username, ip, "2FA bestätigt", used, con=con)
        with lock:
            tfa_tickets.pop(tk, None)
        try:
            rank, perms = resolve_rank(u)
        except Exception as e:
            return self.err(500, "LUCKPERMS", "Rang konnte nicht gelesen werden: " + str(e)[:200])
        if not rank:
            return self.err(403, "NO_RANK", "Dein Rang hat keinen Zugang zum Panel.")
        return self.issue_session(u, rank, perms)

    def tfa(self, s):
        """2FA-Verwaltung des eigenen Accounts: status | setup | enable | disable."""
        if self.rate_limited("tfa"):
            return self.err(429, "RATE_LIMIT", "Zu viele Versuche – bitte 5 Minuten warten.")
        try:
            data = json.loads(self.body(65536) or b"{}")
        except ValueError:
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage.")
        cmd, name = str(data.get("cmd", "status")), s["username"]
        with db() as con:
            u = con.execute("select * from users where username=?", (name,)).fetchone()
            if not u:
                return self.err(404, "UNKNOWN", "Account unbekannt.")
            if cmd == "status":
                return self.send_json(200, {"status": "ok", "data": {"enabled": bool(u["totp_on"]), "required": tfa_required(s["rank"]),
                                                                     "recovery_left": len(json.loads(u["recovery"] or "[]")) if u["totp_on"] else 0}})
            if cmd == "setup":
                if u["totp_on"]:
                    return self.err(400, "ALREADY", "2FA ist schon aktiv – zum Neu-Einrichten erst deaktivieren.")
                secret = b32_secret()
                con.execute("update users set totp_secret=?, totp_last=0 where username=?", (secret, name))
                audit(name, self.ip, "2FA-Einrichtung gestartet", "", con=con)
                uri = "otpauth://totp/PXMX:%s?secret=%s&issuer=PXMX&algorithm=SHA1&digits=6&period=30" % (urllib.parse.quote(name, safe=""), secret)
                return self.send_json(200, {"status": "ok", "data": {"secret": secret, "uri": uri}})
            if cmd == "enable":
                if u["totp_on"] or not u["totp_secret"]:
                    return self.err(400, "NO_SETUP", "Keine Einrichtung offen.")
                step = totp_verify(u["totp_secret"], data.get("code"), 0)
                if not step:
                    audit(name, self.ip, "2FA-Bestätigung falsch", "", "abgelehnt", con=con)
                    return self.err(403, "TFA_FAILED", "Code falsch – bitte den aktuellen Code der App eingeben.")
                codes, hashes = new_recovery_codes()
                con.execute("update users set totp_on=1, totp_last=?, recovery=? where username=?", (step, json.dumps(hashes), name))
                con.execute("delete from sessions where username=? and token_hash<>?", (name, s["token_hash"]))
                audit(name, self.ip, "2FA aktiviert", "", con=con)
                return self.send_json(200, {"status": "ok", "data": {"recovery_codes": codes}})   # nur dieses eine Mal sichtbar
            if cmd == "disable":
                if tfa_required(s["rank"]):
                    return self.err(403, "REQUIRED", "Für deinen Rang ist 2FA Pflicht – nur der Owner kann sie zurücksetzen.")
                if not u["totp_on"]:
                    return self.err(400, "NOT_ON", "2FA ist nicht aktiv.")
                if not check_pw(str(data.get("password", "")), u["pw"]) or not totp_verify(u["totp_secret"], data.get("code"), u["totp_last"]):
                    audit(name, self.ip, "2FA deaktivieren", "Passwort oder Code falsch", "abgelehnt", con=con)
                    return self.err(403, "TFA_FAILED", "Passwort oder Code falsch.")
                con.execute("update users set totp_on=0, totp_secret='', totp_last=0, recovery='[]' where username=?", (name,))
                audit(name, self.ip, "2FA deaktiviert", "", con=con)
                return self.send_json(200, {"status": "ok"})
        return self.err(400, "BAD_REQUEST", "Unbekannte Aktion.")

    def change_password(self, s):
        if self.rate_limited("password"):
            return self.err(429, "RATE_LIMIT", "Zu viele Versuche – bitte 5 Minuten warten.")
        try:
            data = json.loads(self.body(65536) or b"{}")
        except ValueError:
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage.")
        old, new = str(data.get("old", "")), str(data.get("new", ""))
        with db() as con:
            u = con.execute("select pw from users where username=?", (s["username"],)).fetchone()
            if not u or not check_pw(old, u["pw"]):
                audit(s["username"], self.ip, "Passwortänderung", "altes Passwort falsch", "abgelehnt", con=con)
                return self.err(403, "WRONG_PASSWORD", "Das bisherige Passwort ist falsch.")
            prob = pw_problem(new)
            if prob:
                return self.err(400, "WEAK_PASSWORD", prob)
            if new == old:
                return self.err(400, "WEAK_PASSWORD", "Das neue Passwort muss sich vom alten unterscheiden.")
            con.execute("update users set pw=?, must_change=0 where username=?", (hash_pw(new), s["username"]))
            # other sessions of this account end; the current one stays
            con.execute("delete from sessions where username=? and token_hash<>?", (s["username"], s["token_hash"]))
        audit(s["username"], self.ip, "Passwort geändert")
        return self.send_json(200, {"status": "ok"})

    def eco(self, s, perms):
        """Economy tab: runs eco.py (installed by the owner's panel) with the caller's rights."""
        try:
            req = json.loads(self.body(1024 * 1024).decode() or "{}")
        except ValueError:
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage")
        cmd = str(req.get("cmd", ""))
        need = ECO_CMDS.get(cmd)
        if need is not None and need not in perms:
            # stored rights may be stale (rank changed in LuckPerms) – check once more before refusing
            perms = refresh_session(s["token_hash"], s["username"], s["rank"], perms)[1]
        if need is None or need not in perms:
            audit(s["username"], self.ip, "Verweigert", "Wirtschaft: " + cmd[:40], "keine Berechtigung")
            return self.err(403, "NO_PERMISSION", "Keine Berechtigung" + (" (" + need + ")" if need else "") + ".")
        if not os.path.exists(ECO_PY):
            return self.err(503, "NOT_INSTALLED", "Der Wirtschafts-Reiter ist noch nicht eingerichtet – der Owner muss ihn einmal im Panel öffnen.")
        args = req.get("args") if isinstance(req.get("args"), dict) else {}
        args["actor"] = s["username"] + " (Team)"
        arg = base64.b64encode(json.dumps(args).encode()).decode()
        try:
            r = subprocess.run(["python3", ECO_PY, cmd, arg], capture_output=True, timeout=120)
            res = json.loads(r.stdout.decode() or "{}")
        except (subprocess.TimeoutExpired, ValueError) as e:
            return self.err(500, "ECO_ERROR", "Auswertung fehlgeschlagen: " + str(e)[:200])
        if need == "economy.edit":
            audit(s["username"], self.ip, "Wirtschaft: " + cmd, json.dumps({k: v for k, v in args.items() if k != "actor"}, ensure_ascii=False)[:300],
                  "ok" if res.get("status") == "ok" else "Fehler")
        if res.get("status") != "ok":
            return self.err(400, "ECO_ERROR", res.get("error") or "Fehler")
        return self.send_json(200, res)

    def finanzen(self, s, perms):
        """Finanzen (Buchhaltung): finanzen.py; Belege kommen als Base64 über stdin (bis ca. 14 MB Anfrage)."""
        try:
            req = json.loads(self.body(15 * 1024 * 1024).decode() or "{}")
        except ValueError:
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage")
        cmd = str(req.get("cmd", ""))
        need = FINANZEN_CMDS.get(cmd)
        if need is not None and need not in perms:
            perms = refresh_session(s["token_hash"], s["username"], s["rank"], perms)[1]
        if need is None or need not in perms:
            audit(s["username"], self.ip, "Verweigert", "Finanzen: " + cmd[:40], "keine Berechtigung")
            return self.err(403, "NO_PERMISSION", "Keine Berechtigung" + (" (" + need + ")" if need else "") + ".")
        if not os.path.exists(FINANZEN_PY):
            return self.err(503, "NOT_INSTALLED", "Finanzen ist noch nicht eingerichtet – der Owner muss den Reiter einmal im Panel öffnen.")
        args = req.get("args") if isinstance(req.get("args"), dict) else {}
        args["actor"] = s["username"] + " (Team)"
        try:
            r = subprocess.run(["python3", FINANZEN_PY, cmd, "-"], input=json.dumps(args).encode(), capture_output=True, timeout=120)
            res = json.loads(r.stdout.decode() or "{}")
        except (subprocess.TimeoutExpired, ValueError) as e:
            return self.err(500, "FINANZEN_ERROR", "Auswertung fehlgeschlagen: " + str(e)[:200])
        if need == "finanzen.edit":
            detail = json.dumps({k: (v if k != "beleg_b64" else "<beleg>") for k, v in args.items() if k != "actor"}, ensure_ascii=False)[:300]
            audit(s["username"], self.ip, "Finanzen: " + cmd, detail, "ok" if res.get("status") == "ok" else "Fehler")
        elif cmd == "export":
            audit(s["username"], self.ip, "Finanzen: Export", "%s %s" % (args.get("jahr"), args.get("format")), "ok" if res.get("status") == "ok" else "Fehler")
        if res.get("status") != "ok":
            return self.err(400, "FINANZEN_ERROR", res.get("error") or "Fehler")
        return self.send_json(200, res)

    def netz(self, s, perms):
        """Netzwerk-Monitor: liest die unabhängigen Messungen von netmon.py (eigener systemd-Dienst, nicht in Docker)."""
        if "netzwerk.view" not in perms:
            perms = refresh_session(s["token_hash"], s["username"], s["rank"], perms)[1]
        if "netzwerk.view" not in perms:
            audit(s["username"], self.ip, "Verweigert", "Netzwerk-Monitor", "keine Berechtigung")
            return self.err(403, "NO_PERMISSION", "Keine Berechtigung (netzwerk.view).")
        try:
            req = json.loads(self.body(64 * 1024).decode() or "{}")
        except ValueError:
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage")
        cmd = str(req.get("cmd", ""))
        if cmd not in NETMON_CMDS:
            return self.err(400, "BAD_REQUEST", "Unbekannter Befehl")
        if not os.path.exists(NETMON_PY):
            return self.err(503, "NOT_INSTALLED", "Der Netzwerk-Monitor ist nicht installiert.")
        args = req.get("args") if isinstance(req.get("args"), dict) else {}
        try:
            r = subprocess.run(["python3", NETMON_PY, cmd, base64.b64encode(json.dumps(args).encode()).decode()], capture_output=True, timeout=60)
            res = json.loads(r.stdout.decode() or "{}")
        except (subprocess.TimeoutExpired, ValueError) as e:
            return self.err(500, "NETZ_ERROR", "Abfrage fehlgeschlagen: " + str(e)[:200])
        if cmd == "export":
            audit(s["username"], self.ip, "Netzwerk-Monitor", "CSV-Export (%s Zeilen)" % res.get("zeilen", "?"), "ok" if res.get("status") == "ok" else "Fehler")
        if res.get("status") != "ok":
            return self.err(400, "NETZ_ERROR", res.get("error") or "Fehler")
        return self.send_json(200, res)

    def mod(self, s, perms):
        """Moderation tab: runs mod.py with the caller's rights; the team member is recorded as the actor."""
        try:
            req = json.loads(self.body(256 * 1024).decode() or "{}")
        except ValueError:
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage")
        cmd = str(req.get("cmd", ""))
        args = req.get("args") if isinstance(req.get("args"), dict) else {}
        if cmd in ("nutzer", "nutzer_detail", "rang_detail", "nutzer_log"):
            need = "users.view"
        elif cmd == "nutzer_act":
            need = "users.economy" if str(args.get("op", "")) in ("user_money", "user_coins") else "users.ranks"
        elif cmd in ("verbindungen", "plugins", "datenbanken", "ds_status", "waechter_bericht", "ds_antraege", "ds_vorschau", "ds_auskunft_status"):
            need = "datenschutz.view"
        elif cmd in ("verbindung_ok", "plugin_doku"):
            need = "datenschutz.edit"
        elif cmd in ("ds_loeschen", "ds_ablehnen"):
            need = "datenschutz.loeschen"
        elif cmd in ("ds_auskunft", "ds_auskunft_kategorien"):
            need = "datenschutz.auskunft"
        elif cmd in ("ki_overview", "ki_verlauf", "ki_wissen", "ki_wissen_lesen", "ki_config"):
            need = "ki.view"
        elif cmd in ("ki_set_status", "ki_wissen_speichern", "ki_config_set"):
            need = "ki.edit"
        elif cmd in MOD_VIEW:
            need = "moderation.view"
        elif cmd == "report_set":
            need = "moderation.reports"
        elif cmd == "act":
            need = MOD_ACT.get(str(args.get("action", "")))
        else:
            need = None
        if need is not None and need not in perms:
            perms = refresh_session(s["token_hash"], s["username"], s["rank"], perms)[1]
        if need is None or need not in perms:
            audit(s["username"], self.ip, "Verweigert", "Moderation: " + cmd[:20] + " " + str(args.get("action", ""))[:12], "keine Berechtigung")
            return self.err(403, "NO_PERMISSION", "Keine Berechtigung" + (" (" + need + ")" if need else "") + ".")
        if not os.path.exists(MOD_PY):
            return self.err(503, "NOT_INSTALLED", "Die Moderation ist noch nicht eingerichtet – der Owner muss das Panel einmal starten.")
        args["actor"] = s["username"] + " (Panel)"
        try:
            r = subprocess.run(["python3", MOD_PY, cmd, "-"], input=json.dumps(args).encode(), capture_output=True, timeout=(240 if cmd in ("ds_auskunft", "ds_auskunft_kategorien") else 60))
            res = json.loads(r.stdout.decode() or "{}")
        except (subprocess.TimeoutExpired, ValueError, OSError) as e:
            return self.err(500, "MOD_ERROR", "Moderation fehlgeschlagen: " + str(e)[:200])
        if cmd == "verbindung_ok":
            audit(s["username"], self.ip, "Datenschutz", "Verbindung " + str(args.get("ip", "")) + " als geprüft markiert: " + str(args.get("dienst", ""))[:100] + " | Freigabe: " + str((res.get("data") or {}).get("freigabe", ""))[:140] + " | Grund: " + str((res.get("data") or {}).get("grund", ""))[:120], "ok" if res.get("status") == "ok" else "Fehler")
        if cmd == "plugin_doku":
            audit(s["username"], self.ip, "Datenschutz", "Plugin-Doku %s: %s" % (str(args.get("id", ""))[:60], "gelöscht" if args.get("loeschen") else ", ".join(args.get("kategorien") or [])[:200]), "ok" if res.get("status") == "ok" else "Fehler")
        if cmd == "ds_loeschen":
            audit(s["username"], self.ip, "Datenschutz", "Löschung gestartet: %s" % (("Antrag #%s" % args.get("id")) if args.get("id") else str(args.get("spieler", ""))[:40]) + (" → #%s" % res.get("data", {}).get("id") if res.get("status") == "ok" else ""), "ok" if res.get("status") == "ok" else "Fehler: " + str(res.get("error", ""))[:120])
        if cmd == "ds_auskunft":
            dd = res.get("data") or {}
            audit(s["username"], self.ip, "Datenschutz", "Datenauskunft %s: %s%s" % ({"senden": "per Mail", "discord": "Link per Discord-DM"}.get(args.get("modus"), "heruntergeladen"), str(args.get("spieler", ""))[:40], (" an " + str(args.get("email", ""))[:80]) if args.get("modus") == "senden" else "") + " [Kategorien: %s]" % (",".join(str(x)[:20] for x in args["kategorien"][:12]) if isinstance(args.get("kategorien"), list) else "alle"), "ok (%s Dateien)" % dd.get("dateien") if res.get("status") == "ok" else "Fehler: " + str(res.get("error", ""))[:120])
        if cmd == "ds_auskunft_kategorien":
            audit(s["username"], self.ip, "Datenschutz", "Auskunft-Kategorien abgefragt: %s" % str(args.get("spieler", ""))[:40], "ok" if res.get("status") == "ok" else "Fehler: " + str(res.get("error", ""))[:120])
        if cmd == "ds_ablehnen":
            audit(s["username"], self.ip, "Datenschutz", "Löschantrag #%s abgelehnt: %s" % (args.get("id"), str(args.get("grund", ""))[:150]), "ok" if res.get("status") == "ok" else "Fehler")
        if cmd in ("ki_set_status", "ki_wissen_speichern", "ki_config_set"):
            audit(s["username"], self.ip, "KI-Support", cmd[3:] + " " + ((str(args.get("id")) + " -> " + str(args.get("status") or "done")) if cmd == "ki_set_status" else str(args.get("name") or json.dumps({k: args.get(k) for k in ("cooldown_seconds", "daily_limit")})))[:100], "ok" if res.get("status") == "ok" else "Fehler")
        if cmd == "ki_verlauf":
            audit(s["username"], self.ip, "KI-Support", "Verlauf gelesen (Filter %s, ab %s)" % (str(args.get("filter") or "alle")[:20], str(args.get("offset") or 0)[:8]), "ok" if res.get("status") == "ok" else "Fehler")
        if cmd == "nutzer_act":
            a2 = {k: v for k, v in args.items() if k in ("op", "name", "group", "perm", "value", "dur", "art", "amount", "weight", "prefix", "parent", "server_ctx")}
            audit(s["username"], self.ip, "Nutzer & Ränge", json.dumps(a2, ensure_ascii=False)[:300], "ok" if res.get("status") == "ok" else "Fehler")
        if cmd in ("act", "report_set"):
            detail = (str(args.get("action", "")) + " " + str(args.get("player", "")) + " " + str(args.get("duration", "")) + " " + str(args.get("reason", ""))) \
                if cmd == "act" else "Report #%s → %s" % (args.get("id"), args.get("status"))
            audit(s["username"], self.ip, "Moderation", detail.strip()[:300], "ok" if res.get("status") == "ok" else "Fehler")
        if res.get("status") != "ok":
            return self.err(400, "MOD_ERROR", res.get("error") or "Fehler")
        return self.send_json(200, res)

    def team(self, s, perms):
        """Team & Zugänge im Web-Panel (nur Recht team.manage): Übersicht, IP-Freigaben, Accounts sperren, Sitzungen beenden."""
        if "team.manage" not in perms:
            perms = refresh_session(s["token_hash"], s["username"], s["rank"], perms)[1]
        if "team.manage" not in perms:
            audit(s["username"], self.ip, "Verweigert", "Team-Verwaltung", "keine Berechtigung")
            return self.err(403, "NO_PERMISSION", "Keine Berechtigung (team.manage).")
        try:
            req = json.loads(self.body(64 * 1024).decode() or "{}")
        except ValueError:
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage")
        cmd, a = str(req.get("cmd", "")), (req.get("args") if isinstance(req.get("args"), dict) else {})
        if cmd == "state":
            st = admin("state", "")
            st["groups"] = sorted(st["groups"].keys()) if isinstance(st.get("groups"), dict) else []
            st["perms"] = [list(x) for x in PERMS]
            st["sessions"] = [{k: v for k, v in x.items() if k in ("username", "rank", "ip", "created", "last_seen", "expires")} for x in st.get("sessions", [])]
            st["audit"] = st.get("audit", [])[:200]
            st["me"] = s["username"]
            with db() as con:
                st["api_keys"] = [{k: r[k] for k in r.keys() if k != "token_hash"} for r in con.execute("select * from api_keys order by created desc")]
            st["api_perms"] = API_PERMS
            return self.send_json(200, {"status": "ok", "data": st})
        if cmd == "api_create":
            n = str(a.get("name", "")).strip().lower()
            if not API_NAME_RX.match(n):
                return self.err(400, "BAD_NAME", "Name: 3–32 Zeichen, nur a–z, 0–9, _ und -.")
            ps = [p for p in (a.get("perms") or []) if p in API_PERMS]
            if not ps:
                return self.err(400, "NO_PERMS", "Bitte mindestens ein Recht wählen.")
            ips = [str(x).strip() for x in (a.get("ips") or []) if str(x).strip()]
            if not ips or any(not re.match(r"^[0-9a-fA-F:.]{3,45}$", x) or trusted_proxy(x) for x in ips):
                return self.err(400, "BAD_IPS", "Mindestens eine gültige, öffentliche IP-Adresse angeben (IPv6: gilt für das /64-Netz).")
            try:
                tage = max(0, min(3650, int(a.get("tage") or 0)))
            except ValueError:
                tage = 0
            token = API_PREFIX + secrets.token_urlsafe(32)
            now = time.time()
            with db() as con:
                if con.execute("select 1 from api_keys where name=?", (n,)).fetchone():
                    return self.err(400, "EXISTS", "Diesen Namen gibt es schon.")
                con.execute("insert into api_keys (name, token_hash, prefix, perms, ips, created, created_by, expires, note) values (?,?,?,?,?,?,?,?,?)",
                            (n, thash(token), token[:10], json.dumps(ps), json.dumps(ips), now, s["username"], now + tage * 86400 if tage else 0,
                             str(a.get("note", ""))[:200]))
            audit(s["username"], self.ip, "API-Schlüssel angelegt", "%s · Rechte: %s · IPs: %s" % (n, ", ".join(ps), ", ".join(ips)))
            return self.send_json(200, {"status": "ok", "data": {"name": n, "token": token}})   # nur dieses eine Mal sichtbar
        if cmd == "api_revoke":
            n = str(a.get("name", "")).strip().lower()
            with db() as con:
                r = con.execute("update api_keys set revoked=1 where name=? and revoked=0", (n,)).rowcount
            audit(s["username"], self.ip, "API-Schlüssel gesperrt", n, "ok" if r else "unbekannt")
            return self.send_json(200, {"status": "ok"}) if r else self.err(404, "UNKNOWN", "Schlüssel unbekannt oder schon gesperrt.")
        if cmd == "tfa_reset":
            nm = str(a.get("username", "")).strip().lower()
            res = admin("tfa", base64.b64encode(json.dumps({"username": nm, "action": "reset"}).encode()).decode())
            audit(s["username"], self.ip, "2FA zurückgesetzt (Web)", nm, "ok" if res.get("ok") else "Fehler")
            return self.send_json(200, {"status": "ok"}) if res.get("ok") else self.err(400, "TEAM_ERROR", res.get("error") or "Fehler")
        if cmd == "tfa_config" and isinstance(a.get("ranks"), list):
            res = admin("config", base64.b64encode(json.dumps({"tfa_required_ranks": a["ranks"]}).encode()).decode())
            return self.send_json(200, {"status": "ok"}) if res.get("ok", True) else self.err(400, "TEAM_ERROR", "Fehler")
        name = str(a.get("username", "")).strip().lower()
        if cmd == "ip" and a.get("action") in ("approve", "deny", "remove"):
            res = admin("ip", base64.b64encode(json.dumps({"username": name, "ip": str(a.get("ip", "")), "action": a["action"]}).encode()).decode())
            detail = "%s %s für %s" % (a["action"], a.get("ip", ""), name)
        elif cmd == "ip" and a.get("action") == "add":
            res = admin("ip", base64.b64encode(json.dumps({"username": name, "ip": str(a.get("ip", "")).strip(), "action": "add"}).encode()).decode())
            detail = "add %s für %s" % (a.get("ip", ""), name)
        elif cmd == "user" and a.get("action") in ("freeze", "unfreeze", "kick", "create", "update", "password", "delete"):
            if name == s["username"] and a["action"] in ("freeze", "kick", "delete", "update"):
                return self.err(400, "SELF", "Den eigenen Account kannst du hier nicht sperren, abmelden, ändern oder löschen.")
            data = {"username": name, "action": a["action"]}
            for k in ("password", "mc_name", "role", "note", "must_change"):
                if k in a:
                    data[k] = a[k]
            res = admin("user", base64.b64encode(json.dumps(data).encode()).decode())
            detail = "%s %s" % (a["action"], name)
        elif cmd == "roles" and isinstance(a.get("roles"), dict):
            if "team.manage" not in (a["roles"].get("owner") or []):
                return self.err(400, "LOCKOUT", "Der Rang owner muss „team.manage“ behalten – sonst sperrst du dich aus.")
            res = admin("roles", base64.b64encode(json.dumps(a["roles"]).encode()).decode())
            detail = "Rechte pro Rang geändert"
        else:
            return self.err(400, "BAD_REQUEST", "Unbekannte Aktion.")
        audit(s["username"], self.ip, "Team (Web)", detail[:200], "ok" if res.get("ok") else "Fehler")
        if not res.get("ok"):
            return self.err(400, "TEAM_ERROR", res.get("error") or "Fehler")
        return self.send_json(200, {"status": "ok"})

    def proxy(self, method, s, perms):
        need = required_perm(method, self.path)
        if need is None or need not in perms:
            audit(s["username"], self.ip, "Verweigert", method + " " + self.path.split("?")[0], "keine Berechtigung")
            return self.err(403, "NO_PERMISSION", "Keine Berechtigung" + (" (" + need + ")" if need else "") + ".")
        body = self.body()
        if need == "console.command":
            cmd = body.decode("utf-8", "replace")
            if not command_allowed(cmd, perms):
                audit(s["username"], self.ip, "Befehl blockiert", cmd[:200], "gesperrt")
                return self.err(403, "NO_PERMISSION", "Dieser Befehl ist für deinen Rang gesperrt.")
        c = conf()
        req = urllib.request.Request(c["crafty_url"].rstrip("/") + self.path, data=body if method != "GET" else None, method=method)
        for k, v in self.headers.items():
            if k.lower() not in ("host", "authorization", "content-length", "connection", "accept-encoding"):
                req.add_header(k, v)
        req.add_header("Authorization", "Bearer " + c.get("crafty_token", ""))
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE  # Crafty on localhost uses a self-signed certificate
        try:
            with urllib.request.urlopen(req, timeout=600, context=ctx) as r:
                code, data, ctype = r.status, r.read(), r.headers.get("Content-Type", "application/json")
        except urllib.error.HTTPError as e:
            code, data, ctype = e.code, e.read(), e.headers.get("Content-Type", "application/json")
        if method != "GET":
            detail = method + " " + self.path.split("?")[0]
            if need == "console.command":
                detail = body.decode("utf-8", "replace")[:300]
            audit(s["username"], self.ip, need, detail, "ok" if code < 400 else "Fehler %d" % code)
        if code in (401, 403):
            code = 502  # Crafty rejected the gateway token – do not log the panel user out
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def store_fulfill(self, s, perms):
        if "store.fulfill" not in perms:
            audit(s["username"], self.ip, "Webshop-Auslieferung verweigert", "", "keine Berechtigung")
            return self.err(403, "NO_PERMISSION", "Keine Berechtigung (store.fulfill).")
        try:
            data = json.loads(self.body(65536).decode("utf-8") or "{}")
        except (ValueError, UnicodeDecodeError):
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage.")
        if not isinstance(data, dict):
            return self.err(400, "BAD_REQUEST", "Ungültige Anfrage.")
        order_id = str(data.get("order_id", ""))
        player = str(data.get("player", ""))
        product_id = str(data.get("product_id", ""))
        product = SHOP_PRODUCTS.get(product_id)
        if not SHOP_ORDER_RX.fullmatch(order_id) or not SHOP_PLAYER_RX.fullmatch(player) or not product:
            return self.err(400, "BAD_REQUEST", "Ungültige Bestellnummer, Spielername oder Produkt-ID.")

        now = time.time()
        with db() as con:
            con.execute("begin immediate")
            old = con.execute("select product_id, player, status from shop_fulfillments where order_id=?", (order_id,)).fetchone()
            if old:
                if old["product_id"] != product_id or old["player"].casefold() != player.casefold():
                    audit(s["username"], self.ip, "Webshop-Auftragskonflikt", order_id, "abgelehnt", con=con)
                    return self.err(409, "ORDER_CONFLICT", "Bestellnummer wurde bereits mit anderen Daten verwendet.")
                if old["status"] == "delivered":
                    return self.send_json(200, {"status": "ok", "data": {"order_id": order_id, "state": "delivered", "duplicate": True}})
                return self.err(409, "MANUAL_REVIEW", "Auslieferung läuft oder muss manuell geprüft werden; sie wird nicht wiederholt.")
            con.execute("insert into shop_fulfillments (order_id, product_id, player, status, created, updated) values (?,?,?,?,?,?)",
                        (order_id, product_id, player, "processing", now, now))
            audit(s["username"], self.ip, "Webshop-Auslieferung gestartet", "%s · %s · %s" % (order_id, product_id, player), con=con)

        try:
            command = shop_command(product_id, player)
            c = conf()
            crafty_url = c.get("crafty_url", "")
            crafty_token = c.get("crafty_token", "")
            if not isinstance(crafty_url, str) or not isinstance(crafty_token, str):
                raise ValueError("Invalid Crafty configuration")
            url = crafty_url.rstrip("/") + "/api/v2/servers/8742f521-e4e4-4277-88cf-84b9e0b91660/stdin"
            request = urllib.request.Request(url, data=command.encode("utf-8"), method="POST")
            request.add_header("Authorization", "Bearer " + crafty_token)
            request.add_header("Content-Type", "text/plain; charset=utf-8")
            context = ssl.create_default_context()
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
            with urllib.request.urlopen(request, timeout=30, context=context) as response:
                if response.status >= 400:
                    raise urllib.error.HTTPError(url, response.status, "Crafty rejected the command", response.headers, None)
                response.read(65536)
        except (OSError, urllib.error.URLError, urllib.error.HTTPError, ValueError, TypeError, AttributeError) as exc:
            with db() as con:
                con.execute("update shop_fulfillments set status='manual_review', updated=?, detail=? where order_id=?",
                            (time.time(), type(exc).__name__, order_id))
                audit(s["username"], self.ip, "Webshop-Auslieferung prüfen", order_id, "manuell prüfen", con=con)
            return self.err(503, "DELIVERY_UNKNOWN", "Der Server hat die Auslieferung nicht eindeutig bestätigt. Bitte manuell prüfen; keine automatische Wiederholung.")

        with db() as con:
            con.execute("update shop_fulfillments set status='delivered', updated=? where order_id=? and status='processing'",
                        (time.time(), order_id))
            audit(s["username"], self.ip, "Webshop-Auslieferung angenommen", "%s · %s · %s" % (order_id, product_id, player), con=con)
        return self.send_json(200, {"status": "ok", "data": {"order_id": order_id, "state": "delivered", "duplicate": False}})


def ip_ok(ip, allowed):
    """IP freigegeben? Exakt – oder bei IPv6 im selben /64-Netz (Privacy-Adressen wechseln, der Anschluss bleibt)."""
    if ip in allowed:
        return True
    try:
        a = ipaddress.ip_address(ip)
    except ValueError:
        return False
    if a.version != 6:
        return False
    net = ipaddress.ip_network(ip + "/64", strict=False)
    for x in allowed:
        try:
            if ipaddress.ip_address(x) in net:
                return True
        except ValueError:
            pass
    return False


def trusted_proxy(ip):
    """Nur das Docker-Netz des Web-Panels (pxmx-net) darf eine Client-IP weiterreichen."""
    try:
        a = ipaddress.ip_address(ip)
        return any(a in ipaddress.ip_network(n, strict=False) for n in conf().get("trusted_proxies", ["172.30.42.0/24"]))
    except ValueError:
        return False


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True
    address_family = __import__("socket").AF_INET6

    def server_bind(self):
        import socket
        try:
            self.socket.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        except OSError:
            pass
        super().server_bind()

    def finish_request(self, request, client_address):
        request.settimeout(60)
        try:
            tls = self.ctx.wrap_socket(request, server_side=True)
        except Exception:
            try:
                request.close()
            except Exception:
                pass
            return
        self.RequestHandlerClass(tls, client_address, self)


# ───────────────────────────── owner CLI (via SSH) ─────────────────────────────
def link_player(data):
    """Validates mc_name / role of a create/update request -> (mc_name, mc_uuid, role) or raises ValueError."""
    mc = str(data.get("mc_name", "") or "").strip()
    role = str(data.get("role", "") or "").strip().lower()
    mc_uuid = ""
    if mc:
        if not MC_RX.match(mc):
            raise ValueError("Ungültiger Minecraft-Name.")
        found = lp_player(mc)
        if not found:
            raise ValueError("Spieler „%s“ ist LuckPerms unbekannt – er muss einmal auf dem Server gewesen sein." % mc)
        mc_uuid, mc = found
        role = ""
    elif role:
        if not ROLE_RX.match(role):
            raise ValueError("Ungültiger Rang.")
    else:
        raise ValueError("Entweder einen Minecraft-Spieler verknüpfen oder einen festen Rang wählen.")
    return mc, mc_uuid, role


def admin(cmd, arg):
    data = json.loads(base64.b64decode(arg).decode()) if arg else {}
    c = conf()
    out = {"ok": True}
    with db() as con:
        if cmd == "state":
            try:
                groups = lp_groups()
                gerr = ""
            except Exception as e:
                groups, gerr = {}, str(e)
            now = time.time()
            out = {
                "ok": True, "version": VERSION, "perms": PERMS, "roles": c.get("roles", {}), "groups": groups, "groups_error": gerr,
                "blocked_commands": c.get("blocked_commands", []), "port": c.get("port"),
                "tfa_required_ranks": c.get("tfa_required_ranks", []),
                "users": [{k: r[k] for k in r.keys() if k not in ("pw", "totp_secret", "recovery", "totp_last")} for r in con.execute("select * from users order by username")],
                "requests": [dict(r) for r in con.execute("select * from ip_requests where status='pending' order by time desc")],
                "sessions": [dict(r) | {"token_hash": r["token_hash"][:12]} for r in
                             con.execute("select * from sessions where expires > ? order by last_seen desc", (now,))],
                "audit": [dict(r) for r in con.execute("select * from audit order by id desc limit 400")],
            }
        elif cmd == "roles":
            roles = {g: [p for p in ps if p in PERM_KEYS] for g, ps in data.items() if ROLE_RX.match(g)}
            c["roles"] = {g: ps for g, ps in roles.items() if ps}
            save_conf(c)
            audit("Owner", "ssh", "Rechte geändert", json.dumps(c["roles"])[:500], con=con)
            # Running sessions get the new rights immediately. The rank is resolved again: a newly enabled LuckPerms group
            # can now be a member's highest panel rank (before, only the stored rank's rights were copied).
            for s in con.execute("select s.token_hash as th, s.rank as srank, u.mc_uuid as mc_uuid, u.role as role "
                                 "from sessions s join users u on u.username = s.username").fetchall():
                try:
                    rank, ps = resolve_rank(s)
                    rank = rank or ""
                except Exception:
                    rank, ps = s["srank"], [p for p in c["roles"].get(s["srank"], []) if p in PERM_KEYS]
                con.execute("update sessions set rank=?, perms=? where token_hash=?", (rank, json.dumps(ps), s["th"]))
        elif cmd == "user":
            name, action = str(data.get("username", "")).strip().lower(), data.get("action")
            if not USER_RX.match(name):
                return {"ok": False, "error": "Benutzername: 3–32 Zeichen, nur a–z, 0–9, _ . -"}
            u = con.execute("select * from users where username=?", (name,)).fetchone()
            if action != "create" and not u:
                return {"ok": False, "error": "Account unbekannt"}
            if action == "create":
                if u:
                    return {"ok": False, "error": "Diesen Benutzernamen gibt es schon."}
                prob = pw_problem(str(data.get("password", "")))
                if prob:
                    return {"ok": False, "error": prob}
                try:
                    mc, mc_uuid, role = link_player(data)
                except ValueError as e:
                    return {"ok": False, "error": str(e)}
                con.execute("insert into users (username, pw, mc_name, mc_uuid, role, created, must_change, note) values (?,?,?,?,?,?,?,?)",
                            (name, hash_pw(data["password"]), mc, mc_uuid, role, time.time(), 1 if data.get("must_change", True) else 0,
                             str(data.get("note", ""))[:200]))
                audit("Owner", "ssh", "Account angelegt", name + (" → " + mc if mc else " · Rang " + role), con=con)
            elif action == "update":
                try:
                    mc, mc_uuid, role = link_player(data)
                except ValueError as e:
                    return {"ok": False, "error": str(e)}
                con.execute("update users set mc_name=?, mc_uuid=?, role=?, note=? where username=?",
                            (mc, mc_uuid, role, str(data.get("note", u["note"] or ""))[:200], name))
                con.execute("delete from sessions where username=?", (name,))
                audit("Owner", "ssh", "Account geändert", name + (" → " + mc if mc else " · Rang " + role), con=con)
            elif action == "password":
                prob = pw_problem(str(data.get("password", "")))
                if prob:
                    return {"ok": False, "error": prob}
                con.execute("update users set pw=?, must_change=1, fails=0, locked_until=0 where username=?", (hash_pw(data["password"]), name))
                con.execute("delete from sessions where username=?", (name,))
                audit("Owner", "ssh", "Passwort zurückgesetzt", name, con=con)
            elif action == "freeze":
                con.execute("update users set frozen=1 where username=?", (name,))
                con.execute("delete from sessions where username=?", (name,))
                audit("Owner", "ssh", "Account gesperrt", name, con=con)
            elif action == "unfreeze":
                con.execute("update users set frozen=0, fails=0, locked_until=0 where username=?", (name,))
                audit("Owner", "ssh", "Account entsperrt", name, con=con)
            elif action == "kick":
                con.execute("delete from sessions where username=?", (name,))
                audit("Owner", "ssh", "Abmeldung erzwungen", name, con=con)
            elif action == "delete":
                con.execute("delete from sessions where username=?", (name,))
                con.execute("delete from users where username=?", (name,))
                con.execute("delete from ip_requests where username=?", (name,))
                audit("Owner", "ssh", "Account gelöscht", name, con=con)
            else:
                return {"ok": False, "error": "unbekannte Aktion"}
        elif cmd == "ip":
            name, ip, action = str(data.get("username", "")).lower(), data.get("ip", ""), data.get("action")
            u = con.execute("select * from users where username=?", (name,)).fetchone()
            if not u:
                return {"ok": False, "error": "Account unbekannt"}
            ips = json.loads(u["ips"] or "[]")
            if action in ("approve", "add") and trusted_proxy(ip):
                return {"ok": False, "error": "Das ist eine interne Docker-Adresse des Web-Panels und kann nicht freigegeben werden."}
            if action == "approve":
                if ip not in ips:
                    ips.append(ip)
                con.execute("update ip_requests set status='approved' where username=? and ip=?", (name, ip))
            elif action == "deny":
                con.execute("update ip_requests set status='denied' where username=? and ip=?", (name, ip))
            elif action == "remove":
                ips = [x for x in ips if x != ip]
                con.execute("delete from sessions where username=? and ip=?", (name, ip))
            elif action == "add" and re.match(r"^[0-9a-fA-F:.]{3,45}$", ip):
                if ip not in ips:
                    ips.append(ip)
            con.execute("update users set ips=? where username=?", (json.dumps(ips), name))
            audit(name, "ssh", "IP " + str(action), ip, con=con)
        elif cmd == "apikeys":
            out = {"ok": True, "api_perms": API_PERMS,
                   "keys": [{k: r[k] for k in r.keys() if k != "token_hash"} for r in con.execute("select * from api_keys order by created desc")]}
        elif cmd == "apikey":
            name, action = str(data.get("name", "")).strip().lower(), data.get("action")
            if action == "create":
                if not API_NAME_RX.match(name):
                    return {"ok": False, "error": "Name: 3–32 Zeichen, nur a–z, 0–9, _ und -."}
                ps = [p for p in (data.get("perms") or []) if p in API_PERMS]
                if not ps:
                    return {"ok": False, "error": "Bitte mindestens ein Recht wählen."}
                ips = [str(x).strip() for x in (data.get("ips") or []) if str(x).strip()]
                if not ips or any(not re.match(r"^[0-9a-fA-F:.]{3,45}$", x) or trusted_proxy(x) for x in ips):
                    return {"ok": False, "error": "Mindestens eine gültige, öffentliche IP-Adresse angeben (IPv6: gilt für das /64-Netz)."}
                try:
                    tage = max(0, min(3650, int(data.get("tage") or 0)))
                except (ValueError, TypeError):
                    tage = 0
                if con.execute("select 1 from api_keys where name=?", (name,)).fetchone():
                    return {"ok": False, "error": "Diesen Namen gibt es schon."}
                token = API_PREFIX + secrets.token_urlsafe(32)
                now = time.time()
                con.execute("insert into api_keys (name, token_hash, prefix, perms, ips, created, created_by, expires, note) values (?,?,?,?,?,?,?,?,?)",
                            (name, thash(token), token[:10], json.dumps(ps), json.dumps(ips), now, "Owner", now + tage * 86400 if tage else 0,
                             str(data.get("note", ""))[:200]))
                audit("Owner", "ssh", "API-Schlüssel angelegt", "%s · Rechte: %s · IPs: %s" % (name, ", ".join(ps), ", ".join(ips)), con=con)
                out = {"ok": True, "name": name, "token": token}   # nur dieses eine Mal sichtbar
            elif action == "revoke":
                r = con.execute("update api_keys set revoked=1 where name=? and revoked=0", (name,)).rowcount
                audit("Owner", "ssh", "API-Schlüssel gesperrt", name, "ok" if r else "unbekannt", con=con)
                if not r:
                    return {"ok": False, "error": "Schlüssel unbekannt oder schon gesperrt."}
            else:
                return {"ok": False, "error": "unbekannte Aktion"}
        elif cmd == "tfa":
            name, action = str(data.get("username", "")).strip().lower(), data.get("action")
            if action == "reset":
                u = con.execute("select 1 from users where username=?", (name,)).fetchone()
                if not u:
                    return {"ok": False, "error": "Account unbekannt"}
                con.execute("update users set totp_on=0, totp_secret='', totp_last=0, recovery='[]' where username=?", (name,))
                con.execute("delete from sessions where username=?", (name,))
                audit("Owner", "ssh", "2FA zurückgesetzt", name, con=con)
            else:
                return {"ok": False, "error": "unbekannte Aktion"}
        elif cmd == "session":
            action = data.get("action")
            if action == "kick":
                h = str(data.get("hash", "")).strip().lower()
                if not re.match(r"^[0-9a-f]{8,64}$", h):
                    return {"ok": False, "error": "Ungültige Sitzung."}
                rows = con.execute("select token_hash, username from sessions where token_hash like ?", (h + "%",)).fetchall()
                if len(rows) != 1:
                    return {"ok": False, "error": "Sitzung nicht gefunden (evtl. schon beendet)."}
                con.execute("delete from sessions where token_hash=?", (rows[0]["token_hash"],))
                audit("Owner", "ssh", "Sitzung beendet", rows[0]["username"], con=con)
            elif action == "kickall":
                n = con.execute("delete from sessions").rowcount
                audit("Owner", "ssh", "Alle Sitzungen beendet", str(n), con=con)
            else:
                return {"ok": False, "error": "unbekannte Aktion"}
        elif cmd == "audit":
            q, who, res = str(data.get("q", "")).strip().lower(), str(data.get("who", "")).strip().lower(), str(data.get("result", "")).strip()
            sql, args = "select * from audit where 1=1", []
            if q:
                sql += " and (lower(action) like ? or lower(detail) like ? or lower(ip) like ?)"
                args += ["%" + q + "%"] * 3
            if who:
                sql += " and lower(name)=?"
                args.append(who)
            if res == "ok":
                sql += " and result='ok'"
            elif res == "fehler":
                sql += " and result!='ok'"
            try:
                since = float(data.get("since") or 0)
            except (ValueError, TypeError):
                since = 0
            if since > 0:
                sql += " and time>=?"
                args.append(since)
            try:
                lim = max(1, min(2000, int(data.get("limit") or 400)))
            except (ValueError, TypeError):
                lim = 400
            out = {"ok": True, "audit": [dict(r) for r in con.execute(sql + " order by id desc limit ?", args + [lim])]}
        elif cmd == "config":
            if "blocked_commands" in data:
                c["blocked_commands"] = [str(b).strip().lower() for b in data["blocked_commands"] if str(b).strip()][:300]
            if "tfa_required_ranks" in data:
                c["tfa_required_ranks"] = sorted({str(x) for x in data["tfa_required_ranks"] if ROLE_RX.match(str(x))})
                audit("Owner", "ssh", "2FA-Pflicht geändert", ", ".join(c["tfa_required_ranks"]) or "keine", con=con)
            save_conf(c)
            audit("Owner", "ssh", "Einstellungen geändert", con=con)
        else:
            out = {"ok": False, "error": "unbekannter Befehl"}
    return out


def init_from_env():
    os.makedirs(os.path.dirname(CONF), exist_ok=True)
    c = dict(conf())
    for env, key in (("RP_CRAFTY_TOKEN", "crafty_token"), ("RP_LP_CONFIG", "luckperms_config")):
        v = os.environ.get(env, "").strip()
        if v:
            c[key] = v
    if os.environ.get("RP_PORT", "").isdigit():
        c["port"] = int(os.environ["RP_PORT"])
    c.setdefault("cert", "/etc/pxmx/gw.crt")
    c.setdefault("key", "/etc/pxmx/gw.key")
    save_conf(c)
    init_db()


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--init":
        init_from_env()
        print("ok")
        return
    if len(sys.argv) > 2 and sys.argv[1] == "admin":
        init_db()
        arg = sys.argv[3] if len(sys.argv) > 3 else ""
        if arg == "-":  # payload via stdin – keeps passwords out of the process list
            arg = sys.stdin.read().strip()
        print(json.dumps(admin(sys.argv[2], arg), ensure_ascii=False, default=str))
        return
    init_db()
    c = conf()
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.load_cert_chain(c["cert"], c["key"])
    srv = Server(("::", int(c["port"])), Handler)
    srv.ctx = ctx
    print("PXMX Gateway %s auf Port %s" % (VERSION, c["port"]), flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
