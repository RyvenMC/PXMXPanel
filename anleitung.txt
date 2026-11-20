# PXMX Server Suite

**Eigenständige Windows-App zur Verwaltung eines Minecraft-Servers über SSH und [Crafty Controller](https://craftycontrol.com/).**
Dieses Projekt ist vom RyvenPanel-Quellstand abgeleitet, wird aber separat gebaut und speichert App-Daten, SSH-Schlüssel,
Server-Agenten und Windows-Installation unter eigenen PXMX-Namen.

> [!WARNING]
> **Vorschau:** Die Server-Onboarding-Funktion ist auf Debian 13 ausgelegt. Nicht alle übernommenen Verwaltungsbereiche
> sind bereits von RyvenMC-spezifischen Plugins und Datenbanken entkoppelt.
>
> - Der Erststart verbindet per SSH, prüft den Server und kann fehlendes Docker, Compose und Crafty ergänzen.
> - Die automatische Installation läuft nur auf Debian 13 und benötigt root oder passwortloses sudo.
> - Bestehende Docker-Container und erkannte Crafty-Daten werden nicht überschrieben; Firewall-Regeln werden nicht geändert.
> - Craftys Weboberfläche wird bei einer Neuinstallation nur an `127.0.0.1:8443` gebunden. Die App verbindet sich per SSH-Portweiterleitung.
> - Eine fehlende MariaDB wird intern im Docker-Netz ergänzt; vorhandene MySQL-/MariaDB-Container bleiben unverändert. Dafür kann `crafty_container` ohne Neustart an ein PXMX-Docker-Netz angeschlossen werden.
> - Minecraft-Server werden nicht automatisch angelegt. Plugins können im Datei-Bereich über die Spigot-Suche ausgewählt und als JAR hochgeladen werden; der Server wird nicht automatisch neu gestartet.
> - Der eigene Discord-Bot wird im Tab „KI-Support → Einstellungen“ mit einem vom Betreiber erstellten Discord-Bot-Token als Docker-Compose-Dienst installiert. Für KI-Antworten ist zusätzlich ein selbst erstellter Anthropic-Key erforderlich.
> - E-Mail wird über einen externen SMTP-Relay versendet; PXMX hostet keinen Mailserver. Zugangsdaten lassen sich unter „Datenschutz → Auskunft“ konfigurieren und werden serverseitig mit restriktiven Dateirechten gespeichert.
>
> Nutzung liegt unter unserer Verantwortung, mit Anspruch auf Support.

Das Projekt ist unabhängig vom ursprünglichen Windows-Installationspfad und Update-Kanal. Automatische App-Updates sind derzeit deaktiviert.

## Download

Nach dem lokalen Build liegt der Installer unter `dist\PXMX-ServerSuite-Setup-x.y.z.exe`.

- Installation nur für den eigenen Windows-Benutzer, **keine Adminrechte** nötig
- Startmenü- und Desktop-Verknüpfung, Deinstallation über *Einstellungen → Apps*
- Beim ersten Start SSH-Verbindung und Servereinrichtung abschließen; anschließend mit Crafty anmelden

## Funktionen

| Bereich | Was es kann |
|---|---|
| **Server-Steuerung** | Alle Crafty-Server mit Live-Status, Start/Stopp/Neustart, Backups, Konsole, Dateien, Metriken |
| **Spigot-Plugins** | Kostenlose Spigot-Ressourcen suchen, Version und getestete Minecraft-Versionen prüfen und JARs in den ausgewählten Server hochladen |
| **Spieler & VPN** | Wer ist online, Login-Verlauf, VPN/Proxy-Erkennung |
| **Nutzer & Ränge** | Money, Coins, LuckPerms-Ränge und Permissions, Bans |
| **Wirtschaft** | Geldmenge (Inflation), woher das Geld kommt, Top-Verdiener, Shop-Statistik, Preise ändern (EconomyShopGUI), Crate-Auswertung, Verkaufssteuer |
| **Moderation** | Reports mit Chatverlauf bearbeiten, alle Strafen (Bann, Mute, Kick, Verwarnung) mit Filter und Aufheben, Discord-Verknüpfungen, Spieler-Akte – Strafen netzwerkweit mit Vorlagen und Eskalation (RyvenBans) |
| **Discord-Bot & KI-Support** | Eigenen Discord-Bot installieren, `/support`, `/privacy` und `/ping`; Anthropic-Key und Fragenlimits im Panel verwalten |
| **Datenschutz & E-Mail** | Datenauskunft nach Identitätsprüfung; externen SMTP-Relay konfigurieren und Verbindung testen |
| **Terminal** | SSH-Konsole mit mehreren Tabs |
| **Dateien** | Datei-Manager für den ganzen Server (Upload/Download, Editor) |
| **Docker** | Container, Stacks, Images, Netzwerke |
| **Firewall** | Ports öffnen/schließen, IPs sperren (iptables) |
| **Team & Zugänge** | Team-Accounts mit Passwort, Rechte pro LuckPerms-Rang, IP-Freigaben, Protokoll |

### Team-Zugang
Teammitglieder bekommen **nie** deinen SSH-Schlüssel oder Crafty-Zugang. Ein kleiner Dienst auf dem Server
(`gateway/gateway.py`, wird aus dem Panel heraus installiert) prüft jede Aktion gegen die Rechte des Rangs,
bindet Accounts an IP-Adressen und protokolliert alles.

## API des Team-Dienstes (`gateway/gateway.py`)

HTTPS-Dienst auf dem Server (Standard-Port **8444**, eigenes Zertifikat, das das Panel fest kennt). Er ist der einzige Weg,
über den Team-Accounts etwas am Server tun – jede Anfrage wird gegen die Rechte des LuckPerms-Rangs geprüft und protokolliert.
Antworten sind JSON im Crafty-Stil: `{"status":"ok","data":…}` bzw. `{"status":"error","error":"CODE","error_data":"Text"}`.

**Anmeldung:** `POST /panel/login` mit `{"username","password"}` → Sitzungs-Token. Danach bei jeder Anfrage
`Authorization: Bearer <token>`. Login ist pro IP begrenzt (Sperre nach zu vielen Versuchen), Accounts können an IPs gebunden sein.

| Endpunkt | Recht | Zweck |
|---|---|---|
| `GET /panel/config` | – | Anmeldeart und Version des Dienstes |
| `GET /panel/update` · `GET /panel/update/PXMX.exe` | – | Release-Endpunkt (Updates derzeit deaktiviert) |
| `POST /panel/login` · `POST /panel/logout` | – / angemeldet | Sitzung starten / beenden |
| `GET /panel/me` | angemeldet | Eigener Account, Rang und **aktuelle** Rechte |
| `POST /panel/password` | angemeldet | Eigenes Passwort ändern (beim ersten Login Pflicht) |
| `POST /panel/eco` `{"cmd","args"}` | siehe unten | Wirtschaft (ruft `eco.py` auf dem Server auf) |
| `POST /panel/mod` `{"cmd","args"}` | siehe unten | Moderation (ruft `mod.py` auf, Daten aus RyvenLink/RyvenBans) |
| `/api/v2/…` | je nach Pfad | Durchreiche an die **Crafty-API** mit Rechteprüfung, siehe unten |

**Wirtschaft (`/panel/eco`):** `summary`, `items` → `economy.view` · `setprice`, `additem`, `reload`, `settax` → `economy.edit`.
Datenquellen: Tabellen `eco_tx` / `eco_supply` von **RyvenEco**, Kontostände, Crate-Öffnungen, Shop-Dateien.

**Moderation (`/panel/mod`):** `overview`, `reports`, `report`, `punishments`, `links`, `player`, `templates` → `moderation.view` ·
`report_set` → `moderation.reports` · `act` mit `action` = `warn|kick|mute|unmute` → `moderation.punish`, `ban|ipban|unban` → `moderation.ban`
(führt `ryvenbans exec …` auf dem Proxy aus).

**Crafty-Durchreiche (`/api/v2/servers/<id>/…`):**

| Pfad | Recht |
|---|---|
| `GET /api/v2/servers`, `GET …/<id>`, `…/stats`, `…/history` | `servers.view` |
| `GET …/logs` | `console.view` |
| `POST …/stdin` | `console.command` (Befehle auf der Sperrliste nur mit `console.command.unrestricted`) |
| `POST …/action/start_server\|stop_server\|restart_server` | `server.control` |
| `POST …/action/kill_server` | `server.kill` |
| `POST …/action/backup_server`, `GET …/backups` | `server.backup` |
| `POST …/files` (lesen) | `files.view` |
| `PUT/PATCH/DELETE …/files`, `…/files/…` | `files.edit` |

Alle anderen Pfade werden abgelehnt. **Owner-Verwaltung** (Accounts, Rang-Rechte, IP-Freigaben, Einstellungen) läuft nicht über HTTP,
sondern lokal per SSH: `python3 gateway.py admin <roles|user|ip|config> [base64-json]`.

## Voraussetzungen

**PC:** Windows 10/11 (.NET Framework 4.8 ist bereits enthalten). Für Terminal, Dateien, Docker, Firewall und
Wirtschaft den Windows-Client „OpenSSH“ (*Einstellungen → Optionale Features*).

**Server (für automatische Einrichtung):**
- Debian 13 auf einem eigenen Linux-Server mit SSH-Schlüsselzugriff
- root oder ein SSH-Benutzer mit passwortlosem sudo
- Internetzugriff zum Installieren von Debian-Paketen und Abruf des offiziellen Crafty-Images

## Erste Schritte

1. App starten, Serveradresse, SSH-Benutzer und Port eintragen.
2. Einen lokalen Ed25519-Schlüssel erstellen und den öffentlichen Schlüssel auf dem Server autorisieren.
3. Server prüfen. Die App zeigt erkannte Docker-, Compose-, Crafty- und Datenbank-Komponenten an.
4. Nach ausdrücklicher Bestätigung werden fehlende Docker-/Compose-Pakete, Crafty und MariaDB ergänzt; Zugangsdaten einer neuen MariaDB speichert die App DPAPI-verschlüsselt und zeigt sie auf Wunsch an.
5. „Weiter zur Anmeldung“ öffnet den Crafty-Login über die SSH-Portweiterleitung.
6. Im Datei-Bereich eines Servers „Plugins aus Spigot …“ öffnen, Plugin suchen und vor dem Upload Quelle und Kompatibilität prüfen. Ein Neustart erfolgt manuell.
7. Im [Discord Developer Portal](https://discord.com/developers/applications) eine Bot-Anwendung erstellen, mit den Scopes `bot` und `applications.commands` zum Discord-Server hinzufügen und deren Token unter „KI-Support → Einstellungen“ eintragen. Das Panel baut den PXMX-Bot-Container; dafür lädt Docker das Python-Image und `discord.py` herunter. `/support`, `/privacy` und `/ping` werden als Slash-Befehle registriert. `/privacy` gibt Hinweise; Datenauskünfte erstellt ein berechtigter Betreiber im Panel nach Identitätsprüfung.
8. Für KI-Antworten einen API-Key in der [Anthropic Console](https://console.anthropic.com/settings/keys) erstellen und im selben Tab hinterlegen. Der Key wird nicht zurückgelesen; ohne Key bleibt der Bot online, antwortet aber nicht mit KI.
9. Für Datenauskünfte per E-Mail unter „Datenschutz → Auskunft → Mail-Relay konfigurieren“ einen externen SMTP-Anbieter hinterlegen. Ein Verbindungstest prüft TLS und optional die Anmeldung; für den Versand muss der Anbieter die Absenderadresse zulassen.

## Nach der Einrichtung: das normale Panelmenü

Die Servereinrichtung ist ein einmaliger Onboarding-Schritt. Nach der Crafty-Anmeldung öffnet sich das normale PXMX-Panel mit seinem Menü für Server, Dateien, Spieler, Moderation und die weiteren verfügbaren Bereiche. Discord-Bot und Mail-Relay sind optionale Dienste innerhalb dieses Panels – sie starten kein separates Verwaltungsprogramm und ersetzen das Menü nicht. Während ihrer Einrichtung bleibt PXMX geöffnet; danach kannst du wie gewohnt über das Panelmenü weiterarbeiten.

## Discord-Bot einrichten

1. Erstelle im [Discord Developer Portal](https://discord.com/developers/applications) eine Bot-Anwendung und füge sie deinem Discord-Server mit den OAuth2-Scopes `bot` und `applications.commands` hinzu.
2. Öffne in PXMX **KI-Support → Einstellungen**, füge den Bot-Token ein und wähle **Bot installieren**. PXMX erstellt den Compose-Dienst `pxmx-discord-bot`; Docker lädt dazu das Python-Image und die Bot-Abhängigkeit herunter. Der Dienst benötigt keine geöffneten eingehenden Ports.
3. Für KI-Antworten musst du zusätzlich einen Anthropic-API-Key erstellen und im selben Bereich speichern. Der Bot kann ohne diesen Key online sein, beantwortet `/support` dann aber nicht mit KI.

Der Bot stellt die Slash-Befehle `/ping`, `/support` und `/privacy` bereit. Discord kann nach dem ersten Start etwas Zeit benötigen, bis die Befehle überall angezeigt werden. `/privacy` gibt Hinweise zum Datenschutz; es führt selbst keine Auskunft oder Löschung aus. Ein berechtigter Betreiber erstellt Datenauskünfte über PXMX und prüft vor der Herausgabe die Identität des Spielers. Bot-Token und KI-Key werden nicht im Panel angezeigt oder in den Docker-Build-Kontext übernommen.

## E-Mail für Datenauskünfte einrichten

PXMX betreibt keinen eigenen Mailserver. Für den Versand wird ein externer SMTP-Relay benötigt, dessen Zugang und zulässige Absenderadresse du beim E-Mail-Anbieter erhältst.

1. Öffne **Datenschutz → Auskunft → Mail-Relay konfigurieren**.
2. Trage SMTP-Host, Port, TLS-Modus, Absenderadresse und – falls erforderlich – Zugangsdaten ein.
3. Wähle **Speichern und Verbindung testen**. Der Test prüft die TLS-Verbindung und die Anmeldung, wenn ein Benutzername gesetzt ist; er versendet keine Testmail.

Das SMTP-Passwort bleibt serverseitig gespeichert und wird nicht an die Oberfläche zurückgesendet. Ein leeres Passwortfeld behält ein bereits gespeichertes Passwort bei. Vor dem Versand einer Datenauskunft muss der Betreiber sicherstellen, dass die Empfängeradresse tatsächlich dem betreffenden Spieler gehört.

## Selbst bauen

Keine Installation nötig – gebaut wird mit dem C#-Compiler, der in Windows steckt:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Ergebnis: `PXMX.exe` und `dist\PXMX-ServerSuite-Setup-<version>.exe`.

## Lizenz

[MIT](LICENSE) – kostenlos nutzbar, auch für eigene Server. Keine Garantie.
