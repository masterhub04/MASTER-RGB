namespace MasterRgb.Services.DirectHardware;

/// <summary>
/// Ponto único de entrada para drivers nativos. A lista começa vazia intencionalmente:
/// nenhum hardware será declarado compatível até existir um driver real e testado.
/// </summary>
public sealed class DirectHardwareEngine
{
    private readonly IReadOnlyList<IRgbDeviceDriver> _drivers;

    public DirectHardwareEngine(IEnumerable<IRgbDeviceDriver>? drivers = null)
    {
        _drivers = (drivers ?? Array.Empty<IRgbDeviceDriver>()).ToArray();
    }

    public IReadOnlyList<string> AvailableDrivers => _drivers.Select(x => x.DisplayName).ToArray();

    public async Task<IReadOnlyList<DirectRgbDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DirectRgbDevice>();
        foreach (var driver in _drivers)
            results.AddRange(await driver.DiscoverAsync(cancellationToken));
        return results;
    }
}
