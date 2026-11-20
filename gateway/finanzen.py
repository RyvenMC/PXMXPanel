#!/usr/bin/env python3
"""
RyvenPanel – Finanzen (finanzen.py)

Einfache Buchhaltung (Einnahmen/Ausgaben, EÜR-artig aufgebaut) für Hobby-Kosten und später ein Gewerbe.
KEINE Steuerberatung, KEINE Steuerberechnung. Gebuchte Zeilen werden nie gelöscht oder geändert (GoBD-Stil):
Korrekturen laufen über Storno (Gegenbuchung mit negativem Betrag).

    python3 finanzen.py <befehl> <base64-json | ->

Befehle: uebersicht {jahr} / liste {jahr, art, kategorie, suche} / buchen {datum, art, kategorie, beschreibung, betrag_cent,
         zahlungsart, partner, intervall, beleg_name, beleg_b64} / stornieren {id, grund} / beleg {id} / kategorien {} /
         kategorie_neu {name, art} / wiederkehrend_ausfuehren {} / export {jahr, format: csv|zip}
"""
import base64, calendar, csv, datetime, io, json, os, re, sqlite3, sys, zipfile

VERSION = "1.0"
DB = os.environ.get("FINANZEN_DB", "/var/lib/pxmx/finanzen.db")
BELEGE = os.environ.get("FINANZEN_BELEGE", "/var/lib/pxmx/belege")
MAX_BELEG = 10 * 1024 * 1024
MAX_BETRAG = 10 ** 10
ARTEN = ("ausgabe", "einnahme")
INTERVALLE = ("monatlich", "jährlich")
SEED = [("Hosting/Server", "ausgabe"), ("Domains", "ausgabe"), ("Plugins & Lizenzen", "ausgabe"), ("Software/Abos", "ausgabe"),
        ("Werbung", "ausgabe"), ("Hardware", "ausgabe"), ("Gebühren", "ausgabe"), ("Sonstiges", "ausgabe"),
        ("Spenden/Einnahmen", "einnahme"), ("Sonstiges", "einnahme")]
BELEG_TYPEN = {"pdf": "application/pdf", "png": "image/png", "jpg": "image/jpeg", "webp": "image/webp"}
COLS = ("id", "belegnr", "datum", "art", "kategorie", "beschreibung", "betrag_cent", "zahlungsart", "partner", "intervall",
        "beleg_datei", "storniert_von", "storno_zu", "storno_grund", "vorlage_id", "erstellt_am", "erstellt_von")


class Fail(Exception):
    pass


def db():
    d = os.path.dirname(DB)
    os.makedirs(d, mode=0o700, exist_ok=True)
    new = not os.path.exists(DB)
    con = sqlite3.connect(DB, timeout=30, isolation_level=None)
    if new:
        os.chmod(DB, 0o600)
    con.row_factory = sqlite3.Row
    con.executescript("""
    create table if not exists buchungen (
      id integer primary key autoincrement,
      belegnr text not null unique,
      datum text not null,
      art text not null check (art in ('ausgabe','einnahme')),
      kategorie text not null,
      beschreibung text not null,
      betrag_cent integer not null,
      zahlungsart text not null default '',
      partner text not null default '',
      intervall text check (intervall is null or intervall in ('monatlich','jährlich')),
      beleg_datei text,
      storniert_von integer,
      storno_zu integer,
      storno_grund text,
      vorlage_id integer,
      erstellt_am text not null,
      erstellt_von text not null default '');
    create unique index if not exists ux_vorlage on buchungen(vorlage_id, datum) where vorlage_id is not null;
    create index if not exists ix_datum on buchungen(datum);
    create table if not exists kategorien (name text not null, art text not null check (art in ('ausgabe','einnahme')), primary key (name, art));
    create table if not exists meta (k text primary key, v text);
    create trigger if not exists trg_no_delete before delete on buchungen begin select raise(abort, 'Buchungen koennen nicht geloescht werden (Storno verwenden)'); end;
    create trigger if not exists trg_no_update before update on buchungen
      when OLD.storniert_von is not null or NEW.storniert_von is null or NEW.id != OLD.id or NEW.belegnr != OLD.belegnr or NEW.datum != OLD.datum
        or NEW.art != OLD.art or NEW.kategorie != OLD.kategorie or NEW.beschreibung != OLD.beschreibung or NEW.betrag_cent != OLD.betrag_cent
        or NEW.zahlungsart != OLD.zahlungsart or NEW.partner != OLD.partner or NEW.intervall is not OLD.intervall
        or NEW.beleg_datei is not OLD.beleg_datei or NEW.storno_zu is not OLD.storno_zu or NEW.vorlage_id is not OLD.vorlage_id
        or NEW.erstellt_am != OLD.erstellt_am or NEW.erstellt_von != OLD.erstellt_von
      begin select raise(abort, 'Gebuchte Zeilen sind unveraenderlich (Storno verwenden)'); end;
    """)
    if con.execute("select count(*) from kategorien").fetchone()[0] == 0:
        con.executemany("insert or ignore into kategorien (name, art) values (?,?)", SEED)
    return con


