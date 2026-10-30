using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace MasterRgb.Services;

/// <summary>
/// Checks the latest public GitHub Release. In this prototype it reports the
/// release URL; in-app package installation will be added with Velopack after
/// the official repository and release pipeline are configured.
/// </summary>
public sealed class GitHubReleaseChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MASTER-RGB-Updater/0.1.0");
        return client;
    }

    public async Task<string> CheckLatestAsync()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MASTER RGB", "Settings.json");

            if (!File.Exists(settingsPath))
                return "Configure GitHubOwner e GitHubRepo em %AppData%\\MASTER RGB\\Settings.json. Veja README.md.";

            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            var owner = settings.RootElement.GetProperty("GitHubOwner").GetString();
            var repo = settings.RootElement.GetProperty("GitHubRepo").GetString();

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo) ||
                owner == "SEU_USUARIO" || repo == "MASTER-RGB")
                return "O repositório ainda não foi configurado. Crie o repositório no GitHub e informe proprietário/nome no Settings.json.";

            using var response = await Http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            if (!response.IsSuccessStatusCode)
                return $"GitHub respondeu {(int)response.StatusCode}. Verifique se o repositório é público e se já existe uma Release.";

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tag = json.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : "versão desconhecida";
            var url = json.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : "";
            return $"Última Release encontrada: {tag}. Página: {url}. Nesta versão, a atualização ainda é manual; instalação automática será adicionada na etapa Velopack.";
        }
        catch (Exception ex)
        {
            return "Não foi possível verificar atualizações: " + ex.Message;
        }
    }
}
