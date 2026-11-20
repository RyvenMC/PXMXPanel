using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    class SpigotPluginDialog : DarkForm
    {
        const long MaxPluginSize = 95L * 1024 * 1024;
        const string SpigetApi = "https://api.spiget.org/v2/";

        readonly Api api;
        readonly ServerItem server;
        readonly Field search;
        readonly ListView results;
        readonly Label detail, status;
        readonly FlatBtn install, searchButton;
        bool busy;

        public SpigotPluginDialog(Api api, ServerItem server)
        {
            this.api = api;
            this.server = server;
            Text = "Spigot-Plugins · " + server.Name;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MinimumSize = new Size(720, 500);
            ClientSize = new Size(820, 570);

            var searchBar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Theme.Bg, Padding = new Padding(12, 10, 12, 8) };
            search = new Field(false) { Dock = DockStyle.Fill, Margin = new Padding(0) };
            search.Placeholder = "Plugin auf Spigot suchen (z. B. AdvancedBan oder BanSystem)";
            searchButton = new FlatBtn("Suchen", Theme.Accent) { Dock = DockStyle.Right, Width = 104, Height = 38, Margin = new Padding(8, 0, 0, 0) };
            searchBar.Controls.Add(search);
            searchBar.Controls.Add(searchButton);

            results = Ui.List(new[] { "Plugin", "Version", "MC-Versionen", "Aktualisiert" }, new[] { 180, 75, 190, 100 });
            results.Dock = DockStyle.Fill;
            results.SelectedIndexChanged += (s, e) => ShowSelection();

            var side = new Panel { Dock = DockStyle.Bottom, Height = 148, BackColor = Theme.Bg, Padding = new Padding(12, 8, 12, 10) };
            detail = Theme.Lbl("Plugin auswählen. Downloads kommen aus dem SpigotMC-Katalog über die Spiget-API.", Theme.F(9.25f), Theme.Text2, Theme.Bg);
            detail.Dock = DockStyle.Fill;
            detail.AutoEllipsis = true;
            detail.Padding = new Padding(2, 4, 2, 4);
            status = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            status.Dock = DockStyle.Bottom;
            status.Height = 22;
            install = new FlatBtn("Ausgewähltes Plugin installieren", Theme.Accent) { Dock = DockStyle.Right, Width = 240, Height = 36, Enabled = false };
            install.Click += async (s, e) => await InstallSelected();
            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Theme.Bg };
            buttons.Controls.Add(install);
            side.Controls.Add(detail);
            side.Controls.Add(status);
            side.Controls.Add(buttons);

            Controls.Add(results);
            Controls.Add(side);
            Controls.Add(searchBar);
            AcceptButton = searchButton;
            searchButton.Click += async (s, e) => await Search();
            search.Box.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    await Search();
                }
            };
            Shown += async (s, e) => { search.Box.Text = "AdvancedBan"; await Search(); };
        }

        async Task Search()
        {
            if (busy) return;
            var term = search.Box.Text.Trim();
            if (term.Length < 2 || term.Length > 80)
            {
                status.Text = "Suchbegriff muss 2 bis 80 Zeichen lang sein.";
                status.ForeColor = Theme.Yellow;
                return;
            }

            busy = true;
            searchButton.Enabled = false;
            install.Enabled = false;
            status.Text = "Suche auf Spigot …";
            status.ForeColor = Theme.Muted;
            try
            {
                var url = SpigetApi + "search/resources/" + Uri.EscapeDataString(term) + "?size=25";
                var response = await GetJson(url);
                var rows = new List<ListViewItem>();
                foreach (var resource in J.L(response))
                {
                    var file = J.Get(resource, "file");
                    if (J.B(resource, "external") || J.B(resource, "premium") ||
                        (J.Get(resource, "existenceStatus") != null && J.N(resource, "existenceStatus") != 1) ||
                        !string.Equals(J.S(file, "type"), ".jar", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var tested = string.Join(", ", J.L(J.Get(resource, "testedVersions")));
                    var update = J.N(resource, "updateDate");
                    var updated = update > 0
                        ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(update).ToLocalTime().ToString("yyyy-MM-dd")
                        : "keine Angabe";
                    var row = Ui.Row(new RowTag { Key = J.S(resource, "id"), Data = resource },
                        J.S(resource, "name") ?? "Unbenannt", J.S(J.Get(resource, "version"), "name") ?? "–",
                        tested.Length == 0 ? "keine Angabe" : tested, updated);
                    rows.Add(row);
                }
                Ui.Fill(results, rows);
                status.Text = rows.Count == 0
                    ? "Keine passenden kostenlosen Spigot-JARs gefunden. Suche z. B. nach „BanSystem“."
                    : rows.Count + " auswählbare Plugins gefunden. Bitte Version und getestete MC-Version prüfen.";
                status.ForeColor = rows.Count == 0 ? Theme.Yellow : Theme.Muted;
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    Ui.Fill(results, new List<ListViewItem>());
                    status.Text = "Keine passenden Plugins gefunden. Suche z. B. nach „BanSystem“.";
                    status.ForeColor = Theme.Yellow;
                }
                else
                {
                    status.Text = "Spigot-Suche fehlgeschlagen: " + ex.Message;
                    status.ForeColor = Theme.Red;
                }
            }
            catch (IOException ex)
            {
                status.Text = "Spigot-Suche fehlgeschlagen: " + ex.Message;
                status.ForeColor = Theme.Red;
            }
            catch (ArgumentException ex)
            {
                status.Text = "Ungültige Spigot-Antwort: " + ex.Message;
                status.ForeColor = Theme.Red;
            }
            finally
            {
                busy = false;
                searchButton.Enabled = true;
            }
        }

        void ShowSelection()
        {
            var row = results.SelectedItems.Count == 0 ? null : results.SelectedItems[0];
            var resource = row == null ? null : ((RowTag)row.Tag).Data;
            install.Enabled = resource != null && !busy;
            if (resource == null)
            {
                detail.Text = "Plugin auswählen. Downloads kommen aus dem SpigotMC-Katalog über die Spiget-API.";
                return;
            }

            var id = J.S(resource, "id");
            var name = J.S(resource, "name") ?? "Unbenannt";
            var version = J.S(J.Get(resource, "version"), "name") ?? "unbekannt";
            var tag = J.S(resource, "tag") ?? "";
            detail.Text = name + " · Version " + version + " · Spigot-Resource " + id +
                          (tag.Length > 0 ? "\r\n" + tag : "") +
                          "\r\nDer JAR-Upload erfolgt nach /plugins auf „" + server.Name +
                          "“. Der Server wird nicht automatisch neu gestartet.";
        }

        async Task InstallSelected()
        {
            if (busy || results.SelectedItems.Count == 0) return;
            var resource = ((RowTag)results.SelectedItems[0].Tag).Data;
            var file = J.Get(resource, "file");
            var filePath = J.S(file, "url");
            var name = J.S(resource, "name") ?? "Plugin";
            var id = J.S(resource, "id");
            var version = J.S(J.Get(resource, "version"), "name") ?? "unbekannt";

            var versionMatch = Regex.Match(filePath ?? "", @"\?version=([0-9]+)$");
            if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, @"^[0-9]+$") ||
                !Regex.IsMatch(filePath ?? "", @"^resources/[A-Za-z0-9._-]+/download\?version=[0-9]+$") ||
                !versionMatch.Success)
            {
                status.Text = "Die Spigot-Antwort enthält keinen gültigen Downloadpfad.";
                status.ForeColor = Theme.Red;
                return;
            }

            var resourceUrl = "https://www.spigotmc.org/resources/" + filePath.Substring("resources/".Length).Split('/')[0] + "/";
            if (!Dialogs.Confirm(this, "Plugin installieren",
                "„" + name + "“ v" + version + " von SpigotMC auf „" + server.Name + "“ laden?\n\n" +
                "Quelle: " + resourceUrl + "\n\n" +
                "Plugins sind Drittanbieter-Code und können Serverrechte erhalten. Prüfe Entwickler und Minecraft-Kompatibilität vor der Installation. " +
                "Eine gleichnamige Datei wird ersetzt; der Server startet nicht automatisch neu."))
                return;

            busy = true;
            searchButton.Enabled = false;
            install.Enabled = false;
            status.Text = "Plugin wird heruntergeladen und geprüft …";
            status.ForeColor = Theme.Muted;
            try
            {
                var downloadUrl = SpigetApi + "resources/" + id + "/download?version=" + versionMatch.Groups[1].Value;
                var bytes = await Download(downloadUrl);
                if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4b)
                    throw new InvalidDataException("Der Download ist kein gültiges JAR/ZIP-Archiv.");

                var safeName = Regex.Replace(name, @"[^A-Za-z0-9._-]+", "_").Trim('.', '_');
                if (safeName.Length == 0) safeName = "plugin-" + id;
                var jarName = safeName + ".jar";
                var headers = new Dictionary<string, string>
                {
                    { "fileName", jarName },
                    { "location", "plugins" },
                    { "fileSize", bytes.Length.ToString() },
                    { "fileId", Guid.NewGuid().ToString() }
                };
                status.Text = "Lade " + jarName + " auf den Server …";
                var uploaded = await api.SendRaw("POST", "/api/v2/servers/" + server.Id + "/files/upload",
                    bytes, "application/octet-stream", headers, 600000);
                if (!uploaded.Ok)
                    throw new InvalidOperationException("Upload fehlgeschlagen: " + uploaded.Error);

                status.Text = jarName + " installiert. Zum Laden den Server manuell neu starten.";
                status.ForeColor = Theme.Green;
                install.Enabled = false;
            }
            catch (WebException ex)
            {
                status.Text = "Download fehlgeschlagen: " + ex.Message;
                status.ForeColor = Theme.Red;
            }
            catch (IOException ex)
            {
                status.Text = "Plugin-Installation fehlgeschlagen: " + ex.Message;
                status.ForeColor = Theme.Red;
            }
            catch (InvalidOperationException ex)
            {
                status.Text = ex.Message;
                status.ForeColor = Theme.Red;
            }
            finally
            {
                busy = false;
                searchButton.Enabled = true;
                if (results.SelectedItems.Count > 0) install.Enabled = true;
            }
        }

        static async Task<object> GetJson(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = "PXMX-ServerSuite/0.1.0";
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return J.Parse(await reader.ReadToEndAsync());
        }

        static async Task<byte[]> Download(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "PXMX-ServerSuite/0.1.0";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            {
                if (response.ContentLength > MaxPluginSize)
                    throw new InvalidDataException("Plugin ist größer als das Crafty-Upload-Limit von 95 MB.");
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaxPluginSize)
                            throw new InvalidDataException("Plugin ist größer als das Crafty-Upload-Limit von 95 MB.");
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
        }
    }
}