def today():
    return datetime.date.today()


def now():
    return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def need_year(a):
    try:
        j = int(a.get("jahr") or today().year)
    except (TypeError, ValueError):
        raise Fail("Ungültiges Jahr.")
    if not 2000 <= j <= 2100:
        raise Fail("Ungültiges Jahr.")
    return j


def parse_date(s):
    try:
        return datetime.date.fromisoformat(str(s))
    except ValueError:
        raise Fail("Ungültiges Datum (erwartet JJJJ-MM-TT).")


def clean(v, name, maxlen, required=False):
    v = re.sub(r"[\x00-\x1f\x7f]", " ", str(v or "")).strip()
    if required and not v:
        raise Fail(name + " fehlt.")
    if len(v) > maxlen:
        raise Fail(name + " ist zu lang (max. %d Zeichen)." % maxlen)
    return v


def next_belegnr(con, year):
    r = con.execute("select max(cast(substr(belegnr,6) as integer)) from buchungen where belegnr like ?", (str(year) + "-%",)).fetchone()[0]
    return "%d-%04d" % (year, (r or 0) + 1)


def detect(data):
    if data[:5] == b"%PDF-":
        return "pdf"
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "png"
    if data[:3] == b"\xff\xd8\xff":
        return "jpg"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "webp"
    return None


