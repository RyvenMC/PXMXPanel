# RyvenPanel API

Die API des RyvenPanel-Gateways – dieselbe, die das Web-Panel (`secure.ryvenmc.de`) und die Desktop-App benutzen.
Für **Folgeprojekte** (Discord-Bots, Tools, Statistik-Seiten …) gibt es **API-Schlüssel**.

- **Basis-URL:** `https://secure.ryvenmc.de`
- **Format:** JSON rein, JSON raus. Antworten sind immer `{"status": "ok", "data": …}` oder
  `{"status": "error", "error": "CODE", "error_data": "Klartext"}`.
- **Gateway-Version** abfragen: `GET /panel/config` (ohne Anmeldung).

---

## 1. Anmeldung

### a) API-Schlüssel (für Projekte) – empfohlen

Ein **Owner** legt den Schlüssel im Web-Panel an: *Team & Zugänge → API-Schlüssel → anlegen*.

| Eigenschaft | Bedeutung |
|---|---|
| Name | Projektname, `a–z 0–9 _ -` (taucht im Protokoll als `api:<name>` auf) |
| Rechte | nur die nötigen ankreuzen (siehe Abschnitt 3). `team.manage` ist per Schlüssel **nie** möglich |
| Erlaubte IPs | **Pflicht.** Nur von diesen IPs funktioniert der Schlüssel (bei IPv6 gilt das ganze /64-Netz) |
| Gültig für | Tage, 0 = unbegrenzt |

Der Schlüssel (`rpk_…`) wird **nur einmal** angezeigt. Er gehört in eine Umgebungsvariable des Projekts –
**nie** in Git, Discord oder Logs. Sperren: im selben Reiter („Sperren“) – wirkt sofort.

