using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MasterRgb.Services;

public sealed class BoostGameService
{
    private const string BackupKey = @"Software\MasterHub\BoostGame\Backup";

    public object GetDashboard()
    {
        string cpu = ReadWmi("Win32_Processor", "Name");
        string gpu = ReadWmi("Win32_VideoController", "Name");
        string os = ReadWmi("Win32_OperatingSystem", "Caption");
        ulong totalKb = ReadUlongWmi("Win32_OperatingSystem", "TotalVisibleMemorySize");
        ulong freeKb = ReadUlongWmi("Win32_OperatingSystem", "FreePhysicalMemory");
        double totalGb = totalKb / 1024d / 1024d;
        double usedGb = Math.Max(0, totalGb - freeKb / 1024d / 1024d);
        double ramPercent = totalKb > 0 ? usedGb / totalGb * 100 : 0;
        string systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string driveRoot = string.IsNullOrWhiteSpace(systemDrive) ? "C:\\\\" : Path.GetPathRoot(systemDrive) ?? "C:\\\\";
        var disks = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.Equals(driveRoot, StringComparison.OrdinalIgnoreCase))
            ?? DriveInfo.GetDrives().First(d => d.IsReady);
        return new {
            cpu, gpu, os, ramTotalGb = Math.Round(totalGb,1), ramUsedGb = Math.Round(usedGb,1), ramPercent = Math.Round(ramPercent,1),
            diskName = disks.Name, diskTotalGb = Math.Round(disks.TotalSize/1024d/1024/1024,1),
            diskFreeGb = Math.Round(disks.AvailableFreeSpace/1024d/1024/1024,1),
            uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\\:hh\\:mm")
        };
    }

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

    public object GetCleanupPreview()
    {
        var files = GetTempFiles().ToArray();
        long bytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        return new { count = files.Length, sizeMb = Math.Round(bytes / 1024d / 1024d, 1), locations = new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") } };
    }

    public object CleanTemp()
    {
        int deleted = 0; long bytes = 0;
        foreach (var file in GetTempFiles())
        {
            try { var info = new FileInfo(file); long length = info.Length; info.Delete(); deleted++; bytes += length; } catch { }
        }
        return new { deleted, freedMb = Math.Round(bytes / 1024d / 1024d, 1) };
    }

    public object GetPowerPlans()
    {
        var output = Run("powercfg.exe", "/list");
        var plans = System.Text.RegularExpressions.Regex.Matches(output, @"([0-9a-fA-F-]{36})\s*\(([^)]+)\)(\s*\*)?")
            .Select(m => new { id = m.Groups[1].Value, name = m.Groups[2].Value, active = m.Groups[3].Success }).ToArray();
        return new { plans, active = plans.FirstOrDefault(p => p.active)?.id };
    }

    public object SetPowerPlan(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidOperationException("Plano de energia inválido.");
        Run("powercfg.exe", "/setactive " + id);
        return GetPowerPlans();
    }

    public object GetGameSettings() => new {
        gameMode = ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1) != 0,
        captures = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1) != 0
    };

    public object SetGameSetting(string setting, bool enabled)
    {
        string path, name;
        if (setting == "gameMode") { path = @"Software\Microsoft\GameBar"; name = "AutoGameModeEnabled"; }
        else if (setting == "captures") { path = @"Software\Microsoft\Windows\CurrentVersion\GameDVR"; name = "AppCaptureEnabled"; }
        else throw new InvalidOperationException("Ajuste desconhecido.");
        using var key = Registry.CurrentUser.CreateSubKey(path);
        BackupValue(path, name, key);
        key.SetValue(name, enabled ? 1 : 0, RegistryValueKind.DWord);
        return GetGameSettings();
    }

    public object ToggleStartup(string id, bool enabled)
    {
        var parts = id.Split('|', 3);
        if (parts.Length != 3) throw new InvalidOperationException("Item de inicialização inválido.");
        RegistryKey root = parts[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        using var key = root.OpenSubKey(parts[1], true) ?? throw new InvalidOperationException("Chave de inicialização não encontrada.");
        if (enabled)
        {
            using var backup = Registry.CurrentUser.CreateSubKey(BackupKey);
            string backupName = parts[0] + "|" + parts[1] + "|" + parts[2];
            if (backup.GetValue(backupName) is string old) key.SetValue(parts[2], old);
        }
        else
        {
            object? value = key.GetValue(parts[2]);
            if (value is null) return GetStartupItems();
            using var backup = Registry.CurrentUser.CreateSubKey(BackupKey);
            backup.SetValue(parts[0] + "|" + parts[1] + "|" + parts[2], Convert.ToString(value) ?? "");
            key.DeleteValue(parts[2], false);
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

    private static IEnumerable<string> GetTempFiles()
    {
        foreach (var folder in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToArray(); } catch { continue; }
            foreach (var file in files) yield return file;
        }
    }

    private static string ReadWmi(string cls, string property)
    {
        try { using var q = new ManagementObjectSearcher("SELECT " + property + " FROM " + cls); return q.Get().Cast<ManagementObject>().Select(o => Convert.ToString(o[property])).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "Não disponível"; }
        catch { return "Não disponível"; }
    }
    private static ulong ReadUlongWmi(string cls, string property)
    {
        try { using var q = new ManagementObjectSearcher("SELECT " + property + " FROM " + cls); return Convert.ToUInt64(q.Get().Cast<ManagementObject>().First()[property]); } catch { return 0; }
    }
    private static int ReadDword(RegistryKey root, string path, string name, int fallback)
    {
        try { using var key = root.OpenSubKey(path); return Convert.ToInt32(key?.GetValue(name, fallback) ?? fallback); } catch { return fallback; }
    }
    private static void BackupValue(string path, string name, RegistryKey key)
    {
        using var backup = Registry.CurrentUser.CreateSubKey(BackupKey);
        string backupName = path + "|" + name;
        if (backup.GetValue(backupName) is not null) return;
        object? old = key.GetValue(name);
        if (old is null) backup.SetValue(backupName, "__MISSING__");
        else backup.SetValue(backupName, Convert.ToString(old) ?? "");
    }
    private static string Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        string output = p.StandardOutput.ReadToEnd(); string error = p.StandardError.ReadToEnd(); p.WaitForExit(10000);
        if (p.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Falha ao executar comando do Windows." : error.Trim());
        return output;
    }
}