def write_beleg(year, belegnr, name, b64):
    try:
        data = base64.b64decode(b64, validate=False)
    except Exception:
        raise Fail("Beleg ist nicht lesbar (Base64).")
    if not data:
        raise Fail("Beleg ist leer.")
    if len(data) > MAX_BELEG:
        raise Fail("Beleg ist größer als 10 MB.")
    ext = detect(data)
    if not ext:
        raise Fail("Beleg: nur PDF, PNG, JPG und WEBP erlaubt.")
    base = os.path.splitext(os.path.basename(str(name or "beleg").replace("\\", "/")))[0]
    base = re.sub(r"[^A-Za-z0-9_-]+", "_", base).strip("_")[:60] or "beleg"
    rel = "%d/%s_%s.%s" % (year, belegnr, base, ext)
    d = os.path.join(BELEGE, str(year))
    os.makedirs(BELEGE, mode=0o700, exist_ok=True)
    os.makedirs(d, mode=0o700, exist_ok=True)
    fd = os.open(os.path.join(BELEGE, rel), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as f:
        f.write(data)
    return rel


def row_out(r, namen):
    o = {k: r[k] for k in COLS}
    o["hat_beleg"] = bool(r["beleg_datei"])
    o["storniert_von_belegnr"] = namen.get(r["storniert_von"])
    o["storno_zu_belegnr"] = namen.get(r["storno_zu"])
    return o


def belegnamen(con):
    return {r[0]: r[1] for r in con.execute("select id, belegnr from buchungen")}


def check_kat(con, kat, art):
    if not con.execute("select 1 from kategorien where name=? and art=?", (kat, art)).fetchone():
        raise Fail("Unbekannte Kategorie für %s: %s" % (art, kat))


# ---------------------------------------------------------------- commands
def c_buchen(a):
    d = parse_date(a.get("datum"))
    art = a.get("art")
    if art not in ARTEN:
        raise Fail("Art muss 'ausgabe' oder 'einnahme' sein.")
    kat = clean(a.get("kategorie"), "Kategorie", 60, True)
    besch = clean(a.get("beschreibung"), "Beschreibung", 200, True)
    b = a.get("betrag_cent")
    if isinstance(b, bool) or not isinstance(b, int) or not 0 < b <= MAX_BETRAG:
        raise Fail("Betrag muss eine positive ganze Zahl in Cent sein.")
    zahl = clean(a.get("zahlungsart"), "Zahlungsart", 40)
    partner = clean(a.get("partner"), "Partner", 100)
    iv = a.get("intervall") or None
    if iv is not None and iv not in INTERVALLE:
        raise Fail("Intervall muss 'monatlich' oder 'jährlich' sein.")
    actor = clean(a.get("actor") or "owner", "Benutzer", 60)
    con = db()
    check_kat(con, kat, art)
    con.execute("begin immediate")
    rel = None
    try:
        nr = next_belegnr(con, d.year)
        if a.get("beleg_b64"):
            rel = write_beleg(d.year, nr, a.get("beleg_name"), a["beleg_b64"])
        cur = con.execute("insert into buchungen (belegnr,datum,art,kategorie,beschreibung,betrag_cent,zahlungsart,partner,intervall,beleg_datei,erstellt_am,erstellt_von)"
                          " values (?,?,?,?,?,?,?,?,?,?,?,?)",
                          (nr, d.isoformat(), art, kat, besch, b, zahl, partner, iv, rel, now(), actor))
        con.execute("commit")
    except BaseException:
        con.execute("rollback")
        if rel and os.path.exists(os.path.join(BELEGE, rel)):
            os.remove(os.path.join(BELEGE, rel))
        raise
    return {"id": cur.lastrowid, "belegnr": nr}


def c_stornieren(a):
    grund = clean(a.get("grund"), "Grund", 200, True)
    actor = clean(a.get("actor") or "owner", "Benutzer", 60)
    con = db()
    con.execute("begin immediate")
    try:
        o = con.execute("select * from buchungen where id=?", (a.get("id"),)).fetchone()
        if not o:
            raise Fail("Buchung nicht gefunden.")
        if o["storniert_von"]:
            raise Fail("Buchung ist bereits storniert.")
        if o["storno_zu"]:
            raise Fail("Eine Storno-Buchung kann nicht storniert werden.")
        t = today()
        nr = next_belegnr(con, t.year)
        cur = con.execute("insert into buchungen (belegnr,datum,art,kategorie,beschreibung,betrag_cent,zahlungsart,partner,storno_zu,storno_grund,erstellt_am,erstellt_von)"
                          " values (?,?,?,?,?,?,?,?,?,?,?,?)",
                          (nr, t.isoformat(), o["art"], o["kategorie"], ("Storno " + o["belegnr"] + ": " + o["beschreibung"])[:200], -o["betrag_cent"],
                           o["zahlungsart"], o["partner"], o["id"], grund, now(), actor))
        con.execute("update buchungen set storniert_von=? where id=?", (cur.lastrowid, o["id"]))
        con.execute("commit")
    except BaseException:
        con.execute("rollback")
        raise
    return {"storno_id": cur.lastrowid, "belegnr": nr}


def c_liste(a):
    j = need_year(a)
    con = db()
    q, p = "select * from buchungen where datum >= ? and datum <= ?", ["%d-01-01" % j, "%d-12-31" % j]
    if a.get("art"):
        if a["art"] not in ARTEN:
            raise Fail("Ungültige Art.")
        q += " and art=?"
        p.append(a["art"])
    if a.get("kategorie"):
        q += " and kategorie=?"
        p.append(str(a["kategorie"]))
    if a.get("suche"):
        s = "%" + str(a["suche"]).replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
        q += " and (beschreibung like ? escape '\\' or partner like ? escape '\\' or belegnr like ? escape '\\' or kategorie like ? escape '\\')"
        p += [s] * 4
    q += " order by datum desc, id desc"
    namen = belegnamen(con)
    return {"buchungen": [row_out(r, namen) for r in con.execute(q, p)]}


def sums(con, j):
    r = {"ausgabe": 0, "einnahme": 0}
    for art, s in con.execute("select art, sum(betrag_cent) from buchungen where datum >= ? and datum <= ? group by art", ("%d-01-01" % j, "%d-12-31" % j)):
        r[art] = s or 0
    return r


def c_uebersicht(a):
    j = need_year(a)
    con = db()
    s = sums(con, j)
    monate = [{"monat": m, "ausgaben": 0, "einnahmen": 0} for m in range(1, 13)]
    for m, art, v in con.execute("select cast(substr(datum,6,2) as integer), art, sum(betrag_cent) from buchungen where datum >= ? and datum <= ? group by 1,2",
                                 ("%d-01-01" % j, "%d-12-31" % j)):
        monate[m - 1]["ausgaben" if art == "ausgabe" else "einnahmen"] = v or 0
    kats = []
    for kat, art, v in con.execute("select kategorie, art, sum(betrag_cent) from buchungen where datum >= ? and datum <= ? group by 1,2 order by 3 desc",
                                   ("%d-01-01" % j, "%d-12-31" % j)):
        tot = s[art]
        kats.append({"kategorie": kat, "art": art, "summe": v or 0, "anteil_prozent": round(100.0 * (v or 0) / tot, 1) if tot else 0})
    jahre = {int(r[0]) for r in con.execute("select distinct substr(datum,1,4) from buchungen")} | {today().year, j}
    vj = sums(con, j - 1)
    return {"jahr": j, "jahre": sorted(jahre, reverse=True), "summe_ausgaben": s["ausgabe"], "summe_einnahmen": s["einnahme"],
            "saldo": s["einnahme"] - s["ausgabe"], "monate": monate, "kategorien": kats,
            "vorjahr": {"summe_ausgaben": vj["ausgabe"], "summe_einnahmen": vj["einnahme"]}}


def beleg_pfad(rel):
    p = os.path.realpath(os.path.join(BELEGE, rel))
    if not p.startswith(os.path.realpath(BELEGE) + os.sep):
        raise Fail("Ungültiger Belegpfad.")
    return p


def c_beleg(a):
    r = db().execute("select beleg_datei from buchungen where id=?", (a.get("id"),)).fetchone()
    if not r:
        raise Fail("Buchung nicht gefunden.")
    if not r[0]:
        raise Fail("Zu dieser Buchung gibt es keinen Beleg.")
    p = beleg_pfad(r[0])
    if not os.path.isfile(p):
        raise Fail("Belegdatei fehlt auf dem Server.")
    with open(p, "rb") as f:
        data = f.read()
    return {"name": os.path.basename(p), "mime": BELEG_TYPEN.get(p.rsplit(".", 1)[-1], "application/octet-stream"), "b64": base64.b64encode(data).decode()}


def c_kategorien(a):
    rows = db().execute("select name, art from kategorien order by art, name").fetchall()
    return {"kategorien": [{"name": r[0], "art": r[1]} for r in rows],
            "ausgabe": [r[0] for r in rows if r[1] == "ausgabe"], "einnahme": [r[0] for r in rows if r[1] == "einnahme"]}


def c_kategorie_neu(a):
    name = clean(a.get("name"), "Name", 60, True)
    if a.get("art") not in ARTEN:
        raise Fail("Art muss 'ausgabe' oder 'einnahme' sein.")
    con = db()
    if con.execute("select 1 from kategorien where name=? and art=?", (name, a["art"])).fetchone():
        raise Fail("Kategorie existiert bereits.")
    con.execute("insert into kategorien (name, art) values (?,?)", (name, a["art"]))
    return c_kategorien({})


def occurrence(start, iv, n):
    if iv == "monatlich":
        m = start.month - 1 + n
        y, m = start.year + m // 12, m % 12 + 1
    else:
        y, m = start.year + n, start.month
    return datetime.date(y, m, min(start.day, calendar.monthrange(y, m)[1]))


def c_wiederkehrend(a):
    con = db()
    t = today()
    neu = []
    for v in con.execute("select * from buchungen where intervall is not null and storniert_von is null").fetchall():
        start = datetime.date.fromisoformat(v["datum"])
        for n in range(1, 1200):
            d = occurrence(start, v["intervall"], n)
            if d > t:
                break
            con.execute("begin immediate")
            try:
                if con.execute("select 1 from buchungen where vorlage_id=? and datum=?", (v["id"], d.isoformat())).fetchone():
                    con.execute("rollback")
                    continue
                nr = next_belegnr(con, d.year)
                con.execute("insert into buchungen (belegnr,datum,art,kategorie,beschreibung,betrag_cent,zahlungsart,partner,vorlage_id,erstellt_am,erstellt_von)"
                            " values (?,?,?,?,?,?,?,?,?,?,?)",
                            (nr, d.isoformat(), v["art"], v["kategorie"], v["beschreibung"], v["betrag_cent"], v["zahlungsart"], v["partner"], v["id"], now(), "wiederkehrend"))
                con.execute("commit")
                neu.append(nr)
            except BaseException:
                con.execute("rollback")
                raise
    con.execute("insert or replace into meta (k, v) values ('letzter_lauf', ?)", (now(),))
    return {"gebucht": len(neu), "belegnrs": neu, "letzter_lauf": now()}


def de(cent):
    s = "-" if cent < 0 else ""
    c = abs(cent)
    return "%s%d,%02d" % (s, c // 100, c % 100)


def safe(v):
    v = str(v or "")
    return "'" + v if v[:1] in ("=", "+", "-", "@", "\t", "\r") else v


def make_csv(con, j):
    namen = belegnamen(con)
    out = io.StringIO()
    w = csv.writer(out, delimiter=";", lineterminator="\r\n")
    w.writerow(["Belegnr", "Datum", "Art", "Kategorie", "Beschreibung", "Partner", "Betrag", "Storno"])
    rows = con.execute("select * from buchungen where datum >= ? and datum <= ? order by datum, id", ("%d-01-01" % j, "%d-12-31" % j)).fetchall()
    for r in rows:
        st = ("Storno zu " + namen[r["storno_zu"]]) if r["storno_zu"] else (("storniert durch " + namen[r["storniert_von"]]) if r["storniert_von"] else "")
        w.writerow([r["belegnr"], r["datum"], "Ausgabe" if r["art"] == "ausgabe" else "Einnahme", safe(r["kategorie"]), safe(r["beschreibung"]),
                    safe(r["partner"]), de(r["betrag_cent"]), st])
    return ("﻿" + out.getvalue()).encode("utf-8"), rows


def c_export(a):
    j = need_year(a)
    fmt = a.get("format", "csv")
    if fmt not in ("csv", "zip"):
        raise Fail("Format muss csv oder zip sein.")
    con = db()
    data, rows = make_csv(con, j)
    if fmt == "csv":
        return {"name": "Finanzen-%d.csv" % j, "b64": base64.b64encode(data).decode()}
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("Finanzen-%d.csv" % j, data)
        for r in rows:
            if r["beleg_datei"]:
                p = beleg_pfad(r["beleg_datei"])
                if os.path.isfile(p):
                    z.write(p, "belege/" + os.path.basename(p))
    return {"name": "Finanzen-%d.zip" % j, "b64": base64.b64encode(buf.getvalue()).decode()}


CMDS = {"uebersicht": c_uebersicht, "liste": c_liste, "buchen": c_buchen, "stornieren": c_stornieren, "beleg": c_beleg,
        "kategorien": c_kategorien, "kategorie_neu": c_kategorie_neu, "wiederkehrend_ausfuehren": c_wiederkehrend, "export": c_export}


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in CMDS:
        print(json.dumps({"status": "error", "error": "Befehle: " + ", ".join(CMDS)}))
        return
    try:
        if len(sys.argv) > 2 and sys.argv[2] == "-":
            args = json.loads(sys.stdin.buffer.read().decode() or "{}")
        else:
            args = json.loads(base64.b64decode(sys.argv[2]).decode()) if len(sys.argv) > 2 and sys.argv[2] else {}
        if not isinstance(args, dict):
            raise Fail("Argumente müssen ein Objekt sein.")
        print(json.dumps({"status": "ok", "data": CMDS[sys.argv[1]](args)}, ensure_ascii=False, default=str))
    except Fail as e:
        print(json.dumps({"status": "error", "error": str(e)}, ensure_ascii=False))
    except Exception as e:
        print(json.dumps({"status": "error", "error": "%s: %s" % (type(e).__name__, str(e)[:300])}, ensure_ascii=False))


if __name__ == "__main__":
    main()
