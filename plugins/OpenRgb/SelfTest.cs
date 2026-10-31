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
        await GenericTestsAsync(parsed, stable);
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
    private static async Task GenericTestsAsync(RgbDevice memory, string memoryId)
    {
        RgbDevice Device(uint type, string name, string location, string serial = "", string mode = "Direct", uint flags = 32)
            => RgbCodec.Device(0, Fixture(name, location, type, serial: serial, mode: mode, flags: flags));
        var gpu = Device(2, "Test GPU", "PCI:1:0", "GPU-1");
        var keyboard = Device(5, "Test Keyboard", "HID:1", "KB-1");
        var mouse = Device(6, "Test Mouse", "HID:2", "M-1");
        var gamepad = Device(10, "Test Gamepad", "HID:3", "G-1");
        var unsupported = Device(5, "No Static", "HID:4", mode: "Rainbow");
        var modeOnly = Device(6, "Mode Colors Only", "HID:5", mode: "Static", flags: 64);
        var extras = new[] { gpu, keyboard, mouse, gamepad, unsupported, modeOnly };
        var settings = new BackendSettings
        {
            RequestTimeoutMs = 5000,
            AdditionalDevices = extras.Select(d => new DeviceRule { Type = d.Type, Name = d.Name, Location = d.Location, Serial = d.Serial, Detector = $"Test {d.Type}" }).ToArray()
        };
        DevicePolicy.Validate(settings);
        Check(OpenRgbPlugin.Bind([memory, .. extras]).Length == 1, "default remains memory only");
        var bound = OpenRgbPlugin.Bind([memory, .. extras.Select((d, i) => d with { Index = (uint)(i + 1) })], settings);
        Check(bound.Length == 7 && bound[0].Id == memoryId, "opt-in devices and legacy ID coexist");
        var expected = "ene-dram:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("[\"I2C: bus 0 address 0x70\",\"DIMM A\"]"))).ToLowerInvariant();
        Check(memoryId == expected, "persisted ENE hash contract");
        Check(bound.Select(b => DevicePolicy.Kind(b.Device.Type)).SequenceEqual(new[] { "memory", "gpu", "keyboard", "mouse", "gamepad", "keyboard", "mouse" }), "generic kinds use SDK type");
        var ids = bound.ToDictionary(b => b.Device.Name, b => b.Id);
        Check(OpenRgbPlugin.Bind(extras.Reverse().Select((d, i) => d with { Index = (uint)i }).ToArray(), settings).All(b => b.Id == ids[b.Device.Name]), "generic IDs survive reorder");
        Check(OpenRgbPlugin.Bind([gpu, gpu with { Index = 1 }], settings).All(b => b.Ambiguous), "generic duplicates refused");
        Check(OpenRgbPlugin.Bind([gpu with { Serial = "replacement" }], settings).Length == 0, "serial selector refuses replacement");
        var serialRule = settings with { AdditionalDevices = [new() { Type = 5, Name = keyboard.Name, Serial = "KB-1", Detector = "Test 5" }] };
        Check(!OpenRgbPlugin.Bind([keyboard with { Location = "" }], serialRule)[0].Ambiguous, "serial alone is stable identity");
        Check(!unsupported.SupportsStatic && !modeOnly.SupportsStatic, "unsupported static does not advertise effects");
        var inventory = """{"Detectors":{"detectors":{"ENE SMBus DRAM":true,"Test 2":false,"Test 5":false,"Test 6":false,"Test 10":false,"Unknown HID":true,"Test 5 other":true}}}""";
        var sanitized = ManagedBackend.SanitizeConfig(inventory, settings);
        Check(sanitized["Detectors"]!["hid_safe_mode"]!.GetValue<bool>() && sanitized["Detectors"]!["detectors"]!["Test 5"]!.GetValue<bool>()
            && !sanitized["Detectors"]!["detectors"]!["Unknown HID"]!.GetValue<bool>() && !sanitized["Detectors"]!["detectors"]!["Test 5 other"]!.GetValue<bool>(), "only exact opted-in detectors enabled with safe HID");
        ExpectConfigurationFailure(() => ManagedBackend.SanitizeConfig(inventory, settings with { AdditionalDevices = [new() { Type = 5, Name = "K", Location = "HID", Detector = "test 5" }] }));
        ExpectConfigurationFailure(() => DevicePolicy.Validate(settings with { AdditionalDevices = [new() { Type = 5, Name = "*", Location = "HID", Detector = "Test 5" }] }));
        ExpectConfigurationFailure(() => BackendSettings.Parse("""{"enableEneDram":false,"additionalDevices":[],"unknownOverride":true}"""));
        Check(BackendSettings.Parse("""{"enableEneDram":false,"additionalDevices":[]}""").EnableEneDram == false, "explicit no-detector opt-out");
        var hidOnly = ManagedBackend.SanitizeConfig(inventory, serialRule with { EnableEneDram = false });
        Check(!hidOnly["Detectors"]!["detectors"]!["ENE SMBus DRAM"]!.GetValue<bool>(), "peripheral-only disables SMBus");
        Check(ProtectedPaths.UntrustedWrite("S-1-5-32-545", System.Security.AccessControl.FileSystemRights.Modify, false, false)
            && !ProtectedPaths.UntrustedWrite("S-1-5-32-545", System.Security.AccessControl.FileSystemRights.ReadAndExecute, false, false)
            && !ProtectedPaths.UntrustedWrite("S-1-5-18", System.Security.AccessControl.FileSystemRights.FullControl, true, false), "protected ACL policy");
        Check(BackendSettings.SettingsPath("C:/Program Files/LightOrder/plugins/OpenRgb").Replace('\\', '/') == "C:/Program Files/LightOrder/plugins/OpenRgb/backend.json", "settings resolve only beside plugin");
        await using (var server = new MockServer { Devices = extras })
        await using (var plugin = new OpenRgbPlugin(server.Port, settings, standardDiscovery: true))
        {
            var devices = (StandardDevice[])await plugin.HandleAsync("device.discover", Wire.Element(new { }), CancellationToken.None);
            Check(devices.Length == extras.Length && server.Writes == 0, "generic discovery read only");
            Check(devices.Single(d => d.Name == "No Static").Metadata!.Value.GetProperty("effects").GetArrayLength() == 0, "unsupported actual effects empty");
            server.Devices = extras.Reverse().ToArray();
            var target = devices.Single(d => d.Kind == "gpu");
            await plugin.HandleAsync("device.apply", Wire.Element(new { deviceId = target.Id, operationId = "generic-gpu", color = "#123456", brightness = 1, off = false }), CancellationToken.None);
            Check(server.LastWriteIndex == 5 && server.Writes == 2, "GPU rebinding writes its current index after reorder");
            var kb = devices.Single(d => d.Name == "Test Keyboard");
            await plugin.HandleAsync("device.execute", Wire.Element(new { deviceId = kb.Id, capability = "lighting.off", parameters = new { operationId = "keyboard-off" } }), CancellationToken.None);
            Check(server.LastWriteIndex == 4 && server.Color == 0 && server.Writes == 4, "keyboard off binds independently");
            try
            {
                await plugin.HandleAsync("device.apply", Wire.Element(new { deviceId = devices.Single(d => d.Name == "No Static").Id, operationId = "unsupported", color = "#FFFFFF", brightness = 1, off = false }), CancellationToken.None);
                throw new Exception("Expected unsupported");
            }
            catch (PluginException e) when (e.Code == "Unsupported") { }
            server.Devices = [gpu, gpu];
            try
            {
                await plugin.HandleAsync("device.apply", Wire.Element(new { deviceId = target.Id, operationId = "duplicate", color = "#FFFFFF", brightness = 1, off = false }), CancellationToken.None);
                throw new Exception("Expected duplicate refusal");
            }
            catch (PluginException e) when (e.Code == "DeviceChanged") { }
            Check(server.Writes == 4, "unsupported and duplicate writes refused");
        }
        await using (var server = new MockServer { EmptyDiscoveries = 2 })
        await using (var plugin = new OpenRgbPlugin(server.Port, standardDiscovery: true))
        {
            var devices = (StandardDevice[])await plugin.HandleAsync("device.discover", Wire.Element(new { }), CancellationToken.None);
            Check(devices.Length == 1 && server.Writes == 0, "SDK listening before inventory waits for detection");
        }
        await using (var server = new MockServer { EmptyDiscoveries = 100 })
        await using (var plugin = new OpenRgbPlugin(server.Port, settings, standardDiscovery: true))
        {
            try { await plugin.HandleAsync("device.discover", Wire.Element(new { }), CancellationToken.None); throw new Exception("Expected pending inventory"); }
            catch (PluginException e) when (e.Code == "BackendDiscovering") { }
            Check(server.Writes == 0, "empty startup reports retryable detection without writes");
        }
        var process = new FakeOwnedProcess();
        await OwnedProcessShutdown.CloseAsync(process, true, delay => { process.Drained = true; return Task.CompletedTask; });
        Check(process.Drained && process.Closed && process.Killed && process.HasExited, "pending writes do not orphan exact owned process");
        process = new() { GracefulExit = true };
        await OwnedProcessShutdown.CloseAsync(process, false);
        Check(process.Closed && !process.Killed, "graceful owned shutdown avoids termination");
    }
    private sealed class FakeOwnedProcess : IOwnedProcess
    {
        public bool HasExited { get; private set; }
        public bool GracefulExit { get; init; }
        public bool Drained, Closed, Killed;
        public bool CloseMainWindow() { Closed = true; HasExited = GracefulExit; return GracefulExit; }
        public void Kill() { Killed = true; HasExited = true; }
        public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
    }
    private static void ExpectConfigurationFailure(Action action)
    {
        try { action(); throw new Exception("Expected configuration refusal"); }
        catch (PluginException e) when (e.Code == "ConfigurationError") { }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception($"Assertion failed: {message}"); }
    private static void ExpectProtocolFailure(Action action)
    {
        try { action(); throw new Exception("Expected malformed payload refusal"); }
        catch (PluginException e) when (e.Code == "ProtocolError") { }
    }
    private static byte[] Fixture(string name, string location, uint type = 1, uint color = 0, string serial = "", string mode = "Direct", uint flags = 32)
    {
        using var buffer = new MemoryStream(); using var w = new BinaryWriter(buffer, Encoding.UTF8, true);
        w.Write(0u); w.Write(type);
        foreach (var text in new[] { name, "ENE", "ENE SMBus Device", "DIMM_LED", serial, location }) RgbCodec.WriteText(w, text);
        w.Write((ushort)1); w.Write(0u);
        RgbCodec.WriteText(w, mode);
        // Literal fixture fields independently follow the v3 source field order.
        foreach (var field in new uint[] { 0xFFFF, flags, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }) w.Write(field);
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
        public RgbDevice[]? Devices { get; set; }
        public int EmptyDiscoveries { get; set; }
        public uint? LastWriteIndex { get; private set; }
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
                    case 0: reply = RgbCodec.U32(EmptyDiscoveries-- > 0 ? 0u : (uint)(Devices?.Length ?? 2)); break;
                    case 1:
                        Check(data.SequenceEqual(new byte[] { 3, 0, 0, 0 }), "request controller v3");
                        var selected = Devices?[(int)device];
                        reply = selected == null ? Fixture(device == 0 ? "DIMM A" : "Motherboard", device == 0 ? Location : "MB", device == 0 ? 1u : 0u, Color)
                            : Fixture(selected.Name, selected.Location, selected.Type, selected.Colors[0], selected.Serial, selected.Modes[0].Name, selected.Modes[0].Flags);
                        break;
                    case 1101:
                        Check(Devices != null || device == 0 && data.SequenceEqual(RgbCodec.Mode(0, RgbCodec.Device(0, Fixture("DIMM A", Location)).Modes[0])), "mode write fields");
                        LastWriteIndex = device; Writes++; continue;
                    case 1050:
                        Check((Devices != null || device == 0) && data.Length == 14 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 14 && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) == 2, "whole device color layout");
                        Color = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(6));
                        Check(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10)) == Color, "all LEDs same color");
                        if (Devices != null) Devices[(int)device] = Devices[(int)device] with { Colors = [Color, Color] };
                        LastWriteIndex = device; Writes++; continue;
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
