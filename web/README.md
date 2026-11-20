# RyvenPanel Web – secure.ryvenmc.de

Das Team-Panel von RyvenMC als Website, im Look der Desktop-App. Läuft im eigenen Docker-Container `ryvenpanel-web`
und spricht mit dem RyvenPanel-Gateway auf dem Host.

```
Browser ──Cloudflare──▶ ryvenmc-web (Caddy, nur Cloudflare-IPs, setzt X-Panel-Client-IP)
                          └──▶ ryvenpanel-web (dieses Repo, Caddy, Netz ryvenpanel-net 172.30.42.0/24)
                                 ├── /            → site/ (statische Web-App)
                                 └── /panel/*, /api/v2/* → Gateway https://host.docker.internal:8444
```

| Datei | Inhalt |
|---|---|
| `site/index.html`, `site/app.js`, `site/style.css` | Die Web-App (Vanilla-JS, keine Build-Schritte) |
| `Caddyfile` | Auslieferung, Sicherheits-Header (CSP), Proxy zum Gateway |
| `docker-compose.yml` | Container + Netz `ryvenpanel-net` |
| `API.md` | **API-Dokumentation für Folgeprojekte** (API-Schlüssel, Endpunkte, Rechte) |
| `beispiele/` | API-Clients in Python und Node.js |

## Bereiche

Server-Steuerung · Moderation (Reports, Strafen, Verify, Spielerakte) · Wirtschaft (Geldfluss, Shop, Crates, Preise,
Steuer je Server) · Nutzer & Ränge (LuckPerms, Money, Coins) · Datenschutz & Compliance (Verbindungs-Wächter,
Bericht, Plugins, Datenbanken, Verarbeitungsverzeichnis) · Team & Zugänge (Accounts, Rechte, API-Schlüssel, Protokoll).

## Entwickeln & ausrollen

- Die App ist reines HTML/CSS/JS – Änderungen in `site/` und im Browser testen.
- Ausrollen: Dateien nach `/opt/ryvenpanel-web/site/` auf dem Server kopieren (Caddy liefert sofort aus, kein Neustart).
  Vorher `node --check site/app.js`.
- CSP erlaubt keine fremden Skripte/Stile – alles muss im Repo liegen.
- Die Serverseite (Gateway, `mod.py`, `eco.py`, `sni.py`) liegt im Repo **RyvenMC/RyvenPanel** unter `gateway/`.

## Sicherheit

- Zugriff nur über Cloudflare; Logins sind an freigegebene IPs gebunden (Owner gibt frei).
- Keine Zugangsdaten, Tokens oder API-Schlüssel ins Repo.
- Jede Aktion steht mit Name und IP im Protokoll (Team & Zugänge → Protokoll).
