#!/usr/bin/env python3
"""
RyvenPanel Netzwerk-Monitor – unabhängige Messungen außerhalb von Docker/Crafty/Minecraft.

Dienst (systemd pxmx-netmon):  python3 netmon.py dienst
Abfragen (Gateway, /panel/netz):     python3 netmon.py <cmd> <base64-json-args>
  status | verlauf {stunden} | ereignisse {tage} | export {von, bis} (Unix-Sekunden) | pruefen

Alle 10 s: Ping (5 Pakete) zu Gateway/Cloudflare/Google/Quad9, RX/TX der Haupt-Netzwerkkarte aus /proc/net/dev,
TCP-Gegenprobe Minecraft-Port, NTP-Status. Jede Zeile ist per SHA-256 mit der vorherigen verkettet (Manipulation erkennbar).
Lücken > 30 s zwischen zwei Messungen = Messung ausgefallen (VM eingefroren/aus) → Tabelle luecken, mit Neustart-Erkennung (boot_id).
"""
import base64, hashlib, json, os, socket, sqlite3, subprocess, sys, threading, time
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

DB = "/var/lib/pxmx/netmon.db"
INTERVALL = 10
LUECKE = 30                      # Sekunden ohne Messung, ab denen es als Ausfall zählt
AUFHEBEN_TAGE = 120
BERLIN = ZoneInfo("Europe/Berlin")
ZIELE_FEST = [("cloudflare", "1.1.1.1", "Cloudflare DNS"), ("google", "8.8.8.8", "Google DNS"), ("quad9", "9.9.9.9", "Quad9 DNS")]
MC_PORT = ("127.0.0.1", 25565)
TCP_ZIELE = [("cloudflare", "1.1.1.1"), ("google", "8.8.8.8"), ("quad9", "9.9.9.9")]
TCP_LANGSAM_MS = 900           # SYN-Neuversand nach 1 s → Verbindungsaufbau > 900 ms = Paket verloren


def db():
    c = sqlite3.connect(DB, timeout=10)
    c.execute("PRAGMA journal_mode=WAL")
    c.executescript("""
      CREATE TABLE IF NOT EXISTS messung (ts INTEGER PRIMARY KEY, iface TEXT, rx_bps REAL, tx_bps REAL, rx_pps REAL, tx_pps REAL,
        rx_err INTEGER, tx_err INTEGER, rx_drop INTEGER, tx_drop INTEGER, ping TEXT, mc_ms REAL, ntp INTEGER, boot TEXT, hash TEXT);
      CREATE TABLE IF NOT EXISTS luecke (start INTEGER, ende INTEGER, sekunden INTEGER, neustart INTEGER, boot_alt TEXT, boot_neu TEXT,
        PRIMARY KEY (start));
      CREATE TABLE IF NOT EXISTS info (k TEXT PRIMARY KEY, v TEXT);""")
    try: c.execute("ALTER TABLE messung ADD COLUMN tcp TEXT")     # ab 2026-11-16: TCP-Messung (in der Hash-Kette)
    except sqlite3.OperationalError: pass
    return c


# ───────────────────────────── Messen ─────────────────────────────

def default_iface():
    try:
        for z in open("/proc/net/route").read().splitlines()[1:]:
            t = z.split()
            if t[1] == "00000000" and int(t[3], 16) & 2:
                gw = socket.inet_ntoa(bytes.fromhex(t[2])[::-1])
                return t[0], gw
    except OSError:
        pass
    return None, None


def zaehler(iface):
    for z in open("/proc/net/dev").read().splitlines()[2:]:
        name, rest = z.split(":", 1)
        if name.strip() == iface:
            f = [int(x) for x in rest.split()]
            return {"rx_b": f[0], "rx_p": f[1], "rx_err": f[2], "rx_drop": f[3], "tx_b": f[8], "tx_p": f[9], "tx_err": f[10], "tx_drop": f[11]}
    return None


