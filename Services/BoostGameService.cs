using System.IO;
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
