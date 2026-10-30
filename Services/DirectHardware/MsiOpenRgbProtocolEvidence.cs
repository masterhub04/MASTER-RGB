namespace MasterRgb.Services.DirectHardware;

/// <summary>
/// Correlação documental, somente leitura, entre o HID MSI observado e o detector
/// específico existente no código-fonte do OpenRGB. Não implementa nem envia protocolo.
/// </summary>
public sealed class MsiOpenRgbProtocolEvidence
{
    private const string SourceUrl = "https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/4512fb7f346e69fa9c03a0c4f263db8929bfd1ff/Controllers/MSIMysticLightController/MSIMysticLightControllerDetect.cpp";

    public object Build(IReadOnlyList<PnpDeviceRecord> pnpDevices, IReadOnlyList<OpenRgbDevice> openRgbDevices)
    {
        var hidMatches = pnpDevices
            .Where(x => x.Source is "HID" or "USB")
            .Where(x => x.HardwareIds.Contains("VID_1462&PID_7C56", StringComparison.OrdinalIgnoreCase)
                     || x.CompatibleIds.Contains("VID_1462&PID_7C56", StringComparison.OrdinalIgnoreCase))
            .Select(x => new
            {
                x.Source,
                x.FriendlyName,
                x.Service,
                x.HardwareIds,
                x.CompatibleIds,
                x.RedactedInstanceId
            }).ToArray();

        var boardController = openRgbDevices.FirstOrDefault(x =>
            x.Name.Contains("MSI MPG B550 GAMING PLUS", StringComparison.OrdinalIgnoreCase));

        return new
        {
            ReadOnly = true,
            BoardIdentity = "MSI MPG B550 Gaming Plus (MS-7C56)",
            UsbHidIdentity = new { VendorId = "0x1462", ProductId = "0x7C56" },
            WindowsPnpMatches = hidMatches,
            ExactOpenRgbDetectorMatch = new
            {
                FoundInOpenRgbSource = true,
                Registration = "MSI Mystic Light MS_7C56",
                VendorId = "0x1462",
                ProductId = "0x7C56",
                ControllerImplementation = "DetectMSIMysticLight185Controllers",
                SourceUrl
            },
            OpenRgbControllerObserved = boardController is not null,
            OpenRgbControllerName = boardController?.Name ?? string.Empty,
            OpenRgbControllerDescription = boardController?.Description ?? string.Empty,
            OpenRgbZones = boardController?.Zones.Select((z, i) => new { Index = i, z.Name, z.LedCount }).ToArray()
                ?? Array.Empty<object>(),
            ProtocolEvidence = new
            {
                ControllerFamily = "MSI Mystic Light 185-byte",
                FeatureReportId = "0x52",
                PerLedPacketReportId = "0x53",
                PacketHeader = "0x25 0x06",
                ExpectedProtocolPacketLengthBytes = 185,
                HidSharpMaximumFeatureReportLengthObservedIsNotProtocolPacketLength = true,
                Note = "O máximo de 725 bytes retornado pelo descritor HID não significa que o pacote RGB tenha 725 bytes."
            },
            Safety = new
            {
                HardwareAccessed = false,
                HidDeviceOpened = false,
                FeatureReportsRead = false,
                FeatureReportsWritten = false,
                OutputReportsWritten = false,
                SmbusOrI2cTransactionsSent = false,
                RgbStateChanged = false
            },
            RiskNotice = "O código-fonte do OpenRGB contém aviso histórico de risco de brick em controladores Mystic Light não testados. A correspondência do detector identifica a família/protocolo, mas não autoriza implementar escrita sem validação adicional.",
            Conclusion = "Correspondência exata VID/PID e registro do detector OpenRGB confirmam que o HID MSI observado pertence ao caminho de detecção Mystic Light 185-byte usado para MS-7C56. A próxima etapa é revisar o código do controlador e as condições de segurança; ainda não implementar escrita."
        };
    }
}