def ping(ip, n=5):
    try:
        r = subprocess.run(["ping", "-n", "-q", "-c", str(n), "-i", "0.2", "-W", "1", ip], capture_output=True, text=True, timeout=n * 1.5 + 3)
        out = r.stdout
        gesendet = empfangen = 0
        for z in out.splitlines():
            if "transmitted" in z:
                t = z.split(",")
                gesendet = int(t[0].split()[0]); empfangen = int(t[1].split()[0])
        rtt = None
        for z in out.splitlines():
            if "min/avg/max" in z:
                w = z.split("=")[1].split()[0].split("/")
                rtt = {"min": float(w[0]), "avg": float(w[1]), "max": float(w[2])}
        verlust = 100.0 if gesendet == 0 else round((gesendet - empfangen) * 100.0 / gesendet, 1)
        return {"verlust": verlust, "rtt": rtt["avg"] if rtt else None, "max": rtt["max"] if rtt else None}
    except Exception:
        return {"verlust": 100.0, "rtt": None, "max": None}


def tcp_ms(adr):
    t = time.monotonic()
    try:
        with socket.create_connection(adr, timeout=2):
            return round((time.monotonic() - t) * 1000, 2)
    except OSError:
        return None


def tcp_probe(ip, n=5, port=443):
    """TCP-Verbindungsaufbau n× parallel (50 ms versetzt). Verlust = gescheitert oder > 900 ms (SYN musste neu gesendet werden,
    d. h. ein Paket ging wirklich verloren – Router filtern TCP nicht wie Pings)."""
    res = [None] * n
    def eins(i):
        time.sleep(i * 0.05)
        t = time.monotonic()
        try:
            s = socket.create_connection((ip, port), timeout=2.5); s.close()
            res[i] = (time.monotonic() - t) * 1000
        except OSError:
            res[i] = -1.0
    th = [threading.Thread(target=eins, args=(i,)) for i in range(n)]
    for t in th: t.start()
    for t in th: t.join()
    gut = [x for x in res if x is not None and 0 <= x < TCP_LANGSAM_MS]
    da = [x for x in res if x is not None and x >= 0]
    return {"verlust": round((n - len(gut)) * 100.0 / n, 1), "rtt": round(sum(gut) / len(gut), 2) if gut else None,
            "max": round(max(da), 1) if da else None, "neu": sum(1 for x in da if x >= TCP_LANGSAM_MS), "fehl": sum(1 for x in res if x is not None and x < 0)}


def ntp_ok():
    try:
        r = subprocess.run(["timedatectl", "show", "-p", "NTPSynchronized", "--value"], capture_output=True, text=True, timeout=5)
        return 1 if r.stdout.strip() == "yes" else 0
    except Exception:
        return None


def boot_id():
    try: return open("/proc/sys/kernel/random/boot_id").read().strip()
    except OSError: return ""


