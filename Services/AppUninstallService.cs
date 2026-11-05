using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MasterRgb.Services;

public sealed class AppUninstallService
{
    private sealed record Entry(string Id, string Name, string Publisher, string Version, string Location, string Drive, string Command);
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _snapshot = new(StringComparer.Ordinal);
    private static readonly (RegistryHive hive, RegistryView view, string path)[] Sources =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
    };

    public object List()
    {
        var entries = new List<Entry>();
        foreach (var source in Sources)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(source.hive, source.view);
                using var uninstall = root.OpenSubKey(source.path);
                if (uninstall is null) continue;
                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(keyName);
                        if (key is null) continue;
                        string name = Convert.ToString(key.GetValue("DisplayName"))?.Trim() ?? "";
                        if (name.Length == 0 || Convert.ToInt32(key.GetValue("SystemComponent", 0)) == 1 ||
                            key.GetValue("ParentKeyName") is not null || key.GetValue("ReleaseType") is not null) continue;
                        string command = Convert.ToString(key.GetValue("UninstallString"))?.Trim() ?? "";
                        if (command.Length == 0) continue;
                        string location = Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("InstallLocation"))?.Trim() ?? "");
                        string drive = "";
                        if (Path.IsPathFullyQualified(location) && location.Length >= 2 && location[1] == ':')
                            drive = location[..2].ToUpperInvariant();
                        string id = source.hive + "|" + source.view + "|" + keyName;
                        entries.Add(new Entry(id, name, Convert.ToString(key.GetValue("Publisher")) ?? "",
                            Convert.ToString(key.GetValue("DisplayVersion")) ?? "", location, drive, command));
                    }
                    catch { }
                }
            }
            catch { }
        }
        lock (_sync)
        {
            _snapshot.Clear();
            foreach (var entry in entries) _snapshot[entry.Id] = entry;
        }
        return new
        {
            drives = entries.Select(x => x.Drive).Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToArray(),
            items = entries.OrderBy(x => x.Name).Select(x => new {
                id = x.Id, name = x.Name, publisher = x.Publisher, version = x.Version,
                location = x.Location, drive = x.Drive, hasKnownDrive = x.Drive.Length > 0
            }).ToArray()
        };
    }

    public async Task<object> UninstallSelectedAsync(string[] ids)
    {
        if (ids.Length is 0 or > 50 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidOperationException("Selecione de 1 a 50 aplicativos diferentes.");
        List<Entry> selected;
        lock (_sync)
        {
            if (ids.Any(id => !_snapshot.ContainsKey(id)))
                throw new InvalidOperationException("Lista desatualizada. Atualize os aplicativos antes de desinstalar.");
            selected = ids.Select(id => _snapshot[id]).ToList();
        }
        var results = new List<object>();
        foreach (var app in selected)
        {
            try
            {
                // Usa exclusivamente o comando de desinstalação registrado pelo fabricante.
                // Não adiciona parâmetros silenciosos nem executa comandos via shell.
                var command = app.Command.Trim();
                string executable, arguments;
                if (command.StartsWith('"'))
                {
                    int closing = command.IndexOf('"', 1);
                    if (closing < 2) throw new InvalidOperationException("Comando inválido.");
                    executable = command[1..closing];
                    arguments = command[(closing + 1)..].Trim();
                }
                else
                {
                    int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                    if (exe < 0) throw new InvalidOperationException("Desinstalador não executável ou não suportado.");
                    executable = command[..(exe + 4)].Trim();
                    arguments = command[(exe + 4)..].Trim();
                }
                if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Formato de desinstalador não suportado.");
                if (Path.GetFileName(executable).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var trimmed = arguments.TrimStart();
                    if (trimmed.StartsWith("/I", StringComparison.OrdinalIgnoreCase))
                        arguments = "/X" + trimmed[2..];
                }
                var info = new ProcessStartInfo(executable, arguments) { UseShellExecute = true };
                using var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o desinstalador.");
                await process.WaitForExitAsync();
                results.Add(new { name = app.Name, status = process.ExitCode == 0 ? "finalizado" : "verificar", message = "Desinstalador encerrado com código " + process.ExitCode + ". Confirme a remoção atualizando a lista." });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                results.Add(new { name = app.Name, status = "cancelado", message = "Autorização cancelada. Os próximos aplicativos não foram executados." });
                break;
            }
            catch (Exception ex)
            {
                results.Add(new { name = app.Name, status = "erro", message = ex.Message });
            }
        }
        return results;
    }
}
