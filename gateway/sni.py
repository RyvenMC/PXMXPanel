#!/usr/bin/env python3
"""
RyvenPanel – Hostnamen-Mitschnitt für den Datenschutz-Wächter (sni.py)

Viele Dienste (bStats, CraftingStore, GeyserMC, Discord …) liegen hinter Cloudflare und wechseln ständig die IP-Adresse.
Der Wächter sah deshalb laufend „neue“ Ziele. Dieser Dienst liest auf den Docker-Netzen NUR den Hostnamen aus dem
Verbindungsaufbau der Container (TLS-ClientHello → SNI, HTTP → Host-Kopfzeile) – keine Inhalte, keine Spielerdaten –
und schreibt die Zuordnung IP → Hostname nach /var/lib/pxmx/sni.json. mod.py (aufzeichnen / verbindungen) nutzt sie,
um Ziele nach Hostnamen statt nach IP zu bewerten.

    python3 sni.py            – dauerhaft (systemd pxmx-sni)
    python3 sni.py 120        – 120 Sekunden mitschneiden und Ergebnis ausgeben (Diagnose)
"""
import ctypes, json, os, select, socket, struct, subprocess, sys, time

OUT = "/var/lib/pxmx/sni.json"
ETH_P_ALL = 0x0003
SO_ATTACH_FILTER = 26
# BPF: ip and tcp and (dst port 443 or dst port 80), keine Fragmente
BPF = [(0x28, 0, 0, 12), (0x15, 0, 9, 0x800), (0x30, 0, 0, 23), (0x15, 0, 7, 6), (0x28, 0, 0, 20), (0x45, 5, 0, 0x1fff),
       (0xb1, 0, 0, 14), (0x48, 0, 0, 16), (0x15, 1, 0, 443), (0x15, 0, 1, 80), (0x6, 0, 0, 0x40000), (0x6, 0, 0, 0)]


def bpf_attach(s):
    prog = b"".join(struct.pack("HBBI", *i) for i in BPF)
    buf = ctypes.create_string_buffer(prog)
    fprog = struct.pack("HL", len(BPF), ctypes.addressof(buf))
    s.setsockopt(socket.SOL_SOCKET, SO_ATTACH_FILTER, fprog)
    return buf   # Puffer am Leben halten


def bruecken():
    return sorted(n for n in os.listdir("/sys/class/net") if n.startswith("br-") or n == "docker0")


def container_ips():
    """Container-IP → Container-Name."""
    out = {}
    try:
        names = subprocess.run(["docker", "ps", "--format", "{{.Names}}"], capture_output=True, text=True, timeout=15).stdout.split()
        if names:
            r = subprocess.run(["docker", "inspect", "-f", "{{.Name}}{{range .NetworkSettings.Networks}} {{.IPAddress}}{{end}}"] + names,
                               capture_output=True, text=True, timeout=15)
            for ln in r.stdout.splitlines():
                t = ln.split()
                for ip in t[1:]:
                    out[ip] = t[0].lstrip("/")
    except Exception:
        pass
    return out


def sni_aus(p):
    """Hostname aus einem TLS-ClientHello (Payload beginnt mit dem Record-Header)."""
    try:
        if len(p) < 43 or p[0] != 0x16 or p[5] != 0x01:
            return None
        i = 43
        i += 1 + p[i]                                   # Session-ID
        i += 2 + struct.unpack(">H", p[i:i + 2])[0]     # Cipher-Suites
        i += 1 + p[i]                                   # Kompression
        ende = i + 2 + struct.unpack(">H", p[i:i + 2])[0]
        i += 2
        while i + 4 <= ende and i + 4 <= len(p):
            typ, ln = struct.unpack(">HH", p[i:i + 4])
            if typ == 0:
                n = struct.unpack(">H", p[i + 7:i + 9])[0]
                return p[i + 9:i + 9 + n].decode("ascii", "replace").lower()
            i += 4 + ln
    except (IndexError, struct.error):
        pass
    return None


def host_aus(p):
    if not p[:8].split(b" ")[0] in (b"GET", b"POST", b"HEAD", b"PUT", b"OPTIONS", b"PATCH", b"DELETE"):
        return None
    for ln in p.split(b"\r\n")[1:20]:
        if ln.lower().startswith(b"host:"):
            return ln[5:].strip().decode("ascii", "replace").split(":")[0].lower()
    return None


def main():
    dauer = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    socks, puffer = [], []
    for br in bruecken():
        s = socket.socket(socket.AF_PACKET, socket.SOCK_RAW, socket.htons(ETH_P_ALL))
        puffer.append(bpf_attach(s))
        s.bind((br, 0))
        socks.append(s)
    daten = {}
    try:
        daten = json.load(open(OUT, encoding="utf-8"))
    except (OSError, ValueError):
        pass
    cips, cips_zeit = container_ips(), time.time()
    start, gespeichert, geaendert = time.time(), time.time(), False
    while True:
        r, _, _ = select.select(socks, [], [], 5)
        for s in r:
            pkt = s.recv(65535)
            if len(pkt) < 54:
                continue
            ihl = (pkt[14] & 0x0f) * 4
            src, dst = socket.inet_ntoa(pkt[26:30]), socket.inet_ntoa(pkt[30:34])
            t = 14 + ihl
            dport = struct.unpack(">H", pkt[t + 2:t + 4])[0]
            payload = pkt[t + ((pkt[t + 12] >> 4) * 4):]
            if not payload:
                continue
            host = sni_aus(payload) if dport == 443 else host_aus(payload)
            if not host or dst.startswith(("10.", "172.", "192.168.", "127.")) and not dst.startswith(("172.64.", "172.65.", "172.66.", "172.67.", "172.68.", "172.69.", "172.70.", "172.71.")):
                continue
            if time.time() - cips_zeit > 300:
                cips, cips_zeit = container_ips(), time.time()
            e = daten.setdefault(dst, {})
            h = e.setdefault(host, {"zuerst": int(time.time()), "anzahl": 0, "von": []})
            h["zuletzt"] = int(time.time())
            h["anzahl"] += 1
            wer = cips.get(src, src)
            if wer not in h["von"]:
                h["von"] = (h["von"] + [wer])[-5:]
            geaendert = True
        jetzt = time.time()
        if geaendert and (jetzt - gespeichert > 20 or dauer):
            grenze = jetzt - 30 * 86400        # Einträge älter als 30 Tage verwerfen
            daten = {ip: {h: v for h, v in hs.items() if v.get("zuletzt", 0) > grenze} for ip, hs in daten.items()}
            daten = {ip: hs for ip, hs in daten.items() if hs}
            tmp = OUT + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(daten, f)
            os.replace(tmp, OUT)
            gespeichert, geaendert = jetzt, False
        if dauer and jetzt - start > dauer:
            for ip, hs in sorted(daten.items()):
                for h, v in hs.items():
                    if v.get("zuletzt", 0) >= start:
                        print(ip.ljust(16), h.ljust(40), str(v["anzahl"]).rjust(5), ",".join(v["von"]))
            return


if __name__ == "__main__":
    main()
