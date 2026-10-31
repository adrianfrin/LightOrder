using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlCenter.Contracts;

namespace ControlCenter.OpenRgb;

// Layouts verified against OpenRGB NetworkProtocol.h, NetworkClient.cpp and
// RGBController/RGBController.cpp (Get*DescriptionData), SDK version 3.
internal sealed record RgbMode(string Name, uint[] Fields, uint[] Colors)
{
    public uint Flags => Fields[1];
}
internal sealed record RgbDevice(uint Index, uint Type, string Name, string Vendor,
    string Description, string Version, string Serial, string Location,
    uint ActiveMode, RgbMode[] Modes, uint[] Colors)
{
    public bool IsEneDram => Type == 1 && Description == "ENE SMBus Device";
    public bool SupportsStatic => StaticMode >= 0 && Colors.Length > 0;
    public int StaticMode => Array.FindIndex(Modes, m => m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase) && (m.Flags & 32) != 0) is var direct && direct >= 0
        ? direct : Array.FindIndex(Modes, m => m.Name.Equals("Static", StringComparison.OrdinalIgnoreCase) && (m.Flags & 32) != 0);
}

internal ref struct PacketReader(ReadOnlySpan<byte> bytes)
{
    private ReadOnlySpan<byte> remaining = bytes;
    public int Remaining => remaining.Length;
    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > remaining.Length) throw Invalid("Truncated SDK payload");
        var result = remaining[..count]; remaining = remaining[count..]; return result;
    }
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int Count(int limit)
    {
        var count = U16();
        if (count > limit) throw Invalid("SDK count exceeds supported limit");
        return count;
    }
    public string Text()
    {
        var size = Count(4096);
        var text = Take(size);
        if (size == 0 || text[^1] != 0 || text[..^1].Contains((byte)0)) throw Invalid("Invalid SDK string");
        try { return new UTF8Encoding(false, true).GetString(text[..^1]); }
        catch (DecoderFallbackException) { throw Invalid("Invalid UTF-8 SDK string"); }
    }
    public void End() { if (Remaining != 0) throw Invalid("Unexpected trailing SDK data"); }
    public static PluginException Invalid(string message) => new("ProtocolError", message);
}

internal static class RgbCodec
{
    public const int MaxPacket = 8 * 1024 * 1024;
    public static byte[] U32(uint value)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); return bytes;
    }
    public static RgbDevice Device(uint index, byte[] data)
    {
        var r = new PacketReader(data);
        if (r.U32() != data.Length) throw PacketReader.Invalid("Controller payload size mismatch");
        var type = r.U32();
        var name = r.Text(); var vendor = r.Text(); var description = r.Text();
        var version = r.Text(); var serial = r.Text(); var location = r.Text();
        var modes = new RgbMode[r.Count(256)]; var active = r.U32();
        if (modes.Length > 0 && active >= modes.Length) throw PacketReader.Invalid("Invalid active mode");
        for (var i = 0; i < modes.Length; i++)
        {
            var modeName = r.Text();
            // value, flags, speed min/max, brightness min/max, colors min/max,
            // speed, brightness, direction, color mode (all uint32 in v3).
            var fields = new uint[12];
            for (var j = 0; j < fields.Length; j++) fields[j] = r.U32();
            var colors = new uint[r.Count(16384)];
            for (var j = 0; j < colors.Length; j++) colors[j] = r.U32();
            modes[i] = new(modeName, fields, colors);
        }
        var zones = r.Count(1024);
        for (var i = 0; i < zones; i++)
        {
            _ = r.Text(); _ = r.U32(); _ = r.U32(); _ = r.U32(); _ = r.U32();
            var matrixBytes = r.U16();
            if (matrixBytes != 0)
            {
                var matrix = new PacketReader(r.Take(matrixBytes));
                var height = matrix.U32(); var width = matrix.U32();
                if ((ulong)height * width * 4 != (ulong)matrix.Remaining) throw PacketReader.Invalid("Invalid matrix dimensions");
                matrix.Take(matrix.Remaining);
            }
        }
        var leds = r.Count(16384);
        for (var i = 0; i < leds; i++) { _ = r.Text(); _ = r.U32(); }
        var deviceColors = new uint[r.Count(16384)];
        if (deviceColors.Length != leds) throw PacketReader.Invalid("LED/color count mismatch");
        for (var i = 0; i < deviceColors.Length; i++) deviceColors[i] = r.U32();
        r.End();
        return new(index, type, name, vendor, description, version, serial, location, active, modes, deviceColors);
    }
    public static byte[] Mode(int index, RgbMode mode)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write(0u); w.Write(index); WriteText(w, mode.Name);
        var fields = (uint[])mode.Fields.Clone();
        fields[11] = 1; // MODE_COLORS_PER_LED
        // Brightness is encoded by scaling RGB channels. Keep hardware brightness at max if supported.
        if ((fields[1] & 16) != 0) fields[9] = fields[5];
        foreach (var field in fields) w.Write(field);
        w.Write((ushort)mode.Colors.Length); foreach (var color in mode.Colors) w.Write(color);
        var data = stream.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length); return data;
    }
    public static byte[] Colors(int count, uint color)
    {
        var data = new byte[6 + count * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)count));
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6 + i * 4), color);
        return data;
    }
    internal static void WriteText(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text); writer.Write(checked((ushort)(bytes.Length + 1))); writer.Write(bytes); writer.Write((byte)0);
    }
    public static string Hex(uint color) => $"#{color & 255:X2}{(color >> 8) & 255:X2}{(color >> 16) & 255:X2}";
}

