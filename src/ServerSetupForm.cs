using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    class ServerSetupForm : Form
    {
        static readonly Regex SafeHost = new Regex(@"^[A-Za-z0-9._:%-]{1,253}$");
        static readonly Regex SafeUser = new Regex(@"^[A-Za-z0-9._-]{1,64}$");

        readonly Settings settings;
        readonly TextBox host = new TextBox();
        readonly TextBox user = new TextBox();
        readonly TextBox port = new TextBox();
        readonly TextBox output = new TextBox();
        readonly Button createKey = new Button();
        readonly Button installKey = new Button();
        readonly Button inspect = new Button();
        readonly Button install = new Button();
        readonly Button proceed = new Button();
        readonly Button databaseAccess = new Button();
        readonly Label status = new Label();
        Ssh ssh;
        Inventory inventory;

        sealed class Inventory
        {
            public string Os;
            public string Version;
            public bool Root;
            public bool Sudo;
            public bool Docker;
            public bool Compose;
            public bool Crafty;
            public bool Database;
            public bool CraftyData;

            public bool Debian13 { get { return Os == "debian" && Version == "13"; } }
            public bool CanInstall { get { return Root || Sudo; } }
        }

        public ServerSetupForm(Settings settings)
        {
            this.settings = settings;
            Text = "PXMX Server Suite – Server einrichten";
            ClientSize = new Size(700, 590);
            MinimumSize = new Size(700, 590);
            MaximumSize = new Size(700, 590);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 22, 28);
            ForeColor = Color.FromArgb(232, 236, 242);
            Font = new Font("Segoe UI", 9f);

            var title = new Label
            {
                Text = "PXMX Server Suite",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(28, 22),
                AutoSize = true
            };
            var description = new Label
            {
                Text = "SSH verbinden, Server erkennen und nur fehlende Basisdienste ergänzen. Der erste SSH-Hostschlüssel wird gespeichert und bei Änderungen abgelehnt.",
                ForeColor = Color.FromArgb(172, 181, 194),
                Location = new Point(30, 62),
                AutoSize = true
            };
            Controls.Add(title);
            Controls.Add(description);

            AddField("SERVER / IP", host, 30, 105, settings.SshHost);
            AddField("SSH-BENUTZER", user, 265, 105, settings.SshUser);
            AddField("SSH-PORT", port, 500, 105, settings.SshPort.ToString());

            createKey.Text = "SSH-Schlüssel erstellen";
            installKey.Text = "Öffentlichen Schlüssel installieren";
            inspect.Text = "Server prüfen";
            install.Text = "Fehlende Komponenten installieren";
            proceed.Text = "Weiter zur Anmeldung";
            databaseAccess.Text = "DB-Zugang anzeigen";
            var close = new Button { Text = "Schließen" };
            StyleButton(createKey, 30, 177, 205, 38, false);
            StyleButton(installKey, 245, 177, 235, 38, false);
            StyleButton(inspect, 490, 177, 180, 38, true);
            StyleButton(install, 30, 225, 640, 42, true);
            StyleButton(databaseAccess, 30, 535, 185, 38, false);
            StyleButton(proceed, 225, 535, 285, 38, true);
            StyleButton(close, 520, 535, 150, 38, false);
            Controls.AddRange(new Control[] { createKey, installKey, inspect, install, databaseAccess, proceed, close });

            status.Text = "Der SSH-Schlüssel wird lokal auf diesem Windows-Benutzerkonto gespeichert.";
            status.ForeColor = Color.FromArgb(172, 181, 194);
            status.Location = new Point(30, 278);
            status.Size = new Size(640, 28);
            Controls.Add(status);

            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.BackColor = Color.FromArgb(11, 14, 19);
            output.ForeColor = Color.FromArgb(206, 214, 224);
            output.BorderStyle = BorderStyle.FixedSingle;
            output.Font = new Font("Consolas", 9f);
            output.Location = new Point(30, 310);
            output.Size = new Size(640, 205);
            Controls.Add(output);

            createKey.Click += async (s, e) => await CreateKey();
            installKey.Click += (s, e) => InstallKey();
            inspect.Click += async (s, e) => await InspectServer();
            install.Click += async (s, e) => await InstallMissing();
            databaseAccess.Click += (s, e) => ShowDatabaseAccess();
            proceed.Click += (s, e) => Proceed();
            close.Click += (s, e) => Close();

            install.Enabled = false;
            proceed.Enabled = false;
            installKey.Enabled = false;
            databaseAccess.Enabled = !string.IsNullOrEmpty(settings.DatabasePassword);

            Shown += async (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(settings.SshHost) && ReadConnection() && ssh.KeyExists)
                    await InspectServer();
            };
        }

        void AddField(string label, TextBox box, int x, int y, string value)
        {
            var caption = new Label
            {
                Text = label,
                ForeColor = Color.FromArgb(172, 181, 194),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Location = new Point(x, y),
                AutoSize = true
            };
            box.Text = value ?? "";
            box.Location = new Point(x, y + 23);
            box.Size = new Size(x == 500 ? 170 : 215, 30);
            box.BackColor = Color.FromArgb(35, 42, 52);
            box.ForeColor = Color.White;
            box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(caption);
            Controls.Add(box);
        }

        void StyleButton(Button button, int x, int y, int width, int height, bool accent)
        {
            button.Location = new Point(x, y);
            button.Size = new Size(width, height);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(66, 78, 93);
            button.BackColor = accent ? Color.FromArgb(49, 122, 85) : Color.FromArgb(35, 42, 52);
            button.ForeColor = Color.White;
            button.Cursor = Cursors.Hand;
        }

        bool ReadConnection()
        {
            int sshPort;
            var h = host.Text.Trim();
            var u = user.Text.Trim();
            if (!SafeHost.IsMatch(h) || !SafeUser.IsMatch(u) ||
                !int.TryParse(port.Text.Trim(), out sshPort) || sshPort < 1 || sshPort > 65535)
            {
                SetStatus("Server, Benutzer oder SSH-Port ist ungültig.", true);
                return false;
            }

            ssh = new Ssh
            {
                Host = h,
                User = u,
                Port = sshPort,
                KeyPath = string.IsNullOrEmpty(settings.SshKey) ? Ssh.DefaultKey : settings.SshKey
            };
            settings.SshHost = h;
            settings.SshUser = u;
            settings.SshPort = sshPort;
            settings.SshKey = ssh.KeyPath;
            settings.Save();
            return true;
        }

        async Task CreateKey()
        {
            if (!ReadConnection()) return;
            if (ssh.KeyExists)
            {
                SetStatus("Es existiert bereits ein SSH-Schlüssel. Der vorhandene Schlüssel bleibt unverändert.", false);
                installKey.Enabled = true;
                return;
            }
            if (!ssh.ClientAvailable)
            {
                SetStatus("Der Windows-OpenSSH-Client ist nicht installiert.", true);
                return;
            }

            createKey.Enabled = false;
            var result = await ssh.CreateKey();
            createKey.Enabled = true;
            if (!result.Ok)
            {
                SetStatus("Schlüsselerstellung fehlgeschlagen: " + result.Message, true);
                return;
            }
            installKey.Enabled = true;
            output.Text = "Neuer Ed25519-Schlüssel erstellt.\r\nPrivater Schlüssel: " + ssh.KeyPath +
                          "\r\nDer private Schlüssel wird nicht an den Server übertragen.";
            SetStatus("Schlüssel erstellt. Installiere jetzt den öffentlichen Schlüssel auf dem Server.", false);
        }

        void InstallKey()
        {
            if (!ReadConnection()) return;
            if (!ssh.KeyExists)
            {
                SetStatus("Erstelle zuerst den SSH-Schlüssel.", true);
                return;
            }
            if (MessageBox.Show(this,
                "Es wird nur der öffentliche SSH-Schlüssel übertragen. Im geöffneten Konsolenfenster gibst du das Serverpasswort ein.",
                "SSH-Schlüssel installieren", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;
            ssh.InstallKeyInteractive();
            SetStatus("Konsolenfenster geöffnet. Nach erfolgreicher Installation hier „Server prüfen“ auswählen.", false);
        }

        async Task InspectServer()
        {
            if (!ReadConnection()) return;
            if (!ssh.ClientAvailable || !ssh.KeyExists)
            {
                SetStatus(!ssh.ClientAvailable ? "Der Windows-OpenSSH-Client ist nicht installiert." : "Erstelle und installiere zuerst den SSH-Schlüssel.", true);
                return;
            }

            inspect.Enabled = false;
            install.Enabled = false;
            proceed.Enabled = false;
            output.Text = "Verbinde über SSH und lese System- und Containerstatus ...";
            var result = await ssh.Run(InventoryScript, 60000);
            inspect.Enabled = true;
            if (!result.Ok)
            {
                inventory = null;
                output.Text = RemoveDatabasePassword(result.Message);
                SetStatus("SSH-Prüfung fehlgeschlagen.", true);
                return;
            }

            inventory = ParseInventory(result.Out);
            output.Text = DescribeInventory(inventory);
            install.Enabled = inventory.Debian13 && inventory.CanInstall &&
                              (!inventory.Docker || !inventory.Compose || !inventory.Crafty || !inventory.Database);
            proceed.Enabled = inventory.Crafty && inventory.Database;
            if (inventory.CraftyData && !inventory.Crafty)
                SetStatus("Crafty-Daten gefunden, aber kein Crafty-Container. Zum Schutz der Daten wird nichts neu angelegt.", true);
            else if (inventory.Crafty)
                SetStatus(inventory.Database
                    ? "Crafty und Datenbank wurden erkannt. Bestehende Container und Daten bleiben erhalten."
                    : "Crafty wurde erkannt. Eine fehlende Datenbank kann ergänzt werden.", false);
            else if (!inventory.Debian13)
                SetStatus("Automatische Installation ist nur für Debian 13 freigegeben.", true);
            else if (!inventory.CanInstall)
                SetStatus("Für die Installation ist root oder ein Benutzer mit passwortlosem sudo erforderlich.", true);
            else
                SetStatus("Fehlende Komponenten können jetzt installiert werden. Datenbankports und Firewall bleiben geschlossen.", false);
        }

        async Task InstallMissing()
        {
            if (inventory == null || !inventory.Debian13 || !inventory.CanInstall) return;
            if (MessageBox.Show(this,
                "PXMX ergänzt fehlende Docker-/Compose-Pakete, Crafty und MariaDB. Erkannte Container und vorhandene Daten werden nicht überschrieben.\r\n\r\n" +
                "Craftys Weboberfläche wird nur an localhost gebunden. Wenn PXMX MariaDB neu anlegt, wird crafty_container ohne Neustart mit dem PXMX-Docker-Netz verbunden. Die Firewall wird nicht verändert.",
                "Installation bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            inspect.Enabled = false;
            install.Enabled = false;
            output.Text = "Installation läuft. Das kann einige Minuten dauern ...";
            SetStatus("Installiere fehlende Komponenten über SSH ...", false);
            var result = await ssh.Run(InstallScript, 1200000);
            string dbPassword;
            var detail = result.Out ?? "";
            if (TryReadDatabasePassword(detail, out dbPassword))
            {
                settings.DatabasePassword = dbPassword;
                settings.Save();
                databaseAccess.Enabled = true;
                detail = RemoveDatabasePassword(detail);
            }
            output.Text = result.Ok ? detail.Trim() : RemoveDatabasePassword(result.Message);
            if (!result.Ok)
            {
                inspect.Enabled = true;
                SetStatus("Installation nicht vollständig. Details stehen im Ausgabefeld.", true);
                return;
            }
            await InspectServer();
        }

        void Proceed()
        {
            if (inventory == null || !inventory.Crafty || !inventory.Database) return;
            settings.SetupComplete = true;
            settings.TeamMode = false;
            if (string.IsNullOrWhiteSpace(settings.Url))
                settings.Url = "https://" + settings.SshHost + ":8443";
            settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        }

        void ShowDatabaseAccess()
        {
            if (string.IsNullOrEmpty(settings.DatabasePassword))
            {
                MessageBox.Show(this, "PXMX hat keine neuen Datenbankzugangsdaten erstellt. Ein vorhandener Datenbankcontainer bleibt unverändert.",
                    "Datenbank", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            MessageBox.Show(this,
                "Host: " + settings.DbHost + " (nur Docker-Netz pxmx-services)\r\nDatenbank: " + settings.DbName +
                "\r\nBenutzer: " + settings.DbUser + "\r\nPasswort: " + settings.DatabasePassword,
                "PXMX MariaDB-Zugang", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static Inventory ParseInventory(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int at = line.IndexOf('=');
                if (at > 0) values[line.Substring(0, at)] = line.Substring(at + 1);
            }
            string value;
            Func<string, bool> flag = key => values.TryGetValue(key, out value) && value == "1";
            return new Inventory
            {
                Os = values.TryGetValue("OS", out value) ? value : "",
                Version = values.TryGetValue("VERSION", out value) ? value : "",
                Root = flag("ROOT"),
                Sudo = flag("SUDO"),
                Docker = flag("DOCKER"),
                Compose = flag("COMPOSE"),
                Crafty = flag("CRAFTY"),
                Database = flag("DATABASE"),
                CraftyData = flag("CRAFTY_DATA")
            };
        }

        static bool TryReadDatabasePassword(string text, out string password)
        {
            password = null;
            foreach (var line in (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                const string prefix = "PXMX_DB_PASSWORD=";
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var value = line.Substring(prefix.Length);
                if (!Regex.IsMatch(value, "^[a-fA-F0-9]{64}$")) return false;
                password = value;
                return true;
            }
            return false;
        }

        static string RemoveDatabasePassword(string text)
        {
            var lines = (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var safe = new List<string>();
            foreach (var line in lines)
                if (!line.StartsWith("PXMX_DB_PASSWORD=", StringComparison.Ordinal)) safe.Add(line);
            return string.Join(Environment.NewLine, safe.ToArray());
        }

        static string DescribeInventory(Inventory i)
        {
            var b = new StringBuilder();
            b.AppendLine("System: " + (i.Os.Length == 0 ? "unbekannt" : i.Os + " " + i.Version));
            b.AppendLine("Installationsrechte: " + (i.Root ? "root" : i.Sudo ? "sudo verfügbar" : "nicht verfügbar"));
            b.AppendLine("Docker Engine: " + (i.Docker ? "vorhanden" : "fehlt"));
            b.AppendLine("Docker Compose: " + (i.Compose ? "vorhanden" : "fehlt"));
            b.AppendLine("Crafty: " + (i.Crafty ? "vorhanden – unverändert" : "nicht erkannt"));
            b.Append("Datenbank-Container: " + (i.Database ? "vorhanden – unverändert" : "nicht erkannt"));
            if (i.CraftyData) b.AppendLine().Append("Hinweis: Crafty-Konfigurationsdaten auf dem Datenträger gefunden.");
            return b.ToString();
        }

        void SetStatus(string text, bool error)
        {
            status.Text = text;
            status.ForeColor = error ? Color.FromArgb(255, 130, 120) : Color.FromArgb(172, 181, 194);
        }

        const string InventoryScript = @"set -eu
if [ -r /etc/os-release ]; then . /etc/os-release; else ID=unknown; VERSION_ID=unknown; fi
printf 'OS=%s\nVERSION=%s\n' ""${ID:-unknown}"" ""${VERSION_ID:-unknown}""
if [ ""$(id -u)"" -eq 0 ]; then printf 'ROOT=1\n'; else printf 'ROOT=0\n'; fi
if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then printf 'SUDO=1\n'; else printf 'SUDO=0\n'; fi
DOCKER_CMD=''
if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then DOCKER_CMD='docker'
elif command -v sudo >/dev/null 2>&1 && sudo -n docker info >/dev/null 2>&1; then DOCKER_CMD='sudo docker'; fi
if [ -n ""$DOCKER_CMD"" ]; then
  printf 'DOCKER=1\n'
  if $DOCKER_CMD compose version >/dev/null 2>&1; then printf 'COMPOSE=1\n'; else printf 'COMPOSE=0\n'; fi
  if $DOCKER_CMD ps -a --format '{{.Names}} {{.Image}}' | grep -iq crafty; then printf 'CRAFTY=1\n'; else printf 'CRAFTY=0\n'; fi
  if $DOCKER_CMD ps -a --format '{{.Names}} {{.Image}}' | grep -Eiq 'mariadb|mysql'; then printf 'DATABASE=1\n'; else printf 'DATABASE=0\n'; fi
else
  printf 'DOCKER=0\nCOMPOSE=0\nCRAFTY=0\nDATABASE=0\n'
fi
if [ -d /opt/crafty/docker/config ] && [ -n ""$(find /opt/crafty/docker/config -mindepth 1 -print -quit 2>/dev/null)"" ]; then
  printf 'CRAFTY_DATA=1\n'
else
  printf 'CRAFTY_DATA=0\n'
fi
";

        const string InstallScript = @"set -eu
. /etc/os-release
if [ ""${ID:-}"" != debian ] || [ ""${VERSION_ID:-}"" != 13 ]; then
  echo 'PXMX_INSTALL_ERROR: automatische Installation ist nur für Debian 13 freigegeben.'
  exit 20
fi
if [ ""$(id -u)"" -eq 0 ]; then SUDO=''; else sudo -n true; SUDO=sudo; fi
run_priv() { if [ -n ""$SUDO"" ]; then sudo ""$@""; else ""$@""; fi; }
if ! command -v docker >/dev/null 2>&1; then
  run_priv apt-get update
  run_priv env DEBIAN_FRONTEND=noninteractive apt-get install -y docker.io
fi
if ! docker compose version >/dev/null 2>&1; then
  run_priv apt-get update
  run_priv env DEBIAN_FRONTEND=noninteractive apt-get install -y docker-compose
fi
run_priv systemctl enable --now docker
if docker info >/dev/null 2>&1; then DOCKER_CMD='docker'
elif command -v sudo >/dev/null 2>&1 && sudo -n docker info >/dev/null 2>&1; then DOCKER_CMD='sudo docker'
else echo 'PXMX_INSTALL_ERROR: Docker-Dienst ist nicht erreichbar.'; exit 21; fi
if ! $DOCKER_CMD compose version >/dev/null 2>&1; then
  echo 'PXMX_INSTALL_ERROR: Docker Compose konnte nicht bereitgestellt werden.'
  exit 22
fi
if ! $DOCKER_CMD ps -a --format '{{.Names}} {{.Image}}' | grep -iq crafty; then
  if [ -d /opt/crafty/docker/config ] && [ -n ""$(find /opt/crafty/docker/config -mindepth 1 -print -quit 2>/dev/null)"" ]; then
    echo 'PXMX_INSTALL_ERROR: vorhandene Crafty-Daten erkannt; Container wird aus Sicherheitsgründen nicht erstellt.'
    exit 23
  fi
  run_priv mkdir -p /opt/crafty/docker/backups /opt/crafty/docker/logs /opt/crafty/docker/servers /opt/crafty/docker/config /opt/crafty/docker/import
  $DOCKER_CMD run --detach --name crafty_container --restart always \
    --publish 127.0.0.1:8443:8443 --publish 127.0.0.1:8123:8123 \
    --publish 19132:19132/udp --publish 25500-25600:25500-25600 \
    --env TZ=Etc/UTC \
    --volume /opt/crafty/docker/backups:/crafty/backups \
    --volume /opt/crafty/docker/logs:/crafty/logs \
    --volume /opt/crafty/docker/servers:/crafty/servers \
    --volume /opt/crafty/docker/config:/crafty/app/config \
    --volume /opt/crafty/docker/import:/crafty/import \
    registry.gitlab.com/crafty-controller/crafty-4:latest
fi
if ! $DOCKER_CMD ps -a --format '{{.Names}} {{.Image}}' | grep -Eiq 'mariadb|mysql'; then
  run_priv mkdir -p /opt/pxmx
  if [ -f /etc/pxmx/mariadb.env ]; then
    for pair in 'MARIADB_DATABASE=minecraft' 'MARIADB_USER=pxmx'; do
      if ! run_priv grep -qx ""$pair"" /etc/pxmx/mariadb.env; then
        echo 'PXMX_INSTALL_ERROR: bestehende MariaDB-Konfiguration passt nicht zum PXMX-Standard.'
        exit 24
      fi
    done
    DB_PASS=$(run_priv sed -n 's/^MARIADB_PASSWORD=//p' /etc/pxmx/mariadb.env)
  else
    if [ -d /opt/pxmx/mariadb ] && [ -n ""$(run_priv find /opt/pxmx/mariadb -mindepth 1 -print -quit 2>/dev/null)"" ]; then
      echo 'PXMX_INSTALL_ERROR: vorhandene MariaDB-Daten ohne PXMX-Konfiguration erkannt; nichts wird überschrieben.'
      exit 25
    fi
    DB_PASS=$(openssl rand -hex 32)
    ROOT_PASS=$(openssl rand -hex 32)
    run_priv mkdir -p /etc/pxmx
    printf 'MARIADB_ROOT_PASSWORD=%s\nMARIADB_DATABASE=minecraft\nMARIADB_USER=pxmx\nMARIADB_PASSWORD=%s\n' ""$ROOT_PASS"" ""$DB_PASS"" | run_priv tee /etc/pxmx/mariadb.env >/dev/null
    run_priv chmod 600 /etc/pxmx/mariadb.env
  fi
  if ! printf '%s' ""$DB_PASS"" | grep -Eq '^[a-fA-F0-9]{64}$'; then
    echo 'PXMX_INSTALL_ERROR: Datenbankpasswort konnte nicht sicher gelesen werden.'
    exit 26
  fi
  if ! $DOCKER_CMD network inspect pxmx-services >/dev/null 2>&1; then
    $DOCKER_CMD network create --label com.pxmx.managed=true pxmx-services >/dev/null
  elif [ ""$($DOCKER_CMD network inspect --format '{{ index .Labels ""com.pxmx.managed"" }}' pxmx-services)"" != true ]; then
    echo 'PXMX_INSTALL_ERROR: Docker-Netzwerk pxmx-services existiert, gehört aber nicht zu PXMX.'
    exit 27
  fi
  if $DOCKER_CMD inspect crafty_container >/dev/null 2>&1 &&
     ! $DOCKER_CMD inspect --format '{{json .NetworkSettings.Networks}}' crafty_container | grep -q '""pxmx-services""'; then
    $DOCKER_CMD network connect pxmx-services crafty_container
  fi
  run_priv mkdir -p /opt/pxmx/mariadb
  $DOCKER_CMD run --detach --name mariadb --restart unless-stopped \
    --network pxmx-services \
    --env-file /etc/pxmx/mariadb.env \
    --volume /opt/pxmx/mariadb:/var/lib/mysql \
    mariadb:11.4
  printf 'PXMX_DB_PASSWORD=%s\n' ""$DB_PASS""
fi
echo 'PXMX_INSTALL_OK'
";
    }
}