```http
GET /panel/me
Authorization: Bearer rpk_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

Mit Schlüssel **nicht** erlaubt: `/panel/team*`, `/panel/password`. Jeder schreibende Aufruf landet im Protokoll.

### b) Benutzer-Login (für Menschen / das Web-Panel)

```http
POST /panel/login
{"username": "name", "password": "…"}
→ {"status":"ok","data":{"token":"…", "must_change": false, …}}
```

Das Sitzungs-Token ist an die **freigegebenen IPs des Accounts** gebunden (neue IP → Owner muss freigeben) und läuft ab.
Für Projekte bitte **immer** API-Schlüssel statt Benutzer-Logins verwenden.

`POST /panel/logout` beendet eine Sitzung. `GET /panel/me` liefert Name, Rang und aktuelle Rechte.

---

## 2. Endpunkte

#### Shop-Auslieferung – `POST /panel/store`

Body: `{"order_id":"<32 Hex-Zeichen>","product_id":"<feste Produkt-ID>","player":"<Minecraft-Name>"}`

Recht: `store.fulfill`. Es werden ausschließlich die freigegebenen RyvenMC-Shop-Produkte verarbeitet; frei
wählbare Konsolenbefehle sind ausgeschlossen. Bestell-IDs sind idempotent. Bei unklarem Ergebnis wird nicht
automatisch wiederholt, sondern eine manuelle Prüfung verlangt.

### 2.1 Server (Crafty, durchgereicht unter `/api/v2/…`)

| Methode & Pfad | Recht |
|---|---|
| `GET /api/v2/servers` | `servers.view` |
| `GET /api/v2/servers/<id>` · `/stats` · `/history` | `servers.view` |
| `GET /api/v2/servers/<id>/logs?raw=true` | `console.view` |
| `POST /api/v2/servers/<id>/stdin` (Body = Befehl als Text) | `console.command` (Sperrliste) bzw. `console.command.unrestricted` |
| `POST /api/v2/servers/<id>/action/start_server` · `stop_server` · `restart_server` | `server.control` |
| `POST /api/v2/servers/<id>/action/kill_server` | `server.kill` |
| `POST /api/v2/servers/<id>/action/backup_server`, `GET …/backups` | `server.backup` |
| `POST /api/v2/servers/<id>/files` (lesen) | `files.view` |
| `PUT/PATCH/DELETE …/files…` | `files.edit` |

### 2.2 Moderation, Nutzer, Datenschutz – `POST /panel/mod`

Body: `{"cmd": "<befehl>", "args": {…}}`

| cmd | args | Recht |
|---|---|---|
| `overview` | – | `moderation.view` |
| `reports` | `status` (open/claimed/done/rejected/all), `q`, `limit` | `moderation.view` |
| `report` | `id` | `moderation.view` |
| `report_set` | `id`, `status` | `moderation.reports` |
| `punishments` | `type` (ban/mute/kick/warn/all), `active`, `q`, `limit` | `moderation.view` |
| `links` (Discord-Verknüpfungen) | `q`, `limit` | `moderation.view` |
| `player` | `name` | `moderation.view` |
| `templates` (Strafvorlagen) | – | `moderation.view` |
| `act` | `action` (warn/kick/mute/unmute/ban/ipban/unban), `player`, `duration` (z. B. `7d`, `perm`), `reason`, `silent` | warn/kick/mute/unmute: `moderation.punish` · ban/ipban/unban: `moderation.ban` |
| `nutzer` | `server` (optional) | `users.view` |
| `nutzer_detail` | `uuid`, `server` | `users.view` |
| `rang_detail` | `group`, `server` | `users.view` |
| `nutzer_log` | `server` | `users.view` |
| `nutzer_act` | `op` + Felder, siehe unten | `users.economy` (Money/Coins) bzw. `users.ranks` |
| `verbindungen` (Wächter live + Verlauf) | – | `datenschutz.view` |
| `waechter_bericht` | – | `datenschutz.view` |
| `plugins` (Inventar + Doku) | – | `datenschutz.view` |
| `datenbanken` | – | `datenschutz.view` |
| `ds_status` (Warnzähler) | – | `datenschutz.view` |
| `verbindung_ok` | `ip`, `dienst` | `datenschutz.edit` |
| `plugin_doku` | `id`, `kategorien[]`, `zweck`, `rechtsgrundlage`, `speicherdauer`, `extern`, `notiz`, `version` (oder `loeschen: true`) | `datenschutz.edit` |
| `ki_overview` · `ki_verlauf` (`filter` alle/eskaliert/offen/erledigt, `source`, `q`, `offset`, `limit`) · `ki_wissen` · `ki_wissen_lesen` (`name`) · `ki_config` | siehe links | `ki.view` |
| `ki_set_status` (`id`, `status` done/open) · `ki_wissen_speichern` (`name`, `text`; Sicherung, aktiv in <= 60 min) · `ki_config_set` (`cooldown_seconds`, `daily_limit`; gilt erst nach Bot-Neustart) | siehe links | `ki.edit` |

`nutzer_act` – `op`:
`user_set` / `user_add` / `user_addtemp` / `user_remove` (`uuid`, `group`, `dur`, `server_ctx`, `world_ctx`, `expiry`),
`user_perm_set` / `user_perm_settemp` / `user_perm_unset` (`uuid`, `perm`, `value`, `dur`, …),
`user_money` / `user_coins` (`name`, `art` = set/give/take, `amount`),
`grp_create` / `grp_delete` / `grp_weight` (`weight`) / `grp_prefix` (`prefix`, `prio`) / `grp_perm_set` / `grp_perm_unset` / `grp_parent_add` / `grp_parent_remove` (`parent`).
Änderungen laufen als Konsolenbefehl auf einem laufenden Server; die Antwort enthält `befehle`, `ausgabe`, `warnung`.

### 2.3 Wirtschaft – `POST /panel/eco`

| cmd | args | Recht |
|---|---|---|
| `summary` | `days` (1–30), `server` (optional) – Geldmenge, Geldfluss, Quellen, Spieler, Shop, Crates, Steuern je Server | `economy.view` |
| `items` | `server` – Shop-Items mit Preisen | `economy.view` |
| `setprice` | `path`, `buy`, `sell`, `server` | `economy.edit` |
| `additem` | `section`, `material`, `buy`, `sell`, `server` | `economy.edit` |
| `settax` | `server` (ohne = alle), `percent`, `enabled` | `economy.edit` |
| `reload` | `server` | `economy.edit` |

---

## 3. Rechte

`servers.view` · `console.view` · `console.command` · `console.command.unrestricted` · `server.control` · `server.kill` ·
`server.backup` · `files.view` · `files.edit` · `economy.view` · `economy.edit` · `moderation.view` · `moderation.reports` ·
`moderation.punish` · `moderation.ban` · `users.view` · `users.economy` · `users.ranks` · `datenschutz.view` · `datenschutz.edit` ·
`datenschutz.loeschen` · `datenschutz.auskunft` · `netzwerk.view` · `ki.view` · `ki.edit`
· `store.fulfill` (nur freigegebene RyvenMC-Shop-Produkte ausliefern; keine frei wählbaren Konsolenbefehle)

(`team.manage` gibt es nur für Owner-Logins, nie für API-Schlüssel.)

---

## 4. Fehlercodes

| HTTP | error | Bedeutung |
|---|---|---|
| 401 | `ACCESS_DENIED` | Kein/ungültiger Schlüssel, abgelaufen, gesperrt **oder Aufruf von einer nicht freigegebenen IP** |
| 403 | `NO_PERMISSION` | Recht fehlt (`error_data` nennt es) |
| 403 | `API_KEY_FORBIDDEN` | Endpunkt ist mit API-Schlüssel nicht erlaubt |
| 400 | `BAD_REQUEST` / `MOD_ERROR` / `ECO_ERROR` | Falsche Eingabe – `error_data` erklärt es |
| 404 | `NOT_FOUND` | Unbekannter Pfad |
| 429 | `RATE_LIMIT` | Zu viele Anmeldeversuche – 5 Minuten warten |

---

## 5. Regeln für Folgeprojekte

1. **Ein Schlüssel pro Projekt**, nur die nötigen Rechte, feste Server-IP.
2. Schlüssel aus einer Umgebungsvariablen lesen (`RYVENPANEL_KEY`), nie committen.
3. Abfragen sparsam: Live-Daten höchstens alle 5 s, Wirtschafts-Auswertungen höchstens jede Minute.
4. Schreibende Aktionen (Strafen, Geld, Ränge) nur mit klarem Zweck – alles steht mit `api:<name>` im Protokoll.
5. Personenbezogene Daten (Namen, UUIDs, IPs, Chatverläufe) nur verarbeiten, wenn das Projekt sie braucht, und
   das Projekt in *Datenschutz & Compliance* dokumentieren.

Für den Shop einen eigenen API-Schlüssel mit **nur** `store.fulfill` und der öffentlichen IP des Shop-Servers
anlegen. Dieses Recht gewährt keinen allgemeinen Konsolenzugriff.

Beispiele: [`beispiele/api_beispiel.py`](beispiele/api_beispiel.py) · [`beispiele/api-beispiel.mjs`](beispiele/api-beispiel.mjs)
