using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlCenter.Contracts;

namespace ControlCenter.OpenRgb;

internal static class SelfTest
{
    public static async Task RunAsync()
    {
        var sample = Fixture("DIMM A", "I2C: bus 0 address 0x70");
        var parsed = RgbCodec.Device(0, sample);
        Check(parsed.IsEneDram && parsed.Colors.Length == 2 && parsed.StaticMode == 0, "v3 device layout");
        for (var cut = 0; cut < sample.Length; cut++)
            ExpectProtocolFailure(() => RgbCodec.Device(0, sample[..cut]));
        var extra = sample.Concat(new byte[] { 0 }).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(extra, (uint)extra.Length);
        ExpectProtocolFailure(() => RgbCodec.Device(0, extra));
        var malformed = (byte[])sample.Clone(); malformed[8] = 0xFF; malformed[9] = 0xFF;
        ExpectProtocolFailure(() => RgbCodec.Device(0, malformed));
        var modeBytes = RgbCodec.Mode(0, parsed.Modes[0]);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(modeBytes) == modeBytes.Length, "mode includes full payload size");
        Check(RgbCodec.Hex(0x00332211) == "#112233", "RGB little endian packing");
        var stable = OpenRgbPlugin.Bind([parsed])[0].Id;
        Check(OpenRgbPlugin.Bind([parsed with { Index = 9 }])[0].Id == stable, "stable ID across reorder");
        Check(OpenRgbPlugin.Bind([parsed, parsed with { Index = 1 }]).All(x => x.Ambiguous), "duplicate identity blocked");
        Check(OpenRgbPlugin.Bind([parsed with { Location = "" }])[0].Ambiguous, "missing location blocked");
        Check(OpenRgbPlugin.Bind([parsed with { Type = 0 }, parsed with { Description = "Other DRAM" }]).Length == 0, "ENE DRAM filter");
        var clean = ManagedBackend.SanitizeConfig("""
            {"Detectors":{"detectors":{"ENE SMBus DRAM":true,"Other":true,"other":true}},"ProfileManager":{"service_startup_profile":"red"},"Plugins":{"evil":true}}
            """);
        Check(clean["Detectors"]!["detectors"]!["Other"]!.GetValue<bool>() == false && clean["Detectors"]!["detectors"]!["other"]!.GetValue<bool>() == false
            && clean["Detectors"]!["detectors"]!["ENE SMBus DRAM"]!.GetValue<bool>() && clean["ProfileManager"] == null && clean["Plugins"] == null,
            "config sanitization preserves case-distinct inventory and strips profiles/plugins");
        await using (var mock = new MockServer())
        await using (var plugin = new OpenRgbPlugin(mock.Port))
        {
            var devices = (DeviceInfo[])await plugin.HandleAsync("device.discover", Wire.Element(new { }), CancellationToken.None);
            Check(devices.Length == 1 && mock.Writes == 0, "discovery has no lighting writes and filters non-DRAM");
            var request = Wire.Element(new { deviceId = devices[0].Id, operationId = "color-1", color = "#804020", brightness = 0.5, off = false });
            var apply = (ApplyResult)await plugin.HandleAsync("device.apply", request, CancellationToken.None);
            Check(apply.Status == "accepted" && apply.Readback is CacheReadback { Source: "openrgb-cache", PhysicalActual: "unknown", MatchesRequested: true }, "readback explicitly cache only");
            Check(mock.Color == 0x00102040 && mock.Writes == 2, "whole device brightness-scaled RGB and mode");
            await plugin.HandleAsync("device.apply", request, CancellationToken.None);
            Check(mock.Writes == 2, "operation deduplication");
            try
            {
                await plugin.HandleAsync("device.apply", Wire.Element(new { deviceId = devices[0].Id, operationId = "color-1", color = "#FFFFFF", brightness = 1, off = false }), CancellationToken.None);
                throw new Exception("Expected operation conflict");
            }
            catch (PluginException e) when (e.Code == "OperationConflict") { }
            await plugin.HandleAsync("device.apply", Wire.Element(new { deviceId = devices[0].Id, operationId = "off-2", color = "#FFFFFF", brightness = 1, off = true }), CancellationToken.None);
            Check(mock.Color == 0 && mock.Writes == 4, "off writes all LEDs black");
            mock.Location = "I2C: changed";
            try
            {
                await plugin.HandleAsync("device.apply", Wire.Element(new { deviceId = devices[0].Id, operationId = "stale-3", color = "#FFFFFF", brightness = 1, off = false }), CancellationToken.None);
                throw new Exception("Expected changed device refusal");
            }
            catch (PluginException e) when (e.Code == "DeviceChanged") { }
            Check(mock.Writes == 4, "stale device must not write");
        }
        await using (var old = new MockServer { Version = 2 })
        {
            try { await using var connection = await OpenRgbConnection.ConnectAsync(old.Port, CancellationToken.None); throw new Exception("Expected version refusal"); }
            catch (PluginException e) when (e.Code == "ProtocolMismatch") { }
        }
        foreach (var fault in new[] { "magic", "oversize", "truncated", "timeout", "changed" })
        {
            await using var bad = new MockServer { Fault = fault };
            using var timeout = new CancellationTokenSource(500);
            try
            {
                await using var connection = await OpenRgbConnection.ConnectAsync(bad.Port, timeout.Token);
                throw new Exception($"Expected {fault} refusal");
            }
            catch (Exception e) when (e is PluginException or EndOfStreamException or OperationCanceledException) { }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception($"Assertion failed: {message}"); }
    private static void ExpectProtocolFailure(Action action)
    {
        try { action(); throw new Exception("Expected malformed payload refusal"); }
        catch (PluginException e) when (e.Code == "ProtocolError") { }
    }
    private static byte[] Fixture(string name, string location, uint type = 1, uint color = 0)
    {
        using var buffer = new MemoryStream(); using var w = new BinaryWriter(buffer, Encoding.UTF8, true);
        w.Write(0u); w.Write(type);
        foreach (var text in new[] { name, "ENE", "ENE SMBus Device", "DIMM_LED", "", location }) RgbCodec.WriteText(w, text);
        w.Write((ushort)1); w.Write(0u);
        RgbCodec.WriteText(w, "Direct");
        // Literal fixture fields independently follow the v3 source field order.
        foreach (var field in new uint[] { 0xFFFF, 32, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }) w.Write(field);
        w.Write((ushort)0);
        w.Write((ushort)1); RgbCodec.WriteText(w, "DRAM");
        w.Write(1u); w.Write(2u); w.Write(2u); w.Write(2u); w.Write((ushort)0);
        w.Write((ushort)2);
        RgbCodec.WriteText(w, "LED 1"); w.Write(0u);
        RgbCodec.WriteText(w, "LED 2"); w.Write(1u);
        w.Write((ushort)2); w.Write(color); w.Write(color);
        var data = buffer.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length); return data;
    }
    private sealed class MockServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task run;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public uint Version { get; init; } = 6;
        public string? Fault { get; init; }
        public int Writes { get; private set; }
        public uint Color { get; private set; }
        public string Location { get; set; } = "I2C: bus 0 address 0x70";
        public MockServer() { listener.Start(); run = RunAsync(); }
        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    try { await ServeAsync(stream); }
                    catch (Exception e) when (e is IOException or SocketException) { }
                }
            }
            catch (Exception e) when (stop.IsCancellationRequested && e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
        private async Task ServeAsync(NetworkStream stream)
        {
            while (!stop.IsCancellationRequested)
            {
                var header = new byte[16]; await stream.ReadExactlyAsync(header, stop.Token);
                Check(header.AsSpan(0, 4).SequenceEqual("ORGB"u8), "client magic");
                var device = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                Check(size <= 65536, "bounded mock request");
                var data = new byte[(int)size]; await stream.ReadExactlyAsync(data, stop.Token);
                byte[] reply;
                switch (id)
                {
                    case 40:
                        Check(data.SequenceEqual(new byte[] { 3, 0, 0, 0 }), "negotiate exactly v3");
                        reply = RgbCodec.U32(Version); break;
                    case 50:
                        Check(data[^1] == 0, "null terminated client name"); continue;
                    case 0: reply = RgbCodec.U32(2); break;
                    case 1:
                        Check(data.SequenceEqual(new byte[] { 3, 0, 0, 0 }), "request controller v3");
                        reply = Fixture(device == 0 ? "DIMM A" : "Motherboard", device == 0 ? Location : "MB", device == 0 ? 1u : 0u, Color); break;
                    case 1101:
                        Check(device == 0 && data.SequenceEqual(RgbCodec.Mode(0, RgbCodec.Device(0, Fixture("DIMM A", Location)).Modes[0])), "mode write fields");
                        Writes++; continue;
                    case 1050:
                        Check(device == 0 && data.Length == 14 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 14 && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) == 2, "whole device color layout");
                        Color = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(6));
                        Check(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10)) == Color, "all LEDs same color");
                        Writes++; continue;
                    default: throw new Exception($"Unexpected client command {id}");
                }
                if (Fault == "timeout") { await Task.Delay(Timeout.Infinite, stop.Token); return; }
                "ORGB"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)reply.Length);
                if (Fault == "magic") header[0] = (byte)'X';
                if (Fault == "oversize") BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), RgbCodec.MaxPacket + 1u);
                if (Fault == "changed") { BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 100); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0); reply = []; }
                if (Fault == "truncated") { await stream.WriteAsync(header.AsMemory(0, 7), stop.Token); return; }
                // Fragment both header and payload to exercise ReadExactlyAsync.
                await stream.WriteAsync(header.AsMemory(0, 7), stop.Token);
                await stream.WriteAsync(header.AsMemory(7), stop.Token);
                foreach (var b in reply) await stream.WriteAsync(new byte[] { b }, stop.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await run; } finally { stop.Dispose(); }
        }
    }
}
