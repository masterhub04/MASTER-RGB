using System.IO;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace MasterRgb.Services;

public sealed record OpenRgbZone(string Name, int LedCount);
public sealed record OpenRgbMode(string Name, int Value, uint Flags, uint SpeedMin, uint SpeedMax, uint BrightnessMin, uint BrightnessMax, uint ColorsMin, uint ColorsMax, uint Speed, uint Brightness, uint Direction, uint ColorMode, IReadOnlyList<uint> Colors);
public sealed record OpenRgbDevice(
    int Index, string Name, string Vendor, string Description, string Version,
    int ZoneCount, int LedCount, IReadOnlyList<OpenRgbZone> Zones, IReadOnlyList<OpenRgbMode> Modes, int ActiveMode, int DeviceType, uint ProtocolVersion, string RawDataHex);

public sealed class OpenRgbSdkClient
{
    private const int Port = 6742;
    private const uint RequestControllerCount = 0;
    private const uint RequestControllerData = 1;
    private const uint RequestProtocolVersion = 40;
    private const uint SetClientName = 50;
    private const uint DeviceListUpdated = 100;
    private const uint ResizeZone = 1000;
    private const uint UpdateZoneLeds = 1051;
    private const uint SetCustomMode = 1100;
    private const uint UpdateMode = 1101;
    private const uint ClientProtocolVersion = 5;

    public async Task<IReadOnlyList<OpenRgbDevice>> ReadDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
        using NetworkStream stream = client.GetStream();

        uint protocol = 0;
        await SendPacketAsync(stream, 0, RequestProtocolVersion, UInt32(ClientProtocolVersion), timeout.Token);
        // DEVICE_LIST_UPDATED (100) may arrive before the protocol-version reply (40).
        // Keep reading until the actual reply arrives; otherwise packet 40 remains queued
        // and is mistaken for the controller-count response (packet 0).
        using (var versionTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            versionTimeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                while (!versionTimeout.IsCancellationRequested)
                {
                    var versionPacket = await ReadPacketAsync(stream, versionTimeout.Token);
                    if (versionPacket.Id == RequestProtocolVersion)
                    {
                        if (versionPacket.Data.Length >= 4)
                            protocol = Math.Min(ReadUInt32(versionPacket.Data, 0), ClientProtocolVersion);
                        break;
                    }
                    if (versionPacket.Id != DeviceListUpdated)
                        throw new InvalidDataException($"Resposta inesperada durante a negociação do protocolo (pacote {versionPacket.Id}).");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Protocol 0 servers do not reply to packet 40; use the legacy format.
                protocol = 0;
            }
        }

        await SendPacketAsync(stream, 0, SetClientName, Encoding.UTF8.GetBytes("MASTER RGB\0"), timeout.Token);
        await SendPacketAsync(stream, 0, RequestControllerCount, Array.Empty<byte>(), timeout.Token);
        var countPacket = await ReadExpectedAsync(stream, RequestControllerCount, timeout.Token);
        if (countPacket.Data.Length < 4) throw new InvalidDataException("Resposta de quantidade de dispositivos inválida.");
        uint count = ReadUInt32(countPacket.Data, 0);
        if (count > 256) throw new InvalidDataException($"O OpenRGB informou uma quantidade inesperada de dispositivos: {count}.");

