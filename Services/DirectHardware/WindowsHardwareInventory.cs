using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;

namespace MasterRgb.Services.DirectHardware;

/// <summary>
/// Inventário nativo somente de leitura. Consulta metadados PnP no registro do Windows;
/// não abre interfaces USB/HID/PCI e não envia comandos RGB, I2C ou SMBus.
/// </summary>
public sealed class WindowsHardwareInventory
{
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    public IReadOnlyList<PnpDeviceRecord> Collect()
    {
        var records = new List<PnpDeviceRecord>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        // HID: inventaria coleções HID para localizar periféricos além do G203.
        CollectBranch(baseKey, "HID", "HID", records);
        // USB: limita aos fabricantes relevantes conhecidos para evitar listar todo hub/cabo.
        CollectBranch(baseKey, "USB", "USB", records);
        // PCI: inclui GPU, fabricante MSI e controladores de chipset/SMBus AMD/Intel.
        CollectBranch(baseKey, "PCI", "PCI", records);

        return records
            .GroupBy(x => x.Source + "|" + x.HardwareIds + "|" + x.FriendlyName + "|" + x.RedactedInstanceId,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.DeviceFamily)
            .ThenBy(x => x.FriendlyName)
            .ToArray();
    }

    private static void CollectBranch(RegistryKey baseKey, string branch, string source, List<PnpDeviceRecord> output)
    {
        using var root = baseKey.OpenSubKey($"{EnumRoot}\\{branch}", false);
        if (root is null) return;

        foreach (var vendorKeyName in SafeSubKeyNames(root))
        {
            using var vendorKey = SafeOpenSubKey(root, vendorKeyName);
            if (vendorKey is null) continue;
            foreach (var instanceName in SafeSubKeyNames(vendorKey))
            {
                using var instance = SafeOpenSubKey(vendorKey, instanceName);
                if (instance is null) continue;

                string hardwareIds = ReadMultiString(instance, "HardwareID");
                string compatibleIds = ReadMultiString(instance, "CompatibleIDs");
                string allIds = $"{hardwareIds} {compatibleIds} {vendorKeyName}";
                if (!IsRelevant(allIds, source)) continue;

                string friendly = ReadString(instance, "FriendlyName")
                    ?? ReadString(instance, "DeviceDesc")
                    ?? vendorKeyName;
                string manufacturer = ReadString(instance, "Mfg") ?? string.Empty;
                string service = ReadString(instance, "Service") ?? string.Empty;
                var classification = Classify(source, allIds, friendly, service);

                output.Add(new PnpDeviceRecord(
                    Source: source,
                    FriendlyName: friendly,
                    Manufacturer: manufacturer,
                    Service: service,
                    ClassGuid: ReadString(instance, "ClassGUID") ?? string.Empty,
                    Driver: ReadString(instance, "Driver") ?? string.Empty,
                    HardwareIds: hardwareIds,
                    CompatibleIds: compatibleIds,
                    RedactedInstanceId: RedactInstance(vendorKeyName, instanceName),
                    RegistryPath: $"HKLM\\{EnumRoot}\\{branch}\\{vendorKeyName}\\[INSTANCE]",
                    DeviceFamily: classification.Family,
                    RgbRelevance: classification.Relevance,
                    RecommendedNextStep: classification.NextStep));
            }
        }
    }

    private static bool IsRelevant(string ids, string source)
    {
        if (source == "HID") return true;
        if (source == "USB")
            return ids.Contains("VID_", StringComparison.OrdinalIgnoreCase);

        return ContainsAny(ids,
            "VEN_10DE", // NVIDIA GPU
            "VEN_1002", // AMD GPU
            "VEN_1462", // MSI PCI devices
            "VEN_1022", // AMD chipset / SMBus
            "VEN_8086", // Intel chipset / SMBus
            "VEN_1B4C");
    }

    private static (string Family, string Relevance, string NextStep) Classify(
        string source, string ids, string name, string service)
    {
        if (ContainsAny(ids, "VID_046D", "046D:C084") || name.Contains("Logitech", StringComparison.OrdinalIgnoreCase))
            return ("Logitech / periférico USB-HID", "Alta — periférico identificado; RGB depende do modelo",
                "Verificar VID/PID, coleções HID e protocolo específico. O G203 já possui controle nativo funcional; não alterar seu driver validado.");

        if (ContainsAny(ids, "VID_1462", "VEN_1462") || name.Contains("MSI", StringComparison.OrdinalIgnoreCase))
            return ("MSI / placa-mãe ou periférico", "Alta — fabricante MSI identificado",
                "Correlacionar com o controlador Mystic Light do OpenRGB. Identificação PnP não autoriza escrita SMBus.");

        if (ContainsAny(ids, "VEN_10DE", "VEN_1002") || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase))
            return ("GPU / controlador gráfico", "Alta — adaptador gráfico identificado",
                "Confirmar fabricante, subsistema e presença de iluminação. Investigar protocolo suportado antes de implementar escrita.");

        if (ContainsAny(ids, "VEN_1022", "VEN_8086") || service.Contains("SMBus", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SMBus", StringComparison.OrdinalIgnoreCase))
            return ("Chipset / controlador SMBus", "Média — infraestrutura que pode dar acesso a controladores RGB",
                "Identificar controlador SMBus e dependências de driver. Não enviar transações I2C/SMBus nesta etapa.");

        if (source == "HID")
            return ("Periférico HID", "A investigar — pode ser teclado, mouse, headset ou controle",
                "Usar VID/PID e coleções HID para identificar o modelo; consultar o código aberto do fabricante/OpenRGB.");

        return ("Dispositivo USB/PCI", "Baixa — relação com RGB ainda não confirmada",
            "Manter apenas como inventário. Não implementar comandos até confirmar modelo e protocolo.");
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

    private static string[] SafeSubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch (System.Security.SecurityException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    private static RegistryKey? SafeOpenSubKey(RegistryKey key, string name)
    {
        try { return key.OpenSubKey(name, false); }
        catch { return null; }
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        try { return key.GetValue(name)?.ToString()?.TrimEnd('\0'); }
        catch { return null; }
    }

    private static string ReadMultiString(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) is string[] values
                ? string.Join("; ", values)
                : ReadString(key, name) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string RedactInstance(string parent, string instance)
    {
        // Remove números de série/instâncias únicas; preserva MI_xx e COLxx quando existirem.
        var suffix = Regex.Match(instance, @"(MI_[0-9A-F]{2}(?:&COL[0-9A-F]{2})?)", RegexOptions.IgnoreCase);
        return suffix.Success ? $"{parent}\\[INSTANCE]\\{suffix.Value}" : $"{parent}\\[INSTANCE]";
    }
}

public sealed record PnpDeviceRecord(
    string Source,
    string FriendlyName,
    string Manufacturer,
    string Service,
    string ClassGuid,
    string Driver,
    string HardwareIds,
    string CompatibleIds,
    string RedactedInstanceId,
    string RegistryPath,
    string DeviceFamily,
    string RgbRelevance,
    string RecommendedNextStep);
