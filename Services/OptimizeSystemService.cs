using System.IO;
using Microsoft.Win32;

namespace MasterRgb.Services;

public sealed class OptimizeSystemService
{
    private const string BackupKey = @"Software\MasterHub\OptimizeSystem\StartupBackup";

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
}