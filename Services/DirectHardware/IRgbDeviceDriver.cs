namespace MasterRgb.Services.DirectHardware;

/// <summary>
/// Contrato do motor independente. Cada fabricante/protocolo terá um driver próprio.
/// Um driver só deve ser registrado depois de implementar detecção e escrita reais,
/// validadas no hardware correspondente.
/// </summary>
public interface IRgbDeviceDriver
{
    string DriverId { get; }
    string DisplayName { get; }
    Task<IReadOnlyList<DirectRgbDevice>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task SetZoneColorAsync(string deviceId, int zoneIndex, IReadOnlyList<RgbColor> colors,
        CancellationToken cancellationToken = default);
}

public sealed record DirectRgbDevice(
    string Id,
    string Name,
    string Manufacturer,
    string DriverId,
    IReadOnlyList<DirectRgbZone> Zones);

public sealed record DirectRgbZone(int Index, string Name, int LedCount);
public readonly record struct RgbColor(byte Red, byte Green, byte Blue);