internal sealed class OpenRgbConnection : IAsyncDisposable
{
    private readonly TcpClient client = new(AddressFamily.InterNetwork);
    private NetworkStream stream = null!;
    public uint ServerVersion { get; private set; }
    public bool DeviceListChanged { get; private set; }
    public static async Task<OpenRgbConnection> ConnectAsync(int port, CancellationToken token)
    {
        var connection = new OpenRgbConnection();
        try
        {
            await connection.client.ConnectAsync(IPAddress.Loopback, port, token);
            connection.client.NoDelay = true;
            connection.stream = connection.client.GetStream();
            var version = await connection.RequestAsync(0, 40, RgbCodec.U32(3), token);
            if (version.Length != 4) throw PacketReader.Invalid("Invalid SDK version reply");
            connection.ServerVersion = BinaryPrimitives.ReadUInt32LittleEndian(version);
            if (connection.ServerVersion < 3) throw new PluginException("ProtocolMismatch", "OpenRGB SDK version 3 or newer is required");
            await connection.SendAsync(0, 50, Encoding.UTF8.GetBytes("IndependentControlCenter\0"), token);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task SendAsync(uint device, uint packet, byte[] data, CancellationToken token)
    {
        var header = new byte[16]; "ORGB"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), packet);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)data.Length);
        await stream.WriteAsync(header, token);
        if (data.Length != 0) await stream.WriteAsync(data, token);
    }
    public async Task<byte[]> RequestAsync(uint device, uint packet, byte[] data, CancellationToken token)
    {
        await SendAsync(device, packet, data, token);
        for (var skipped = 0; skipped < 32; skipped++)
        {
            var header = new byte[16]; await stream.ReadExactlyAsync(header, token);
            if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw PacketReader.Invalid("Invalid SDK magic");
            var dev = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (size > RgbCodec.MaxPacket) throw PacketReader.Invalid("SDK packet exceeds 8 MiB");
            var payload = new byte[(int)size]; await stream.ReadExactlyAsync(payload, token);
            if (id == 100)
            {
                DeviceListChanged = true;
                throw new PluginException("DeviceListChanged", "OpenRGB device list changed; rediscover before applying");
            }
            if (id == packet && dev == device) return payload;
            // Server identity notifications from newer servers are harmless at v3.
            if (id is 51 or 53 or 55) continue;
            throw PacketReader.Invalid($"Unexpected SDK reply {id} for device {dev}");
        }
        throw PacketReader.Invalid("Too many unsolicited SDK packets");
    }
    public async Task<RgbDevice[]> DiscoverAsync(CancellationToken token)
    {
        var bytes = await RequestAsync(0, 0, [], token);
        if (bytes.Length != 4) throw PacketReader.Invalid("Invalid controller count reply");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > 256) throw PacketReader.Invalid("Too many OpenRGB controllers");
        var devices = new RgbDevice[(int)count];
        for (uint i = 0; i < count; i++) devices[i] = await ReadDeviceAsync(i, token);
        return devices;
    }
    public async Task<RgbDevice> ReadDeviceAsync(uint index, CancellationToken token) => RgbCodec.Device(index, await RequestAsync(index, 1, RgbCodec.U32(3), token));
    public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
}
