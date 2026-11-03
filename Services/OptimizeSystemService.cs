using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MasterRgb.Services;

public sealed class OptimizeSystemService
{
    private const string StartupBackupKey = @"Software\MasterHub\OptimizeSystem\StartupBackup";
    private const string ServiceBackupKey = @"Software\MasterHub\OptimizeSystem\ServiceBackup";
    private const uint RecycleBinFlags = 0x00000001 | 0x00000002 | 0x00000004;

    private sealed record CleanupTarget(string Id, string Name, string Path, bool IsRecycleBin = false);
    private sealed record ServiceOption(string Name, string Description);

    private static readonly ServiceOption[] SafeOptionalServices =
    {
        new("SysMain", "Pré-carregamento de aplicativos. Em alguns PCs com SSD pode ser desnecessário; pode afetar o comportamento de carregamento."),
        new("WSearch", "Indexação de pesquisa do Windows. Desativar reduz indexação, mas deixa pesquisas de arquivos mais lentas."),
        new("DiagTrack", "Telemetria e experiências conectadas do Windows."),
        new("MapsBroker", "Gerenciador de mapas baixados. Necessário para mapas offline."),
        new("RetailDemo", "Serviço de demonstração de varejo; normalmente não é usado em PCs pessoais."),
        new("Fax", "Envio e recebimento de fax."),
        new("RemoteRegistry", "Permite alterações remotas no Registro. Normalmente não é necessário em um PC doméstico."),
        new("WMPNetworkSvc", "Compartilhamento de bibliotecas do Windows Media Player."),
        new("XblAuthManager", "Autenticação do Xbox Live. Necessário para alguns recursos e jogos Xbox."),
        new("XblGameSave", "Salvamento de jogos do Xbox Live."),
        new("XboxNetApiSvc", "Serviço de rede do Xbox Live."),
        new("XboxGipSvc", "Gerenciamento de acessórios Xbox.")
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    public object GetStartupItems()
    {
        var items = new List<object>();
        ReadRunItems(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Usuário atual", items);
        ReadRunItems(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Todos os usuários", items);
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.GetFiles(folder))
                items.Add(new { id = "file|" + file, name = Path.GetFileNameWithoutExtension(file), command = file, source = "Pasta Inicializar", enabled = true, canToggle = false });
        }
        return items;
    }

    public object ToggleStartup(string id, bool enabled)
    {
        var parts = id.Split('|', 3);
        if (parts.Length != 3) throw new InvalidOperationException("Item de inicialização inválido.");
        RegistryKey root = parts[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        using var key = root.OpenSubKey(parts[1], true) ?? throw new InvalidOperationException("Chave de inicialização não encontrada.");
        using var backup = Registry.CurrentUser.CreateSubKey(StartupBackupKey);
        string backupName = parts[0] + "|" + parts[1] + "|" + parts[2];
        if (enabled)
        {
            if (backup?.GetValue(backupName) is string old) key.SetValue(parts[2], old);
            backup?.DeleteValue(backupName, false);
        }
        else
        {
            object? value = key.GetValue(parts[2]);
            if (value is not null)
            {
                backup?.SetValue(backupName, Convert.ToString(value) ?? "");
                key.DeleteValue(parts[2], false);
            }
        }
        return GetStartupItems();
    }

    private static void ReadRunItems(RegistryKey root, string path, string source, List<object> items)
    {
        using var key = root.OpenSubKey(path);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            string hive = ReferenceEquals(root, Registry.LocalMachine) ? "HKLM" : "HKCU";
            items.Add(new { id = hive + "|" + path + "|" + name, name, command = Convert.ToString(key.GetValue(name)) ?? "", source, enabled = true, canToggle = true });
        }
    }

    public object GetOptionalServices()
    {
        var found = new Dictionary<string, (string DisplayName, string State, string StartMode)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode FROM Win32_Service");
            foreach (ManagementObject service in searcher.Get())
            {
                string name = Convert.ToString(service["Name"]) ?? "";
                if (SafeOptionalServices.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    found[name] = (Convert.ToString(service["DisplayName"]) ?? name, Convert.ToString(service["State"]) ?? "Unknown", Convert.ToString(service["StartMode"]) ?? "Manual");
                service.Dispose();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Não foi possível consultar os serviços do Windows: " + ex.Message, ex);
        }

        using var backup = Registry.CurrentUser.OpenSubKey(ServiceBackupKey);
        return SafeOptionalServices
            .Where(x => found.ContainsKey(x.Name))
            .Select(x =>
            {
                var info = found[x.Name];
                bool changedByApp = backup?.GetValue(x.Name) is string;
                return new
                {
                    name = x.Name,
                    displayName = info.DisplayName,
                    description = x.Description,
                    state = info.State,
                    startMode = info.StartMode,
                    disabledByApp = changedByApp,
                    canChange = true
                };
            }).ToArray();
    }

    public object SetOptionalService(string serviceName, bool disable)
    {
        var option = SafeOptionalServices.FirstOrDefault(x => x.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Este serviço não está na lista permitida para alteração.");
        string currentMode = GetServiceStartMode(option.Name);
        using var backup = Registry.CurrentUser.CreateSubKey(ServiceBackupKey);
        if (disable)
        {
            if (currentMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                return GetOptionalServices();

            backup?.SetValue(option.Name, currentMode, RegistryValueKind.String);
            RunSc(option.Name, "config", "start=", "disabled");
            if (GetServiceState(option.Name).Equals("Running", StringComparison.OrdinalIgnoreCase))
                RunSc(option.Name, "stop");
        }
        else
        {
            string restoreMode = backup?.GetValue(option.Name) as string ?? "Manual";
            string scMode = restoreMode.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "auto"
                : restoreMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? "disabled" : "demand";
            RunSc(option.Name, "config", "start=", scMode);
            backup?.DeleteValue(option.Name, false);
        }
        return GetOptionalServices();
    }

    private static string GetServiceStartMode(string serviceName)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT StartMode FROM Win32_Service WHERE Name='{serviceName.Replace("'", "''")}'");
        foreach (ManagementObject item in searcher.Get())
        {
            string value = Convert.ToString(item["StartMode"]) ?? "Manual";
            item.Dispose();
            return value;
        }
        throw new InvalidOperationException("Serviço não encontrado: " + serviceName);
    }

    private static string GetServiceState(string serviceName)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT State FROM Win32_Service WHERE Name='{serviceName.Replace("'", "''")}'");
        foreach (ManagementObject item in searcher.Get())
        {
            string value = Convert.ToString(item["State"]) ?? "Unknown";
            item.Dispose();
            return value;
        }
        return "Unknown";
    }

