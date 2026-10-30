using System.Net.Sockets;

namespace MasterRgb.Services;

/// <summary>
/// V0.1 connectivity probe only. This does NOT enumerate or control LEDs yet.
/// It checks whether the OpenRGB SDK TCP server is listening locally.
/// </summary>
public sealed class OpenRgbProbe
{
    public async Task<bool> CanConnectAsync()
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync("127.0.0.1", 6742, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