        var devices = new List<OpenRgbDevice>((int)count);
        for (uint i = 0; i < count; i++)
        {
            byte[] requestData = protocol >= 1 ? UInt32(protocol) : Array.Empty<byte>();
            await SendPacketAsync(stream, i, RequestControllerData, requestData, timeout.Token);
            var dataPacket = await ReadExpectedAsync(stream, RequestControllerData, timeout.Token);
            devices.Add(ParseController(i, dataPacket.Data, protocol));
        }
        return devices;
    }


    public async Task ResizeZoneAsync(int deviceIndex, int zoneIndex, int newLedCount, CancellationToken cancellationToken = default)
    {
        if (deviceIndex < 0 || zoneIndex < 0) throw new ArgumentOutOfRangeException("Índice de dispositivo ou zona inválido.");
        if (newLedCount < 1 || newLedCount > ushort.MaxValue) throw new InvalidOperationException("A quantidade de LEDs deve ficar entre 1 e 65535.");

        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
        using NetworkStream stream = client.GetStream();
        await SendPacketAsync(stream, 0, SetClientName, Encoding.UTF8.GetBytes("MASTER RGB\0"), timeout.Token);
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)zoneIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)newLedCount);
        await SendPacketAsync(stream, (uint)deviceIndex, ResizeZone, payload, timeout.Token);
        await Task.Delay(100, timeout.Token);
    }

    public async Task ApplyDeviceZonesColorAsync(int deviceIndex, IReadOnlyList<(int ZoneIndex, int LedCount, byte Red, byte Green, byte Blue)> zones, CancellationToken cancellationToken = default)
    {
        if (deviceIndex < 0) throw new ArgumentOutOfRangeException(nameof(deviceIndex));
        if (zones.Count == 0) return;
        if (zones.Any(z => z.ZoneIndex < 0 || z.LedCount < 1 || z.LedCount > ushort.MaxValue))
            throw new InvalidOperationException("Uma das zonas não possui uma quantidade válida de LEDs.");

        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
        using NetworkStream stream = client.GetStream();
        await SendPacketAsync(stream, 0, SetClientName, Encoding.UTF8.GetBytes("MASTER RGB\0"), timeout.Token);
        await SendPacketAsync(stream, (uint)deviceIndex, SetCustomMode, Array.Empty<byte>(), timeout.Token);

        // Uma conexão e uma única troca para Custom por dispositivo.
        await Task.Delay(500, timeout.Token);

        foreach (var zone in zones)
        {
            uint packedColor = (uint)zone.Red | ((uint)zone.Green << 8) | ((uint)zone.Blue << 16);
            byte[] payload = new byte[10 + (zone.LedCount * 4)];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)zone.ZoneIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), (ushort)zone.LedCount);
            for (int i = 0; i < zone.LedCount; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(10 + (i * 4), 4), packedColor);
            await SendPacketAsync(stream, (uint)deviceIndex, UpdateZoneLeds, payload, timeout.Token);
        }

        await Task.Delay(150, timeout.Token);
    }

    public Task ApplyDeviceZonesColorAsync(int deviceIndex, IReadOnlyList<(int ZoneIndex, int LedCount)> zones, byte red, byte green, byte blue, CancellationToken cancellationToken = default)
    {
        var coloredZones = zones
            .Select(zone => (zone.ZoneIndex, zone.LedCount, Red: red, Green: green, Blue: blue))
            .ToArray();
        return ApplyDeviceZonesColorAsync(deviceIndex, coloredZones, cancellationToken);
    }

    public async Task PrepareZoneForEffectsAsync(int deviceIndex, CancellationToken cancellationToken = default)
    {
        if (deviceIndex < 0) throw new ArgumentOutOfRangeException(nameof(deviceIndex));
        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
        using NetworkStream stream = client.GetStream();
        await SendPacketAsync(stream, 0, SetClientName, Encoding.UTF8.GetBytes("MASTER RGB\0"), timeout.Token);
        await SendPacketAsync(stream, (uint)deviceIndex, SetCustomMode, Array.Empty<byte>(), timeout.Token);
        // Aguarda o controlador concluir a troca para o modo de controle direto.
        await Task.Delay(500, timeout.Token);
    }

    public async Task StreamZoneFramesAsync(
        int deviceIndex,
        int zoneIndex,
        Func<int, IReadOnlyList<(byte Red, byte Green, byte Blue)>> frameFactory,
        int frameDelayMs,
        CancellationToken cancellationToken = default)
    {
        if (deviceIndex < 0 || zoneIndex < 0) throw new ArgumentOutOfRangeException("Índice de dispositivo ou zona inválido.");
        ArgumentNullException.ThrowIfNull(frameFactory);
        if (frameDelayMs < 16 || frameDelayMs > 5000) throw new ArgumentOutOfRangeException(nameof(frameDelayMs));

        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
        using NetworkStream stream = client.GetStream();

        await SendPacketAsync(stream, 0, SetClientName, Encoding.UTF8.GetBytes("MASTER RGB\0"), timeout.Token);
        await SendPacketAsync(stream, (uint)deviceIndex, SetCustomMode, Array.Empty<byte>(), timeout.Token);
        await Task.Delay(500, timeout.Token);

        int frame = 0;
        while (!timeout.IsCancellationRequested)
        {
            var colors = frameFactory(frame);
            if (colors.Count < 1 || colors.Count > ushort.MaxValue)
                throw new InvalidOperationException("Quantidade de LEDs inválida para o efeito.");

            byte[] payload = new byte[10 + (colors.Count * 4)];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)zoneIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), (ushort)colors.Count);
            for (int i = 0; i < colors.Count; i++)
            {
                var color = colors[i];
                uint packed = (uint)color.Red | ((uint)color.Green << 8) | ((uint)color.Blue << 16);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(10 + (i * 4), 4), packed);
            }

            await SendPacketAsync(stream, (uint)deviceIndex, UpdateZoneLeds, payload, timeout.Token);
            frame++;
            await Task.Delay(frameDelayMs, timeout.Token);
        }
    }

    public async Task ApplyNativeModeAsync(int deviceIndex, int modeIndex, OpenRgbMode mode, int brightnessPercent, CancellationToken cancellationToken = default)
    {
        if (deviceIndex < 0 || modeIndex < 0) throw new ArgumentOutOfRangeException("Índice do dispositivo ou modo inválido.");
        ArgumentNullException.ThrowIfNull(mode);
        if (mode.Name.Length == 0) throw new InvalidOperationException("O modo selecionado não possui nome válido.");

        uint brightness = (uint)Math.Clamp(brightnessPercent, 0, 100);
        var modeData = new List<byte>();
        byte[] name = Encoding.UTF8.GetBytes(mode.Name + "\0");
        if (name.Length > ushort.MaxValue) throw new InvalidOperationException("Nome do modo excede o limite do protocolo.");
        AddUInt16(modeData, (ushort)name.Length);
        modeData.AddRange(name);
        AddInt32(modeData, mode.Value);
        AddUInt32(modeData, mode.Flags);
        AddUInt32(modeData, mode.SpeedMin);
        AddUInt32(modeData, mode.SpeedMax);
        if (ClientProtocolVersion >= 3)
        {
            AddUInt32(modeData, mode.BrightnessMin);
            AddUInt32(modeData, mode.BrightnessMax);
        }
        AddUInt32(modeData, mode.ColorsMin);
        AddUInt32(modeData, mode.ColorsMax);
        AddUInt32(modeData, mode.Speed);
        if (ClientProtocolVersion >= 3)
        {
            uint selectedBrightness = (mode.Flags & (1u << 4)) != 0
                ? Math.Clamp(brightness, mode.BrightnessMin, mode.BrightnessMax)
                : mode.Brightness;
            AddUInt32(modeData, selectedBrightness);
        }
        AddUInt32(modeData, mode.Direction);
        AddUInt32(modeData, mode.ColorMode);
        if (mode.Colors.Count > ushort.MaxValue) throw new InvalidOperationException("Quantidade de cores do modo excede o limite do protocolo.");
        AddUInt16(modeData, (ushort)mode.Colors.Count);
        foreach (uint color in mode.Colors) AddUInt32(modeData, color);

        byte[] payload = new byte[8 + modeData.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), modeIndex);
        modeData.CopyTo(payload, 8);

        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
        using NetworkStream stream = client.GetStream();
        await SendPacketAsync(stream, 0, SetClientName, Encoding.UTF8.GetBytes("MASTER RGB\0"), timeout.Token);
        await SendPacketAsync(stream, (uint)deviceIndex, UpdateMode, payload, timeout.Token);
        await Task.Delay(250, timeout.Token);
    }

    private static void AddUInt16(List<byte> target, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        target.AddRange(bytes.ToArray());
    }

    private static void AddUInt32(List<byte> target, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        target.AddRange(bytes.ToArray());
    }

    private static void AddInt32(List<byte> target, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        target.AddRange(bytes.ToArray());
    }

    public async Task ApplyZoneFrameAsync(int deviceIndex, int zoneIndex, IReadOnlyList<(byte Red, byte Green, byte Blue)> colors, CancellationToken cancellationToken = default)
    {
        if (deviceIndex < 0 || zoneIndex < 0) throw new ArgumentOutOfRangeException("Índice de dispositivo ou zona inválido.");
        if (colors.Count < 1 || colors.Count > ushort.MaxValue) throw new InvalidOperationException("Quantidade de LEDs inválida para o efeito.");
        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
        using NetworkStream stream = client.GetStream();
        await SendPacketAsync(stream, 0, SetClientName, Encoding.UTF8.GetBytes("MASTER RGB\0"), timeout.Token);
        uint packedColor;
        byte[] payload = new byte[10 + (colors.Count * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)zoneIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), (ushort)colors.Count);
        for (int i = 0; i < colors.Count; i++)
        {
            var c = colors[i];
            packedColor = (uint)c.Red | ((uint)c.Green << 8) | ((uint)c.Blue << 16);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(10 + (i * 4), 4), packedColor);
        }
        await SendPacketAsync(stream, (uint)deviceIndex, UpdateZoneLeds, payload, timeout.Token);
    }

    public async Task ApplyZoneColorAsync(int deviceIndex, int zoneIndex, int ledCount, byte red, byte green, byte blue, CancellationToken cancellationToken = default)
    {
        if (deviceIndex < 0 || zoneIndex < 0) throw new ArgumentOutOfRangeException("Índice de dispositivo ou zona inválido.");
        if (ledCount < 1 || ledCount > ushort.MaxValue) throw new InvalidOperationException("A zona selecionada não possui LEDs controláveis.");

        using var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
        using NetworkStream stream = client.GetStream();
        await SendPacketAsync(stream, 0, SetClientName, Encoding.UTF8.GetBytes("MASTER RGB\0"), timeout.Token);
        await SendPacketAsync(stream, (uint)deviceIndex, SetCustomMode, Array.Empty<byte>(), timeout.Token);

        // Give the controller a moment to switch to direct/custom mode before
        // sending the LED buffer. Some SDK/device combinations need this gap.
        await Task.Delay(500, timeout.Token);

        // OpenRGB RGBColor uses ToRGBColor(r,g,b) = (b << 16) | (g << 8) | r.
        // Its least-significant byte is RED, not BLUE.
        uint packedColor = (uint)red | ((uint)green << 8) | ((uint)blue << 16);
        byte[] payload = new byte[10 + (ledCount * 4)];
        // OpenRGB SDK data_size includes the 4-byte data_size field itself.
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)zoneIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), (ushort)ledCount);
        for (int i = 0; i < ledCount; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(10 + (i * 4), 4), packedColor);
        await SendPacketAsync(stream, (uint)deviceIndex, UpdateZoneLeds, payload, timeout.Token);
        // OpenRGB SDK clients should leave a delay between repeated operations on the same device.
        // Keep this connection alive long enough for the device worker to apply the update.
        await Task.Delay(750, timeout.Token);
    }

    private static OpenRgbDevice ParseController(uint index, byte[] payload, uint protocol)
    {
        var reader = new PacketReader(payload);
        uint dataSize = reader.UInt32();
        // OpenRGB versions/builds have used both interpretations for this length:
        // bytes following the length field, or total payload bytes. Accept both.
        // The TCP packet itself has already been read completely from its header size.
        int remainingBytes = payload.Length - 4;
        if (dataSize != (uint)remainingBytes && dataSize != (uint)payload.Length)
        {
            throw new InvalidDataException(
                $"Tamanho do bloco do dispositivo incompatível (informado: {dataSize} bytes; recebido: {payload.Length} bytes). " +
                "Verifique a versão do OpenRGB/SDK. Nenhum LED foi alterado.");
        }
        int type = reader.Int32();
        string name = reader.String16();
        string vendor = protocol >= 1 ? reader.String16() : string.Empty;
        string description = reader.String16();
        string version = reader.String16();
        _ = reader.String16(); // serial
        _ = reader.String16(); // location

        ushort modeCount = reader.UInt16();
        int activeMode = reader.Int32();
        var modes = new List<OpenRgbMode>(modeCount);
        for (int i = 0; i < modeCount; i++)
        {
            string modeName = reader.String16();
            int modeValue = reader.Int32();
            uint modeFlags = reader.UInt32();
            uint speedMin = reader.UInt32();
            uint speedMax = reader.UInt32();
            uint brightnessMin = protocol >= 3 ? reader.UInt32() : 0;
            uint brightnessMax = protocol >= 3 ? reader.UInt32() : 0;
            uint colorsMin = reader.UInt32();
            uint colorsMax = reader.UInt32();
            uint speed = reader.UInt32();
            uint brightness = protocol >= 3 ? reader.UInt32() : 100;
            uint direction = reader.UInt32();
            uint colorMode = reader.UInt32();
            ushort modeColorCount = reader.UInt16();
            var modeColors = new List<uint>(modeColorCount);
            for (int colorIndex = 0; colorIndex < modeColorCount; colorIndex++)
                modeColors.Add(reader.UInt32());
            modes.Add(new OpenRgbMode(modeName, modeValue, modeFlags, speedMin, speedMax,
                brightnessMin, brightnessMax, colorsMin, colorsMax, speed, brightness,
                direction, colorMode, modeColors));
        }

        ushort zoneCount = reader.UInt16();
        var zones = new List<OpenRgbZone>(zoneCount);
        for (int i = 0; i < zoneCount; i++)
        {
            string zoneName = reader.String16();
            reader.Skip(4 * 3); // type, min, max
            uint zoneLedCount = reader.UInt32();
            zones.Add(new OpenRgbZone(zoneName, checked((int)zoneLedCount)));
            ushort matrixLength = reader.UInt16();
            reader.Skip(matrixLength);
            if (protocol >= 4)
            {
                ushort segments = reader.UInt16();
                for (int s = 0; s < segments; s++)
                {
                    _ = reader.String16();
                    reader.Skip(12);
                }
            }
            if (protocol >= 5) reader.Skip(4);
        }

        ushort ledCount = reader.UInt16();
        for (int i = 0; i < ledCount; i++)
        {
            _ = reader.String16();
            reader.Skip(4);
        }
        ushort colorCount = reader.UInt16();
        reader.Skip(colorCount * 4);
        if (protocol >= 5)
        {
            ushort alternateNames = reader.UInt16();
            for (int i = 0; i < alternateNames; i++) _ = reader.String16();
            reader.Skip(4); // controller flags
        }
        return new OpenRgbDevice((int)index, name, vendor, description, version, zoneCount, ledCount, zones, modes, activeMode, type, protocol, Convert.ToHexString(payload));
    }

    private static async Task<Packet> ReadExpectedAsync(NetworkStream stream, uint expectedId, CancellationToken token)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var packet = await ReadPacketAsync(stream, token);
            if (packet.Id == expectedId) return packet;
            if (packet.Id != DeviceListUpdated)
                throw new InvalidDataException($"Resposta inesperada do OpenRGB (pacote {packet.Id}, esperado {expectedId}).");
        }
        throw new InvalidDataException("O OpenRGB atualizou a lista repetidamente. Tente novamente.");
    }

    private static async Task SendPacketAsync(NetworkStream stream, uint device, uint id, byte[] data, CancellationToken token)
    {
        byte[] packet = new byte[16 + data.Length];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)data.Length);
        data.CopyTo(packet, 16);
        await stream.WriteAsync(packet, token);
    }

    private static async Task<Packet> ReadPacketAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[16];
        await ReadExactlyAsync(stream, header, token);
        if (Encoding.ASCII.GetString(header, 0, 4) != "ORGB") throw new InvalidDataException("Cabeçalho SDK do OpenRGB inválido.");
        uint id = ReadUInt32(header, 8);
        uint size = ReadUInt32(header, 12);
        if (size > 16 * 1024 * 1024) throw new InvalidDataException("Pacote do OpenRGB excede o limite de segurança.");
        byte[] data = new byte[checked((int)size)];
        if (size > 0) await ReadExactlyAsync(stream, data, token);
        return new Packet(id, data);
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0) throw new EndOfStreamException("O OpenRGB encerrou a conexão durante a leitura.");
            offset += read;
        }
    }

    private static byte[] UInt32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
    private static uint ReadUInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private sealed record Packet(uint Id, byte[] Data);

    private sealed class PacketReader(byte[] data)
    {
        private int _position;
        public ushort UInt16() { Ensure(2); ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_position, 2)); _position += 2; return v; }
        public uint UInt32() { Ensure(4); uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_position, 4)); _position += 4; return v; }
        public int Int32() => unchecked((int)UInt32());
        public string String16()
        {
            ushort length = UInt16(); Ensure(length);
            string value = Encoding.UTF8.GetString(data, _position, length).TrimEnd('\0');
            _position += length;
            return value;
        }
        public void Skip(int count) { if (count < 0) throw new InvalidDataException(); Ensure(count); _position += count; }
        private void Ensure(int count) { if (count < 0 || _position + count > data.Length) throw new InvalidDataException("Dados do dispositivo OpenRGB truncados ou incompatíveis."); }
    }
}
