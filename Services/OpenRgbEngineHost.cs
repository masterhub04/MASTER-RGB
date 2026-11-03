using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace MasterRgb.Services;

/// <summary>
/// Starts the bundled OpenRGB SDK server without showing its own interface.
/// The process is owned by MASTER RGB and is stopped when the app exits.
/// </summary>
public sealed class OpenRgbEngineHost : IDisposable
{
    private const int Port = 6742;
    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string executable = Path.Combine(AppContext.BaseDirectory, "OpenRGB", "OpenRGB.exe");

        // Reuse any existing SDK server on the standard port for compatibility.
        // Blocking an external OpenRGB process here prevents device detection.
        if (await IsSdkPortAvailableAsync(cancellationToken))
            return;

        if (!File.Exists(executable))
            throw new FileNotFoundException(
                "O mecanismo OpenRGB integrado não foi encontrado. Reinstale/atualize o MASTER HUB para incluir os componentes de compatibilidade.",
                executable);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            Arguments = "--server --server-host 127.0.0.1 --server-port 6742 --noautoconnect",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("O Windows não conseguiu iniciar o mecanismo OpenRGB integrado.");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
                throw new InvalidOperationException(
                    $"O mecanismo OpenRGB encerrou inesperadamente (código {_process.ExitCode}). Verifique permissões e drivers do Windows.");

            if (await IsSdkPortAvailableAsync(cancellationToken))
                return;

            await Task.Delay(250, cancellationToken);
        }

        StopOwnedProcess();
        throw new TimeoutException("O mecanismo OpenRGB iniciou, mas não abriu o servidor SDK na porta 6742 dentro do tempo esperado.");
    }

    private static bool IsExpectedOpenRgbProcess(string executable)
    {
        string expectedPath = Path.GetFullPath(executable);
        foreach (var process in Process.GetProcessesByName("OpenRGB"))
        {
            try
            {
                string? actualPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(actualPath) &&
                    string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // An inaccessible process is not proof that it owns the expected SDK server.
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    private static async Task<bool> IsSdkPortAvailableAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(350);
            await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    private void StopOwnedProcess()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit(2500);
        }
        catch { /* Best-effort cleanup during application shutdown. */ }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopOwnedProcess();
    }
}