def dienst():
    c = db()
    letzte = c.execute("SELECT ts, hash, boot FROM messung ORDER BY ts DESC LIMIT 1").fetchone()
    prev_hash = letzte[1] if letzte else "0" * 64
    alt = None
    ntp, ntp_zeit = None, 0
    while True:
        start = time.time()
        iface, gw = default_iface()
        ziele = ([("gateway", gw, "Gateway des Hosters")] if gw else []) + ZIELE_FEST
        erg = {}
        tcp = {}
        th = [threading.Thread(target=lambda k=k, ip=ip: erg.__setitem__(k, ping(ip))) for k, ip, _ in ziele]
        th += [threading.Thread(target=lambda k=k, ip=ip: tcp.__setitem__(k, tcp_probe(ip))) for k, ip in TCP_ZIELE]
        for t in th: t.start()
        mc = tcp_ms(MC_PORT)
        if time.time() - ntp_zeit > 60: ntp, ntp_zeit = ntp_ok(), time.time()
        for t in th: t.join()
        z = zaehler(iface) if iface else None
        ts = int(time.time())
        rate = {}
        if z and alt and alt[1] == iface and ts > alt[0]:
            dt = ts - alt[0]; a = alt[2]
            rate = {"rx_bps": (z["rx_b"] - a["rx_b"]) * 8 / dt, "tx_bps": (z["tx_b"] - a["tx_b"]) * 8 / dt,
                    "rx_pps": (z["rx_p"] - a["rx_p"]) / dt, "tx_pps": (z["tx_p"] - a["tx_p"]) / dt}
        if z: alt = (ts, iface, z)
        boot = boot_id()
        # Lücke seit der letzten Messung?
        if letzte and ts - letzte[0] > LUECKE:
            c.execute("INSERT OR IGNORE INTO luecke VALUES (?,?,?,?,?,?)",
                      (letzte[0], ts, ts - letzte[0], 1 if letzte[2] != boot else 0, letzte[2], boot))
        p = {k: erg.get(k) for k, _, _ in ziele}
        zeile = [ts, iface, rate.get("rx_bps"), rate.get("tx_bps"), rate.get("rx_pps"), rate.get("tx_pps"),
                 z and z["rx_err"], z and z["tx_err"], z and z["rx_drop"], z and z["tx_drop"],
                 json.dumps(p, sort_keys=True), mc, ntp, boot]
        tj = json.dumps(tcp, sort_keys=True)
        h = hashlib.sha256((prev_hash + json.dumps(zeile + [tj], sort_keys=True)).encode()).hexdigest()
        try:
            c.execute("INSERT OR REPLACE INTO messung (ts, iface, rx_bps, tx_bps, rx_pps, tx_pps, rx_err, tx_err, rx_drop, tx_drop, ping, mc_ms, ntp, boot, hash, tcp) "
                      "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", zeile + [h, tj])
            c.execute("INSERT OR REPLACE INTO info VALUES ('ziele', ?)", (json.dumps({k: [ip, n] for k, ip, n in ziele}),))
            if ts % 86400 < INTERVALL:
                c.execute("DELETE FROM messung WHERE ts < ?", (ts - AUFHEBEN_TAGE * 86400,))
            c.commit()
            prev_hash, letzte = h, (ts, h, boot)
        except sqlite3.Error as e:
            print("DB-Fehler:", e, file=sys.stderr, flush=True)
        time.sleep(max(1, INTERVALL - (time.time() - start)))


# ───────────────────────────── Abfragen ─────────────────────────────

