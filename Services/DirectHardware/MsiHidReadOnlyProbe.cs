using HidSharp;

namespace MasterRgb.Services.DirectHardware;

/// <summary>
/// Sondagem HID estritamente somente leitura para investigar controladores MSI.
/// Enumera descritores e comprimentos de relatórios; não abre dispositivos,
/// não envia Feature/Output Reports e não acessa SMBus/I2C.
/// </summary>
public sealed class MsiHidReadOnlyProbe
{
    private const int MsiVendorId = 0x1462;

    public MsiHidProbeResult Collect()
    {
        try
        {
            var devices = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID == MsiVendorId)
                .Select(d =>
                {
                    try
                    {
                        return new MsiHidDescriptor(
                            VendorId: $"0x{d.VendorID:X4}",
                            ProductId: $"0x{d.ProductID:X4}",
                            InputReportLength: d.GetMaxInputReportLength(),
                            OutputReportLength: d.GetMaxOutputReportLength(),
                            FeatureReportLength: d.GetMaxFeatureReportLength(),
                            PathHint: RedactPath(d.DevicePath ?? string.Empty),
                            ReadSucceeded: true,
                            Error: string.Empty);
                    }
                    catch (Exception ex)
                    {
                        return new MsiHidDescriptor(
                            VendorId: $"0x{d.VendorID:X4}",
                            ProductId: $"0x{d.ProductID:X4}",
                            InputReportLength: 0,
                            OutputReportLength: 0,
                            FeatureReportLength: 0,
                            PathHint: RedactPath(d.DevicePath ?? string.Empty),
                            ReadSucceeded: false,
                            Error: ex.GetType().Name + ": " + ex.Message);
                    }
                })
                .ToArray();

            return new MsiHidProbeResult(
                ReadOnly: true,
                VendorIdFilter: "0x1462 (MSI)",
                DeviceCount: devices.Length,
                Devices: devices,
                Conclusion: devices.Length == 0
                    ? "Nenhuma interface HID com VID 1462 foi enumerada. Isso não descarta controle Mystic Light por SMBus/I2C ou por outro VID."
                    : "Interfaces HID MSI enumeradas por descritores. Comprimentos de relatório, isoladamente, não identificam protocolo RGB nem autorizam escrita.",
                Safety: "Nenhum dispositivo foi aberto; nenhum relatório HID, comando RGB, transação SMBus ou I2C foi enviado.");
        }
        catch (Exception ex)
        {
            return new MsiHidProbeResult(
                ReadOnly: true,
                VendorIdFilter: "0x1462 (MSI)",
                DeviceCount: 0,
                Devices: Array.Empty<MsiHidDescriptor>(),
                Conclusion: "A enumeração HID não pôde ser concluída.",
                Safety: "Nenhum comando de hardware foi enviado.",
                Error: ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string RedactPath(string path)
    {
        // Preserva VID/PID/MI/COL, remove o número de série/instância do caminho.
        var match = System.Text.RegularExpressions.Regex.Match(
            path, @"(\\\\\?\\hid#[^#]+#)([^#]+)(#.*)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return string.Empty;
        string suffix = match.Groups[3].Success ? match.Groups[3].Value : string.Empty;
        return match.Groups[1].Value + "[INSTANCE]" + suffix;
    }
}

public sealed record MsiHidProbeResult(
    bool ReadOnly,
    string VendorIdFilter,
    int DeviceCount,
    IReadOnlyList<MsiHidDescriptor> Devices,
    string Conclusion,
    string Safety,
    string Error = "");

public sealed record MsiHidDescriptor(
    string VendorId,
    string ProductId,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength,
    string PathHint,
    bool ReadSucceeded,
    string Error);
