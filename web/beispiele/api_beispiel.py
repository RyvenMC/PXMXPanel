#!/usr/bin/env python3
"""
Beispiel: RyvenPanel-API mit einem API-Schlüssel (nur Python-Standardbibliothek).

    export RYVENPANEL_KEY=rpk_...        # Schlüssel aus dem Web-Panel (Team & Zugänge → API-Schlüssel)
    python3 api_beispiel.py

Der Schlüssel funktioniert nur von den IPs, die beim Anlegen eingetragen wurden.
"""
import json
import os
import urllib.error
import urllib.request

BASIS = os.environ.get("RYVENPANEL_URL", "https://secure.ryvenmc.de")
SCHLUESSEL = os.environ["RYVENPANEL_KEY"]


def aufruf(methode, pfad, daten=None):
    body = json.dumps(daten).encode() if daten is not None else None
    req = urllib.request.Request(BASIS + pfad, data=body, method=methode, headers={
        "Authorization": "Bearer " + SCHLUESSEL, "Content-Type": "application/json", "User-Agent": "RyvenPanel-API-Beispiel"})
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            antwort = json.load(r)
    except urllib.error.HTTPError as e:
        antwort = json.load(e)
    if antwort.get("status") != "ok":
        raise RuntimeError("%s: %s" % (antwort.get("error"), antwort.get("error_data")))
    return antwort.get("data")


def mod(cmd, **args):
    return aufruf("POST", "/panel/mod", {"cmd": cmd, "args": args})


def eco(cmd, **args):
    return aufruf("POST", "/panel/eco", {"cmd": cmd, "args": args})


if __name__ == "__main__":
    ich = aufruf("GET", "/panel/me")
    print("Angemeldet als", ich["username"], "– Rechte:", ", ".join(ich["perms"]))

    if "servers.view" in ich["perms"]:
        for s in aufruf("GET", "/api/v2/servers"):
            print("Server:", s.get("server_name"), s.get("server_id"))

    if "economy.view" in ich["perms"]:
        w = eco("summary", days=7)
        print("Geldmenge: %.0f $ · erzeugt %.0f $ · vernichtet %.0f $" % (w["supply_now"], w["total_in"], w["total_out"]))

    if "moderation.view" in ich["perms"]:
        o = mod("overview")
        print("Moderation:", o)
