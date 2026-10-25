using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ControlCenter.Contracts;

namespace LightOrder.Service;

/// <summary>Pure and in-memory checks. Never starts a worker, opens the public pipe or touches hardware/configuration.</summary>
public static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            count++;
        }
        void Reject(Action action, string name)
        {
            var rejected = false;
            try { action(); }
            catch (Exception e) when (e is PluginException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        try
        {
            var owner = "S-1-5-21-100-200-300-1001";
            new ServiceSettings(1, owner, ["example.device"]).Validate();
            Check(ServiceSecurity.ValidateOwnerSid(owner).Value == owner, "canonical owner SID");
            Reject(() => ServiceSecurity.ValidateOwnerSid("S-1-1-0"), "reject Everyone owner");
            Reject(() => ServiceSecurity.ValidateOwnerSid("S-1-5-32-545"), "reject Users group owner");
            Reject(() => new ServiceSettings(2, owner, []).Validate(), "schema mismatch");
            Reject(() => new ServiceSettings(1, owner, ["a", "a"]).Validate(), "duplicate enabled IDs");
            Check(ServiceSecurity.IsUnsafeGrant(owner, FileSystemRights.Write, AccessControlType.Allow), "reject user write grant");
            Check(ServiceSecurity.IsUnsafeGrant(owner, FileSystemRights.Delete, AccessControlType.Allow), "reject user delete grant");
            Check(!ServiceSecurity.IsUnsafeGrant(owner, FileSystemRights.ReadAndExecute, AccessControlType.Allow), "allow user read");
            Check(!ServiceSecurity.IsUnsafeGrant("S-1-5-18", FileSystemRights.FullControl, AccessControlType.Allow), "allow SYSTEM write");
            Check(ServiceSecurity.RejectRemoteClients == 8, "native remote reject flag");
            var acl = ServiceSecurity.CreatePipeSecurity(new SecurityIdentifier(owner));
            Check(acl.AreAccessRulesProtected, "pipe inheritance blocked");
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<System.IO.Pipes.PipeAccessRule>().ToArray();
            Check(rules.Length == 3 && rules.All(r => new[] { owner, "S-1-5-18", "S-1-5-32-544" }.Contains(r.IdentityReference.Value)), "only owner/admin/SYSTEM pipe grants");
            Check((rules.Single(r => r.IdentityReference.Value == owner).PipeAccessRights & System.IO.Pipes.PipeAccessRights.CreateNewInstance) == 0,
                "owner cannot create server instances");

            var descriptor = new PluginDescriptor("example.device", "Example", "1.0", 1, "worker.exe", [], [], ManifestValidation.AllowedMethods, true);
            ManifestValidation.Validate(descriptor);
            Reject(() => ManifestValidation.Validate(descriptor with { EntryPoint = "../evil.exe" }), "entry traversal");
            Reject(() => ManifestValidation.Validate(descriptor with { EntryPoint = "C:\\evil.exe" }), "absolute entry");
            Reject(() => ManifestValidation.Validate(descriptor with { EntryPoint = "worker.exe:stream.exe" }), "alternate stream entry");
            Reject(() => ManifestValidation.Validate(descriptor with { Methods = ["file.write"] }), "manifest method whitelist");
            Reject(() => ManifestValidation.Validate(descriptor with { ProtocolMajor = 2 }), "plugin protocol mismatch");

            var plugin = new FakePlugin(descriptor);
            var router = new DeviceRouter();
            router.Replace(plugin, plugin.Devices);
            Check(router.Devices.Single().Id == "example.device::test", "external ID prefix");
            var command = new DeviceCommand("example.device::test", StandardCapabilities.LightingStatic, Wire.Element(new { color = "#123456" }));
            var route = router.Resolve(command);
            Check(route.Command.DeviceId == "test" && ReferenceEquals(route.Plugin, plugin), "route strips only known prefix");
            Reject(() => router.Resolve(command with { DeviceId = "other::test" }), "undiscovered plugin rejected");
            Reject(() => router.Resolve(command with { Capability = "file.write" }), "unadvertised capability rejected");
            Reject(() => router.Resolve(command with { Parameters = Wire.Element("path.exe") }), "scalar parameters rejected");
            Check(router.Resolve(command with { Capability = StandardCapabilities.Release }).Method == StandardMethods.Execute, "advertised release uses typed command routing");
            var samples = router.ValidateTelemetry(plugin, plugin.Telemetry);
            Check(samples[0].GetProperty("deviceId").GetString() == "example.device::test", "telemetry ID prefix");
            Reject(() => router.ValidateTelemetry(plugin, Wire.Element(new[] { new { deviceId = "unknown" } })), "unknown telemetry device rejected");
            router.Remove(plugin);
            Reject(() => router.Resolve(command), "disconnected route removed");

            var request = ServiceProtocol.Parse("{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"app.snapshot\"}");
            Check(request.Id == 42, "request correlation ID");
            Reject(() => ServiceProtocol.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"file.write\"}"), "unknown method rejected");
            Reject(() => ServiceProtocol.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":2,\"method\":\"app.snapshot\"}"), "duplicate request fields");
            Reject(() => ServiceProtocol.Parse("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"app.snapshot\"}"), "RPC version mismatch");
            Reject(() => ServiceProtocol.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"app.snapshot\"}"), "notifications rejected");
            Check(ServiceProtocol.RecoverId("{\"jsonrpc\":\"2.0\",\"id\":51,\"method\":\"forbidden\"}") == 51, "error ID matched");
            Check(!ServiceProtocol.Error(51, new IOException("SECRET_PATH")).Error!.Message.Contains("SECRET_PATH"), "OS error details redacted");
            using var goodFrame = new MemoryStream(Encoding.UTF8.GetBytes("第一\r\nsecond\n"));
            var framing = new RpcFraming(goodFrame);
            Check(await framing.ReadAsync(default) == "第一" && await framing.ReadAsync(default) == "second" && await framing.ReadAsync(default) is null, "UTF8 CRLF and buffered frames");
            using var oversized = new MemoryStream(Encoding.UTF8.GetBytes(new string('中', Wire.MaxLine / 3 + 1) + "\n"));
            var overflowRejected = false;
            try { await new RpcFraming(oversized).ReadAsync(default); } catch (InvalidDataException) { overflowRejected = true; }
            Check(overflowRejected, "frame limit counts UTF8 bytes");
            using var truncated = new MemoryStream(Encoding.UTF8.GetBytes("unfinished"));
            var partialRejected = false;
            try { await new RpcFraming(truncated).ReadAsync(default); } catch (EndOfStreamException) { partialRejected = true; }
            Check(partialRejected, "partial frame rejected");
            using var badUtf8 = new MemoryStream(new byte[] { 0xff, 10 });
            var utf8Rejected = false;
            try { await new RpcFraming(badUtf8).ReadAsync(default); } catch (DecoderFallbackException) { utf8Rejected = true; }
            Check(utf8Rejected, "invalid UTF8 rejected");

            ProfileStore.Validate(Wire.Element(new { schemaVersion = 1, profiles = new[] { new { name = "Test", file = "data only" } } }));
            count++;
            Reject(() => ProfileStore.Validate(Wire.Element(new { schemaVersion = 2 })), "profile schema mismatch");
            Reject(() => ProfileStore.Validate(Wire.Element(new { schemaVersion = 1, data = new string('x', ProfileStore.MaxBytes) })), "profile size limit");
            using var duplicateProfile = JsonDocument.Parse("{\"schemaVersion\":1,\"x\":1,\"x\":2}");
            Reject(() => ProfileStore.Validate(duplicateProfile.RootElement), "duplicate profile fields");

            await using (var runtime = new ServiceRuntime(new ProfileStore("unused-test-path")))
            {
                runtime.AddTestPlugin(plugin);
                await runtime.HandleAsync(StandardMethods.Refresh, Wire.Element(new { }), 1, default);
                var applied = (ApplyResult)await runtime.HandleAsync(StandardMethods.Apply, Wire.Element(command), 2, default);
                Check(applied.Status == "applied" && plugin.LastCommand?.DeviceId == "test", "runtime standard command shape");
                plugin.FailExecute = true;
                var timedOut = false;
                try { await runtime.HandleAsync(StandardMethods.Apply, Wire.Element(command), 3, default); }
                catch (PluginException e) { timedOut = e.Code == "TimeoutOutcomeUnknown"; }
                Check(timedOut && plugin.ExecuteCount == 2, "unknown outcome not retried");
                var snapshot = (ServiceSnapshot)await runtime.HandleAsync(StandardMethods.Snapshot, Wire.Element(new { }), 4, default);
                Check(snapshot.Devices.Length == 0 && snapshot.Telemetry.Length == 0, "fault removes stale devices/telemetry");
                for (var i = 0; i < 100; i++) runtime.AddDiagnostic(new string('x', 2000));
                snapshot = (ServiceSnapshot)await runtime.HandleAsync(StandardMethods.Snapshot, Wire.Element(new { }), 5, default);
                Check(snapshot.Diagnostics.Length == 64 && snapshot.Diagnostics.All(s => s.Length < 560), "bounded diagnostics");
            }
            Check(plugin.Released && plugin.Disposed, "release and disposal after failure");
            var first = new FakePlugin(descriptor with { Id = "test.first" });
            var broken = new FakePlugin(descriptor with { Id = "test.broken" }) { FailRelease = true };
            await using (var runtime = new ServiceRuntime(new ProfileStore("unused-test-path")))
            {
                runtime.AddTestPlugin(first);
                runtime.AddTestPlugin(broken);
                await runtime.HandleAsync(StandardMethods.Refresh, Wire.Element(new { }), 10, default);
                first.DelayExecute = true;
                var firstCommand = command with { DeviceId = "test.first::test" };
                await Task.WhenAll(Enumerable.Range(0, 5).Select(i => runtime.HandleAsync(StandardMethods.Apply, Wire.Element(firstCommand), 20 + i, default)));
                Check(first.MaxConcurrentCalls == 1 && first.ExecuteCount == 5, "concurrent client operations serialized");
            }
            Check(broken.Disposed && first.Disposed && first.Released, "release failure does not skip remaining cleanup");
            Console.WriteLine($"PASS {count} hardware-free service checks. No service, public pipe or hardware was started.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private sealed class FakePlugin(PluginDescriptor descriptor) : IServicePlugin
    {
        public PluginDescriptor Descriptor => descriptor;
        public StandardDevice[] Devices => [new("test", "Test device", "example", [StandardCapabilities.LightingStatic, StandardMethods.Release], "ready", "")];
        public JsonElement Telemetry => Wire.Element(new[] { new { deviceId = "test", name = "Example", type = "temperature", value = 24.0, unit = "C", timestamp = DateTimeOffset.UtcNow } });
        public DeviceCommand? LastCommand { get; private set; }
        public bool FailExecute { get; set; }
        public bool FailRelease { get; set; }
        public bool DelayExecute { get; set; }
        public int MaxConcurrentCalls { get; private set; }
        private int concurrentCalls;
        public int ExecuteCount { get; private set; }
        public bool Released { get; private set; }
        public bool Disposed { get; private set; }
        public async Task<JsonElement> CallAsync(string method, object parameters)
        {
            if (method == StandardMethods.Discover) return Wire.Element(Devices);
            if (method == StandardMethods.Telemetry) return Telemetry;
            if (method == StandardMethods.Release)
            {
                Released = true;
                if (FailRelease) throw new IOException("Simulated release failure");
                return Wire.Element(new ApplyResult("restored", "Verified fake restoration"));
            }
            if (method == StandardMethods.Execute)
            {
                ExecuteCount++;
                LastCommand = (DeviceCommand)parameters;
                if (FailExecute) throw new PluginException("TimeoutOutcomeUnknown", "Simulated timeout");
                var concurrent = Interlocked.Increment(ref concurrentCalls);
                MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, concurrent);
                try
                {
                    if (DelayExecute) await Task.Delay(10);
                    return Wire.Element(new ApplyResult("applied", "Fake readback"));
                }
                finally { Interlocked.Decrement(ref concurrentCalls); }
            }
            throw new InvalidOperationException("Unexpected fake method");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
