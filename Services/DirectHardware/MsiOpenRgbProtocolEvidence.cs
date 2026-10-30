using MasterRgb.Services;

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
            NativeControlReadiness = new
            {
                BoardSpecificWriteValidationComplete = false,
                NativeMsiWriteDriverEnabled = false,
                PerLedDirectModeAllowed = false,
                ZoneFeatureReportWriteAllowed = false,
                RequiredBeforeWrite = new[]
                {
                    "Revisar o fluxo completo de SetupModes/SetMode/UpdateLEDs do controlador 185-byte.",
                    "Confirmar a configuração exata do PID 0x7C56 e se DIRECT_MODE_PER_LED está habilitado.",
                    "Validar efeitos e flags por zona para JRGB1, JRAINBOW1, JRAINBOW2 e ONBOARD.",
                    "Definir recuperação segura e teste reversível antes de qualquer escrita."
                },
                Reason = "A família 185-byte não garante que todos os modos funcionem em todos os modelos. Uma regressão recente do OpenRGB documenta uma placa que fica sem iluminação quando Direct por LED é selecionado."
            },
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
            RiskNotice = "O código-fonte do OpenRGB contém aviso histórico de risco em controladores Mystic Light não testados. Além disso, uma correção recente desabilitou Direct por LED em uma placa 185-byte que não o suportava. Por isso, este build mantém todas as escritas MSI bloqueadas.",
            Conclusion = "A identificação da MS-7C56 e da família 185-byte está confirmada. O protocolo e as zonas estão documentados, mas a configuração de modos por placa ainda precisa ser auditada. O MASTER RGB não envia relatórios MSI nem altera o caminho atual do OpenRGB."
        };
    }
}
