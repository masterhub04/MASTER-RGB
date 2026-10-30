using HidSharp;

namespace MasterRgb.Services.DirectHardware.Drivers;

/// <summary>
/// Experimental HID transport for Logitech G203 Prodigy (046D:C084).
/// The two vendor-defined HID collections are selected from their device paths.
/// </summary>
public sealed class LogitechG203HidDriver
{
    private const int VendorId = 0x046D;
    private const int ProductId = 0xC084;

    /// <summary>
    /// Detecta o G203 por VID/PID e pela coleção HID que aceita o relatório longo.
    /// Apenas enumera descritores: não abre o dispositivo nem envia comandos.
    /// </summary>
    public G203DetectionResult DetectConnectedDevice()
    {
        try
        {
            var matches = DeviceList.Local.GetHidDevices(VendorId, ProductId)
                .Where(d => d.VendorID == VendorId && d.ProductID == ProductId)
                .ToList();
            if (matches.Count == 0)
                return new(false, "Logitech G203 Prodigy não conectado (046D:C084). ");

            var longReportCollection = matches.FirstOrDefault(d =>
                (d.DevicePath ?? string.Empty).Contains("COL05", StringComparison.OrdinalIgnoreCase)
                && d.GetMaxOutputReportLength() >= 20);
            if (longReportCollection is null)
                return new(false, "Dispositivo Logitech 046D:C084 encontrado, mas a coleção de relatório RGB de 20 bytes (COL05) não foi confirmada.");

            return new(true, $"VID_046D&PID_C084 • COL05 • Output={longReportCollection.GetMaxOutputReportLength()} bytes • pronto para controle nativo");
        }
        catch (Exception ex)
        {
            return new(false, "Falha na enumeração HID: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public G203ApplyResult ApplyStaticColor(byte red, byte green, byte blue)
    {
        try
        {
            var devices = DeviceList.Local.GetHidDevices(VendorId, ProductId)
                .Where(d => d.VendorID == VendorId && d.ProductID == ProductId)
                .ToList();

            if (devices.Count == 0)
                return new(false, "Logitech G203 Prodigy (046D:C084) não encontrado na enumeração HID/HidSharp.");

            static string PathOf(HidDevice device) => device.DevicePath ?? string.Empty;
            var collections = devices
                .Where(d => PathOf(d).Contains("COL04", StringComparison.OrdinalIgnoreCase)
                         || PathOf(d).Contains("COL05", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => PathOf(d).Contains("COL04", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            if (collections.Count == 0)
            {
                string found = string.Join(Environment.NewLine, devices.Select(PathOf));
                return new(false, $"Nenhuma coleção COL04/COL05 encontrada. Caminhos HID: {found}");
            }

            // O diagnóstico real do usuário mostrou: COL04 Output=7 e COL05 Output=20,
            // enquanto ambas anunciam Feature=0. Portanto o pacote de 20 bytes deve
            // ser enviado como OUTPUT REPORT pela coleção COL05 (Usage 2).
            byte[] report = new byte[20];
            report[0] = 0x11; // HID++ long report ID
            report[1] = 0xFF; // dispositivo conectado diretamente
            report[2] = 0x0E; // software ID
            report[3] = 0x3A; // função Direct do G203 Prodigy
            report[4] = 0x00;
            report[5] = 0x01; // modo estático
            report[6] = red;
            report[7] = green;
            report[8] = blue;
            report[9] = 0x02;

            var diagnostics = new List<string>();
            foreach (var device in collections)
            {
                string collectionName = PathOf(device).Contains("COL04", StringComparison.OrdinalIgnoreCase) ? "COL04" : "COL05";
                int featureLength;
                int outputLength;
                try
                {
                    featureLength = device.GetMaxFeatureReportLength();
                    outputLength = device.GetMaxOutputReportLength();
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"{collectionName}: não foi possível ler os tamanhos dos relatórios: {ex.Message}");
                    continue;
                }

                // O protocolo documentado do G203 usa Output Report de 20 bytes.
                // COL04 anuncia apenas 7 bytes; COL05 anuncia os 20 necessários.
                if (outputLength < report.Length)
                {
                    diagnostics.Add($"{collectionName}: Feature={featureLength} bytes, Output={outputLength} bytes; ignorada porque Output não comporta 20 bytes.");
                    continue;
                }
                if (!collectionName.Equals("COL05", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add($"{collectionName}: Output comporta {outputLength} bytes, mas a coleção esperada para o relatório longo é COL05/Usage 2; ignorada.");
                    continue;
                }

                if (!device.TryOpen(out HidStream? stream) || stream is null)
                {
                    diagnostics.Add($"{collectionName}: não foi possível abrir a coleção HID.");
                    continue;
                }

                using (stream)
                {
                    try
                    {
                        // Stream.Write envia um OUTPUT REPORT (WriteFile/HID output),
                        // diferente de SetFeature, que envia um FEATURE REPORT.
                        stream.Write(report, 0, report.Length);
                        stream.Flush();
                        return new(true, $"Output Report HID++ 0x3A de 20 bytes enviado por {collectionName}. Feature={featureLength}, Output={outputLength}. RGB({red}, {green}, {blue}). O envio aceito não confirma visualmente a mudança no mouse.");
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"{collectionName}: Output Report (20 bytes) falhou: {ex.GetType().Name}: {ex.Message}; Feature={featureLength}, Output={outputLength}.");
                    }
                }
            }

            return new(false, "Nenhuma coleção aceitou o Output Report estático 0x3A. Diagnóstico: " + string.Join(" | ", diagnostics));
        }
        catch (Exception ex)
        {
            return new(false, "Falha no transporte HID do G203: " + ex.GetType().Name + ": " + ex.Message + "\nFeche Logitech G HUB/OpenRGB e tente novamente. Se persistir, envie este diagnóstico.");
        }
    }
}

public sealed record G203ApplyResult(bool Success, string Message);

public sealed record G203DetectionResult(bool Found, string Details);
