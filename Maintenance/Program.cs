using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Forms;

namespace MasterHubMaintenance;

internal static class Program
{
    private const string ReleasesApi = "https://api.github.com/repos/masterhub04/MASTER-RGB/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MaintenanceWindow());
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MasterHubMaintenance", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class MaintenanceWindow : Form
    {
        private readonly Button _uninstall = new() { Text = "Desinstalar", Width = 310, Height = 46 };
        private readonly Button _updates = new() { Text = "Procurar atualizações", Width = 310, Height = 46 };
        private readonly Button _repair = new() { Text = "Reparar instalação", Width = 310, Height = 46 };
        private readonly Label _status = new() { Text = "Escolha uma opção de manutenção.", AutoSize = false, Width = 340, Height = 56, TextAlign = System.Drawing.ContentAlignment.MiddleCenter };
        private readonly ProgressBar _progress = new() { Width = 310, Height = 16, Visible = false };
        private readonly string _currentDirectory = AppContext.BaseDirectory;
        private bool _busy;

        public MaintenanceWindow()
        {
            Text = "MASTER HUB - Manutenção";
            Width = 410;
            Height = 360;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = false,
                Padding = new Padding(28, 14, 15, 10)
            };
            var title = new Label
            {
                Text = "MASTER HUB - Manutenção",
                Width = 330, Height = 35,
                Font = new System.Drawing.Font("Segoe UI", 13, System.Drawing.FontStyle.Bold),
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter
            };
            layout.Controls.Add(title);
            layout.Controls.Add(_updates);
            layout.Controls.Add(_repair);
            layout.Controls.Add(_uninstall);
            layout.Controls.Add(_progress);
            layout.Controls.Add(_status);
            Controls.Add(layout);
            _updates.Click += async (_, _) => await CheckUpdatesAsync();
            _repair.Click += async (_, _) => await RepairAsync();
            _uninstall.Click += (_, _) => Uninstall();
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            _updates.Enabled = _repair.Enabled = _uninstall.Enabled = !busy;
            _status.Text = message;
            UseWaitCursor = busy;
        }

        private async Task<JsonDocument> GetLatestReleaseAsync()
        {
            using var response = await Client.GetAsync(ReleasesApi);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        private async Task CheckUpdatesAsync()
        {
            if (_busy) return;
            SetBusy(true, "Consultando as versões publicadas...");
            try
            {
                using var release = await GetLatestReleaseAsync();
                var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
                var installed = FileVersionInfo.GetVersionInfo(Path.Combine(_currentDirectory, "MASTER HUB.exe")).ProductVersion ?? "";
                var remote = tag.TrimStart('v', 'V');
                if (Version.TryParse(remote, out var remoteVersion) &&
                    Version.TryParse(installed.Split('+')[0], out var installedVersion))
                {
                    if (remoteVersion <= installedVersion)
                    {
                        MessageBox.Show(this, "Você já possui a versão mais recente (" + installed + ").",
                            "MASTER HUB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }
                var choice = MessageBox.Show(this,
                    "Versão instalada: " + (string.IsNullOrWhiteSpace(installed) ? "não identificada" : installed) +
                    "\nVersão disponível: " + tag +
                    "\n\nDeseja baixar e instalar a versão publicada?",
                    "Atualização disponível", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (choice == DialogResult.Yes) await DownloadAndRunSetupAsync(release.RootElement);
            }
            catch (Exception ex)
            {
                ShowError("Não foi possível consultar as atualizações. Verifique a conexão e o acesso ao repositório.", ex);
            }
            finally { SetBusy(false, "Escolha uma opção de manutenção."); _progress.Visible = false; }
        }

        private async Task RepairAsync()
        {
            if (_busy) return;
            var choice = MessageBox.Show(this,
                "A reparação baixará o instalador oficial mais recente e o executará novamente.\n\n" +
                "Feche o MASTER HUB antes de continuar. Seus dados de usuário não serão apagados por este utilitário.\n\nContinuar?",
                "Reparar MASTER HUB", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;
            SetBusy(true, "Localizando o instalador oficial...");
            try
            {
                using var release = await GetLatestReleaseAsync();
                await DownloadAndRunSetupAsync(release.RootElement);
            }
            catch (Exception ex)
            {
                ShowError("Não foi possível iniciar a reparação. Verifique sua conexão e tente novamente.", ex);
            }
            finally { SetBusy(false, "Escolha uma opção de manutenção."); _progress.Visible = false; }
        }

        private async Task DownloadAndRunSetupAsync(JsonElement release)
        {
            string? url = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("Instalador Setup.exe não encontrado na versão publicada.");
            _progress.Visible = true;
            _progress.Style = ProgressBarStyle.Marquee;
            _status.Text = "Baixando instalador oficial...";

            var file = Path.Combine(Path.GetTempPath(), "MASTER-HUB-Repair-" + Guid.NewGuid().ToString("N") + "-Setup.exe");
            try
            {
                using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = File.Create(file))
                    await input.CopyToAsync(output);

                if (new FileInfo(file).Length < 1024 * 1024)
                    throw new InvalidDataException("O arquivo baixado não parece ser um instalador válido.");

                _status.Text = "Abrindo o instalador...";
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                MessageBox.Show(this, "Instalador iniciado. Siga as instruções na tela para concluir.",
                    "MASTER HUB", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch
            {
                try { File.Delete(file); } catch { }
                throw;
            }
        }

        private void Uninstall()
        {
            if (_busy) return;
            var updater = Path.GetFullPath(Path.Combine(_currentDirectory, "..", "Update.exe"));
            if (!File.Exists(updater))
            {
                MessageBox.Show(this, "O desinstalador Velopack não foi encontrado.\n" +
                    "Você pode desinstalar em Configurações > Aplicativos > Aplicativos instalados.",
                    "MASTER HUB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var choice = MessageBox.Show(this,
                "Deseja desinstalar o MASTER HUB deste computador?\n\n" +
                "O desinstalador oficial removerá os arquivos do aplicativo.",
                "Confirmar desinstalação", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes) return;
            try
            {
                Process.Start(new ProcessStartInfo(updater)
                {
                    ArgumentList = { "uninstall" },
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(updater)!
                });
                Close();
            }
            catch (Exception ex) { ShowError("Não foi possível iniciar o desinstalador.", ex); }
        }

        private void ShowError(string message, Exception ex)
        {
            MessageBox.Show(this, message + "\n\nDetalhes: " + ex.Message,
                "MASTER HUB", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