def zeiten(ts):
    return {"utc": datetime.fromtimestamp(ts, timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC"),
            "berlin": datetime.fromtimestamp(ts, BERLIN).strftime("%d.%m.%Y %H:%M:%S")}


def ziele_info(c):
    r = c.execute("SELECT v FROM info WHERE k='ziele'").fetchone()
    return json.loads(r[0]) if r else {}


def mittel(werte):
    return round(sum(werte) / len(werte), 2) if werte else None


def status(c, a):
    jetzt = int(time.time())
    r = c.execute("SELECT * FROM messung ORDER BY ts DESC LIMIT 1").fetchone()
    if not r:
        return {"status": "ok", "laeuft": False, "hinweis": "Noch keine Messungen."}
    spalten = [d[0] for d in c.execute("SELECT * FROM messung LIMIT 0").description]
    m = dict(zip(spalten, r)); m["ping"] = json.loads(m["ping"]); m["tcp"] = json.loads(m.get("tcp") or "{}")
    seit = jetzt - 86400

    def sammeln(von):
        z = {"ping": {}, "tcp": {}}
        for pj, tj in c.execute("SELECT ping, tcp FROM messung WHERE ts >= ?", (von,)):
            for art, js in (("ping", pj), ("tcp", tj)):
                for k, v in json.loads(js or "{}").items():
                    if not v: continue
                    s = z[art].setdefault(k, {"v": [], "r": [], "max": 0})
                    s["v"].append(v["verlust"])
                    if v.get("rtt") is not None: s["r"].append(v["rtt"])
                    s["max"] = max(s["max"], v.get("max") or 0)
        return {art: {k: {"verlust": mittel(s["v"]), "rtt": mittel(s["r"]), "max": s["max"]} for k, s in d.items()} for art, d in z.items()}

    h24, m1 = sammeln(seit), sammeln(jetzt - 65)        # „jetzt“ = Mittel der letzten Minute
    ext_ping = [v["verlust"] for k, v in m1["ping"].items() if k != "gateway"]
    ext_tcp = [v["verlust"] for v in m1["tcp"].values()]
    n24 = c.execute("SELECT COUNT(*) FROM messung WHERE ts >= ?", (seit,)).fetchone()[0]
    l24 = c.execute("SELECT COUNT(*), COALESCE(SUM(sekunden),0) FROM luecke WHERE ende >= ?", (seit,)).fetchone()
    erste = c.execute("SELECT MIN(ts) FROM messung").fetchone()[0]
    return {"status": "ok", "laeuft": jetzt - m["ts"] < 3 * INTERVALL, "alter": jetzt - m["ts"], "letzte": m, "zeit": zeiten(m["ts"]),
            "min1": m1, "h24": h24["ping"], "h24_tcp": h24["tcp"],
            "verlust_1min": {k: v["verlust"] for k, v in m1["ping"].items()},
            "verlust_extern_1min": mittel(ext_ping), "tcp_extern_1min": mittel(ext_tcp),
            "ziele": ziele_info(c), "tcp_ziele": {k: ip for k, ip in TCP_ZIELE},
            "messungen_24h": n24, "erwartet_24h": min(86400, jetzt - erste) // INTERVALL if erste else 0,
            "luecken_24h": l24[0], "ausfall_24h_s": l24[1], "seit": zeiten(erste) if erste else None, "intervall": INTERVALL}


def verlauf(c, a):
    try: std = float(a.get("stunden", 6))
    except (TypeError, ValueError): std = 6
    std = max(0.25, min(24 * 30, std))
    bis = int(time.time()); von = bis - int(std * 3600)
    erste = c.execute("SELECT MIN(ts) FROM messung").fetchone()[0]
    if erste and erste > von: von = erste            # Diagramm beginnt mit der ersten Messung (davor ist kein Ausfall)
    b = max(INTERVALL, (bis - von) // 300)
    luecken = c.execute("SELECT start, ende FROM luecke WHERE ende >= ?", (von,)).fetchall()
    eimer = {}
    for ts, pj, tj, rx, tx, mc in c.execute("SELECT ts, ping, tcp, rx_bps, tx_bps, mc_ms FROM messung WHERE ts >= ? ORDER BY ts", (von,)):
        e = eimer.setdefault((ts - von) // b, {"n": 0, "rx": [], "tx": [], "mc": 0, "p": {}, "q": {}})
        e["n"] += 1
        if rx is not None: e["rx"].append(rx); e["tx"].append(tx)
        if mc is None: e["mc"] += 1
        for feld, js in (("p", pj), ("q", tj)):
            for k, v in json.loads(js or "{}").items():
                if not v: continue
                p = e[feld].setdefault(k, {"v": [], "r": []})
                p["v"].append(v["verlust"])
                if v.get("rtt") is not None: p["r"].append(v["rtt"])
    aus = []
    for i in range((bis - von) // b + 1):
        e = eimer.get(i)
        t = von + i * b
        if not e:
            # rot (leer) nur, wenn der Eimer in einem erfassten Messausfall liegt
            aus.append({"t": t, "leer": any(s < t + b and en > t for s, en in luecken)}); continue
        zus = lambda d: {k: {"verlust": round(sum(p["v"]) / len(p["v"]), 1), "rtt": mittel(p["r"])} for k, p in d.items()}
        aus.append({"t": t, "rx": mittel(e["rx"]), "tx": mittel(e["tx"]), "mc_aus": e["mc"], "p": zus(e["p"]), "q": zus(e["q"])})
    return {"status": "ok", "von": von, "bis": bis, "schritt": b, "punkte": aus, "ziele": ziele_info(c), "tcp_ziele": {k: ip for k, ip in TCP_ZIELE}}


def ereignisse(c, a):
    tage = max(1, min(AUFHEBEN_TAGE, int(a.get("tage", 7))))
    von = int(time.time()) - tage * 86400
    ev = []
    for s, e, sek, neu in c.execute("SELECT start, ende, sekunden, neustart FROM luecke WHERE ende >= ? ORDER BY start DESC", (von,)):
        ev.append({"art": "neustart" if neu else "luecke", "start": s, "ende": e, "sekunden": sek, "von": zeiten(s), "bis": zeiten(e),
                   "text": ("Server neu gestartet – keine Messungen" if neu else "Keine Messungen (VM eingefroren / nicht erreichbar)")})
    # Verlust-Phasen: TCP-Verlust bei ≥ 2 Zielen ≥ 20 % (echter Verlust) oder Ping bei ≥ 2 externen Zielen ≥ 20 %
    phase = None
    for ts, pj, tj, mc in c.execute("SELECT ts, ping, tcp, mc_ms FROM messung WHERE ts >= ? ORDER BY ts", (von,)):
        p, q = json.loads(pj), json.loads(tj or "{}")
        tcp_s = [v["verlust"] for v in q.values() if v and v["verlust"] >= 20]
        ping_s = [v["verlust"] for k, v in p.items() if v and k != "gateway" and v["verlust"] >= 20]
        if len(tcp_s) >= 2 or len(ping_s) >= 2:
            if phase and ts - phase["ende"] <= 2 * INTERVALL:
                phase["ende"] = ts
            else:
                if phase: ev.append(phase_ende(phase))
                phase = {"start": ts, "ende": ts, "tcp": 0, "ping": 0, "mc": 0}
            phase["tcp"] = max(phase["tcp"], max(tcp_s or [0])); phase["ping"] = max(phase["ping"], max(ping_s or [0]))
            phase["mc"] += 1 if mc is None else 0
        elif phase and ts - phase["ende"] > 2 * INTERVALL:
            ev.append(phase_ende(phase)); phase = None
    if phase: ev.append(phase_ende(phase))
    ev.sort(key=lambda x: -x["start"])
    return {"status": "ok", "ereignisse": ev[:500], "tage": tage}


def phase_ende(p):
    sek = p["ende"] - p["start"] + INTERVALL
    teile = []
    if p["tcp"]: teile.append("TCP-Verlust bis %d %%" % p["tcp"])
    if p["ping"]: teile.append("Ping-Verlust bis %d %%" % p["ping"])
    if p["mc"]: teile.append("Minecraft-Port %d× nicht erreichbar" % p["mc"])
    return {"art": "verlust", "start": p["start"], "ende": p["ende"] + INTERVALL, "sekunden": sek, "von": zeiten(p["start"]), "bis": zeiten(p["ende"] + INTERVALL),
            "text": "Paketverlust nach außen: " + " · ".join(teile)}


def pruefen(c, a=None):
    prev, n, fehler, erste_fehler = None, 0, 0, None
    for r in c.execute("SELECT ts, iface, rx_bps, tx_bps, rx_pps, tx_pps, rx_err, tx_err, rx_drop, tx_drop, ping, mc_ms, ntp, boot, hash, tcp FROM messung ORDER BY ts"):
        zeile, h = list(r[:14]) + ([r[15]] if r[15] is not None else []), r[14]   # Zeilen vor der TCP-Messung ohne tcp-Feld
        if prev is not None:
            if hashlib.sha256((prev + json.dumps(zeile, sort_keys=True)).encode()).hexdigest() != h:
                fehler += 1; erste_fehler = erste_fehler or r[0]
        prev, n = h, n + 1
    return {"status": "ok", "geprueft": n, "fehler": fehler, "erster_fehler": zeiten(erste_fehler) if erste_fehler else None,
            "hinweis": "Die erste gespeicherte Zeile ist der Anker (ältere Zeilen werden nach %d Tagen gelöscht)." % AUFHEBEN_TAGE}


def export(c, a):
    bis = int(a.get("bis") or time.time()); von = int(a.get("von") or bis - 86400)
    if bis - von > 31 * 86400: von = bis - 31 * 86400
    zi = ziele_info(c)
    keys = list(zi.keys())
    tk = [k for k, _ in TCP_ZIELE]
    kopf = ["zeit_utc", "zeit_berlin", "unix", "iface", "rx_mbit_s", "tx_mbit_s", "rx_pakete_s", "tx_pakete_s", "rx_fehler", "tx_fehler", "rx_drops", "tx_drops"]
    for k in keys: kopf += ["ping_%s_verlust_prozent" % k, "ping_%s_ms" % k]
    for k in tk: kopf += ["tcp_%s_verlust_prozent" % k, "tcp_%s_ms" % k, "tcp_%s_neu_gesendet" % k]
    kopf += ["minecraft_port_ms", "ntp_synchron", "boot_id", "sha256_kette"]
    zeilen = [";".join(kopf)]
    def f(x, d=3): return "" if x is None else (("%." + str(d) + "f") % x).replace(".", ",")
    for r in c.execute("SELECT ts, iface, rx_bps, tx_bps, rx_pps, tx_pps, rx_err, tx_err, rx_drop, tx_drop, ping, mc_ms, ntp, boot, hash, tcp FROM messung WHERE ts BETWEEN ? AND ? ORDER BY ts", (von, bis)):
        z = zeiten(r[0]); p = json.loads(r[10]); q = json.loads(r[15] or "{}")
        w = [z["utc"], z["berlin"], str(r[0]), r[1] or "", f(r[2] and r[2] / 1e6), f(r[3] and r[3] / 1e6), f(r[4], 1), f(r[5], 1),
             str(r[6] or 0), str(r[7] or 0), str(r[8] or 0), str(r[9] or 0)]
        for k in keys:
            v = p.get(k) or {}
            w += [f(v.get("verlust"), 1), f(v.get("rtt"), 2)]
        for k in tk:
            v = q.get(k) or {}
            w += [f(v.get("verlust"), 1), f(v.get("rtt"), 2), "" if not v else str(v.get("neu", 0) + v.get("fehl", 0))]
        w += [f(r[11], 2) if r[11] is not None else "nicht erreichbar", "ja" if r[12] == 1 else ("nein" if r[12] == 0 else ""), r[13] or "", r[14]]
        zeilen.append(";".join(w))
    luecken = ["", "# Ausfälle der Messung (keine Messung > %d s):" % LUECKE, "# start_berlin;ende_berlin;sekunden;neustart"]
    for s, e, sek, neu in c.execute("SELECT start, ende, sekunden, neustart FROM luecke WHERE ende >= ? AND start <= ? ORDER BY start", (von, bis)):
        luecken.append("# %s;%s;%d;%s" % (zeiten(s)["berlin"], zeiten(e)["berlin"], sek, "ja" if neu else "nein"))
    pr = pruefen(c)
    fuss = ["", "# Ping-Ziele: " + ", ".join("%s=%s (%s)" % (k, v[0], v[1]) for k, v in zi.items()),
            "# TCP-Ziele (Port 443): " + ", ".join("%s=%s" % (k, ip) for k, ip in TCP_ZIELE),
            "# Messintervall %d s. Ping: je Ziel 5 ICMP-Pakete (Router drosseln Pings an sich selbst – Gateway-Werte nur bedingt aussagekräftig)." % INTERVALL,
            "# TCP: je Ziel 5 Verbindungsaufbauten; Verlust = gescheitert oder > %d ms (SYN musste neu gesendet werden = Paket verloren)." % TCP_LANGSAM_MS,
            "# Zeitquelle: Systemuhr, NTP-Status je Zeile.",
            "# Integrität: %d Zeilen per SHA-256 verkettet geprüft, %d Abweichungen." % (pr["geprueft"], pr["fehler"]),
            "# Erstellt: " + zeiten(int(time.time()))["berlin"] + " (Europe/Berlin)"]
    return {"status": "ok", "dateiname": "netzwerk_%s_bis_%s.csv" % (datetime.fromtimestamp(von, BERLIN).strftime("%Y-%m-%d_%H%M"),
                                                                     datetime.fromtimestamp(bis, BERLIN).strftime("%Y-%m-%d_%H%M")),
            "csv": "﻿" + "\n".join(zeilen + luecken + fuss) + "\n", "zeilen": len(zeilen) - 1}


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "dienst":
        os.makedirs(os.path.dirname(DB), exist_ok=True)
        dienst()
    args = {}
    if len(sys.argv) > 2 and sys.argv[2]:
        try: args = json.loads(base64.b64decode(sys.argv[2]).decode() or "{}")
        except Exception: args = {}
    try:
        if not os.path.exists(DB):
            print(json.dumps({"status": "error", "error": "Der Netzwerk-Monitor läuft noch nicht (keine Datenbank)."})); sys.exit(0)
        c = db()
        f = {"status": status, "verlauf": verlauf, "ereignisse": ereignisse, "export": export, "pruefen": pruefen}.get(cmd)
        print(json.dumps(f(c, args) if f else {"status": "error", "error": "Unbekannter Befehl"}, ensure_ascii=False))
    except Exception as e:
        print(json.dumps({"status": "error", "error": str(e)[:300]}))