    private static void RunSc(string serviceName, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(arguments[0]);
        start.ArgumentList.Add(serviceName);
        foreach (string argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o utilitário de serviços do Windows.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Falha ao alterar {serviceName}. Execute o MASTER HUB como administrador. {output} {error}".Trim());
    }

    public object GetCleanupPreview()
    {
        var folders = GetCleanupTargets().Select(target =>
        {
            var scan = target.IsRecycleBin ? GetRecycleBinInfo() : ScanFolder(target.Path);
            return new { id = target.Id, name = target.Name, path = target.Path, count = scan.Count, bytes = scan.Bytes, sizeMb = Math.Round(scan.Bytes / 1024d / 1024d, 2), isRecycleBin = target.IsRecycleBin };
        }).ToArray();
        long totalBytes = folders.Sum(x => x.bytes);
        int totalFiles = folders.Sum(x => x.count);
        return new { count = totalFiles, sizeMb = Math.Round(totalBytes / 1024d / 1024d, 2), totalBytes, locations = folders };
    }

    public object CleanTemp()
    {
        var results = new List<object>();
        int totalDeleted = 0;
        long totalBytes = 0;
        foreach (var target in GetCleanupTargets())
        {
            int deleted = 0;
            long bytes = 0;
            string? error = null;
            if (target.IsRecycleBin)
            {
                try
                {
                    var info = GetRecycleBinInfo();
                    if (info.Count > 0)
                    {
                        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, RecycleBinFlags);
                        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                        deleted = info.Count;
                        bytes = info.Bytes;
                    }
                }
                catch (Exception ex) { error = ex.Message; }
            }
            else
            {
                foreach (string file in EnumerateFilesSafe(target.Path))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        long length = info.Length;
                        info.Delete();
                        deleted++;
                        bytes += length;
                    }
                    catch { }
                }
                try { DeleteEmptyDirectories(target.Path); } catch { }
            }
            totalDeleted += deleted;
            totalBytes += bytes;
            results.Add(new { id = target.Id, name = target.Name, deleted, bytes, freedMb = Math.Round(bytes / 1024d / 1024d, 2), error });
        }
        return new { deleted = totalDeleted, freedMb = Math.Round(totalBytes / 1024d / 1024d, 2), folders = results };
    }

    private static List<CleanupTarget> GetCleanupTargets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new List<CleanupTarget>
        {
            new("user-temp", "Temporários do usuário", Path.GetTempPath()),
            new("windows-temp", "Temporários do Windows", Path.Combine(windows, "Temp")),
            new("directx-cache", "Cache de shaders DirectX", Path.Combine(local, "D3DSCache")),
            new("inet-cache", "Cache temporário da Internet", Path.Combine(local, "Microsoft", "Windows", "INetCache")),
            new("wer-queue", "Relatórios de erro pendentes", Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue")),
            new("wer-archive", "Relatórios de erro arquivados", Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive")),
            new("crash-dumps", "Relatórios de falhas de aplicativos", Path.Combine(local, "CrashDumps")),
            new("delivery-cache", "Cache de Otimização de Entrega", Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")),
            new("recycle-bin", "Lixeira (todas as unidades)", "", true)
        };
        AddBrowserCacheTargets(candidates, Path.Combine(local, "Google", "Chrome", "User Data"), "chrome", "Google Chrome");
        AddBrowserCacheTargets(candidates, Path.Combine(local, "Microsoft", "Edge", "User Data"), "edge", "Microsoft Edge");
        string firefoxProfiles = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxProfiles))
        {
            foreach (string profile in Directory.GetDirectories(firefoxProfiles))
                candidates.Add(new CleanupTarget("firefox-" + Path.GetFileName(profile), "Cache do Firefox • " + Path.GetFileName(profile), Path.Combine(profile, "cache2", "entries")));
        }
        var result = new List<CleanupTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in candidates)
        {
            if (item.IsRecycleBin || (Directory.Exists(item.Path) && seen.Add(Path.GetFullPath(item.Path))))
                result.Add(item);
        }
        return result;
    }

    private static void AddBrowserCacheTargets(List<CleanupTarget> candidates, string userData, string idPrefix, string browserName)
    {
        if (!Directory.Exists(userData)) return;
        foreach (string profile in Directory.GetDirectories(userData))
        {
            string profileName = Path.GetFileName(profile);
            if (profileName.Equals("Crashpad", StringComparison.OrdinalIgnoreCase) ||
                profileName.Equals("ShaderCache", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string cacheName in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                string path = Path.Combine(profile, cacheName);
                if (Directory.Exists(path))
                    candidates.Add(new CleanupTarget(idPrefix + "-" + profileName + "-" + cacheName.Replace(" ", "").ToLowerInvariant(),
                        "Cache do " + browserName + " • " + profileName + " • " + cacheName, path));
            }
        }
    }

    private static (int Count, long Bytes) ScanFolder(string path)
    {
        int count = 0;
        long bytes = 0;
        foreach (string file in EnumerateFilesSafe(path))
        {
            try { var info = new FileInfo(file); bytes += info.Length; count++; } catch { }
        }
        return (count, bytes);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(current); } catch { files = Array.Empty<string>(); }
            foreach (string file in files) yield return file;
            string[] dirs;
            try { dirs = Directory.GetDirectories(current); } catch { dirs = Array.Empty<string>(); }
            foreach (string dir in dirs)
            {
                try { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) pending.Push(dir); } catch { }
            }
        }
    }

    private static void DeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
    }

    private static (int Count, long Bytes) GetRecycleBinInfo()
    {
        long count = 0, bytes = 0;
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady))
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            try
            {
                if (SHQueryRecycleBin(drive.RootDirectory.FullName, ref info) == 0)
                {
                    count += info.i64NumItems;
                    bytes += info.i64Size;
                }
            }
            catch { }
        }
        return ((int)Math.Min(count, int.MaxValue), bytes);
    }
}
