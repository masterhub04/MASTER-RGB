using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MasterRgb.Services;

public sealed record UpdateCheckResult(bool Success, bool UpdateAvailable, string CurrentVersion, string LatestVersion, string? ReleaseUrl, string Message);

public sealed class GitHubReleaseChecker
{
    private const string Owner = "masterhub04";
    private const string Repository = "MASTER-RGB";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MASTER-RGB-Updater/0.8.5");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<UpdateCheckResult> CheckLatestAsync()
    {
        string currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.8.5";
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new(true, false, currentVersion, currentVersion, null,
                    "Ainda não existe uma Release publicada no GitHub. A verificação ficará pronta assim que a primeira versão compilada for publicada.");
            if (!response.IsSuccessStatusCode)
                return new(false, false, currentVersion, currentVersion, null,
                    $"O GitHub respondeu com o código {(int)response.StatusCode}. Tente novamente mais tarde.");

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string tag = json.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = json.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var latest))
                return new(false, false, currentVersion, tag, url, $"A Release mais recente ({tag}) não possui uma versão reconhecida.");

            if (!Version.TryParse(currentVersion, out var installed))
                installed = new Version(0, 0, 0);
            bool available = latest > installed;
            return new(true, available, currentVersion, latest.ToString(3), url,
                available ? "Uma nova versão está disponível." : "Você já está usando a versão mais recente.");
        }
        catch (TaskCanceledException)
        {
            return new(false, false, currentVersion, currentVersion, null, "A consulta ao GitHub demorou demais. Verifique sua conexão e tente novamente.");
        }
        catch (HttpRequestException ex)
        {
            return new(false, false, currentVersion, currentVersion, null, "Não foi possível conectar ao GitHub: " + ex.Message);
        }
        catch (Exception ex)
        {
            return new(false, false, currentVersion, currentVersion, null, "Erro ao verificar atualizações: " + ex.Message);
        }
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        string normalized = tag.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
        int suffix = normalized.IndexOfAny(new[] { '-', '+' });
        if (suffix >= 0) normalized = normalized[..suffix];
        return Version.TryParse(normalized, out version!);
    }
}
