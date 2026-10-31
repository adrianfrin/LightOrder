using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ControlCenter.Contracts;

namespace LightOrder.Service;

/// <summary>Hardware-free checks, including private randomly named pipes. Never opens the public pipe or starts a plugin process.</summary>
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
            var command = new DeviceCommand("example.device::test", StandardCapabilities.LightingStatic, Wire.Element(new { color = "#123456", brightness = 0.5 }));
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
            var budgetRouter = new DeviceRouter();
            var largeDevice = plugin.Devices[0] with { Metadata = Wire.Element(new { processInventory = new string('p', 20000) }) };
            budgetRouter.Replace(plugin, [largeDevice]);
            Check(budgetRouter.Devices.Length == 1, "realistic descriptor larger than legacy 16 KiB accepted");
            Reject(() => budgetRouter.Replace(plugin, [largeDevice with { Metadata = Wire.Element(new { data = new string('x', 128 * 1024) }) }]), "descriptor above 128 KiB rejected");
            var otherBudgetPlugin = new FakePlugin(descriptor with { Id = "budget.other" });
            var nearLimit = largeDevice with { Metadata = Wire.Element(new { data = new string('x', 100000) }) };
            budgetRouter.Replace(plugin, [nearLimit with { Id = "one" }, nearLimit with { Id = "two" }, nearLimit with { Id = "three" }]);
            Reject(() => budgetRouter.Replace(otherBudgetPlugin, [nearLimit]), "aggregate descriptor budget rejected");
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
            var delayed = new FakePlugin(descriptor with { Id = "test.delayed" }) { PendingDiscovery = true };
            await using (var runtime = new ServiceRuntime(new ProfileStore("unused-test-path")))
            {
                runtime.AddTestPlugin(delayed);
                var pendingSnapshot = (ServiceSnapshot)await runtime.HandleAsync(StandardMethods.Refresh, Wire.Element(new { }), 30, default);
                Check(pendingSnapshot.Devices.Length == 0 && pendingSnapshot.Diagnostics.Any(d => d.Contains("discovery pending")), "read-only backend startup is pending, not faulted");
                delayed.PendingDiscovery = false;
                var readySnapshot = (ServiceSnapshot)await runtime.HandleAsync(StandardMethods.Refresh, Wire.Element(new { }), 31, default);
                Check(readySnapshot.Devices.Length == 1 && readySnapshot.Devices[0].Capabilities.Contains(StandardCapabilities.LightingEffect), "pending discovery may succeed without service restart");
                var effect = new DeviceCommand("test.delayed::test", StandardCapabilities.LightingEffect,
                    Wire.Element(new { effect = "breathing", color = "#123456", brightness = .5, periodSeconds = 6 }));
                var result = (ApplyResult)await runtime.HandleAsync(StandardMethods.Apply, Wire.Element(effect), 32, default);
                Check(result.Status == "accepted" && delayed.LastCommand?.Capability == StandardCapabilities.LightingStatic, "virtual lighting capability dispatches supported plugin static frame");
                var dynamicSnapshot = (ServiceSnapshot)await runtime.HandleAsync(StandardMethods.Snapshot, Wire.Element(new { }), 33, default);
                Check(dynamicSnapshot.Devices[0].Metadata!.Value.GetProperty("current").GetProperty("effect").GetString() == "breathing", "runtime snapshot preserves active effect mode");
                Reject(() => runtime.ValidateCommand(effect with { Capability = StandardCapabilities.LightingStatic, Parameters = Wire.Element(new { color = "#123456", brightness = 2 }) }), "service rejects static brightness before plugin call");
                var store = new MemoryAutomationStore();
                runtime.ConfigureAutomation(new AutomationEngine(store, runtime.ExecuteAutomatedAsync, runtime.ValidateCommand));
                var automationRules = Wire.Element(new { schemaVersion = 1, rules = new[] { new { id = "evening", name = "Evening", enabled = false,
                    trigger = new { kind = "daily", time = "20:00", days = new[] { 1 } }, commands = new[] { effect } } } });
                _ = await runtime.HandleAsync(StandardMethods.AutomationWrite, automationRules, 34, default);
                var saved = (JsonElement)await runtime.HandleAsync(StandardMethods.AutomationRead, Wire.Element(new { }), 35, default);
                Check(saved.GetProperty("rules")[0].GetProperty("trigger").GetProperty("kind").GetString() == "daily", "RPC enum serialization matches lower-case browser contract");
                Check(saved.GetProperty("status")[0].GetProperty("ruleId").GetString() == "evening", "automation rule status matches public ID contract");
            }
            await CheckSessionSnapshotsAsync(descriptor, Check);
            await CheckConcurrentDisposalAsync(descriptor, Check);
            await CheckPrivatePipeLifecycleAsync(Check);
            count += await LightingEffectTests.RunAsync();
            Console.WriteLine($"PASS {count} hardware-free service checks. No service, public pipe or hardware was started.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static async Task CheckConcurrentDisposalAsync(PluginDescriptor descriptor, Action<bool, string> check)
    {
        var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin(descriptor with { Id = "test.dispose" })
        {
            ReleaseEntered = releaseEntered,
            AllowRelease = allowRelease.Task
        };
        await using var runtime = new ServiceRuntime(new ProfileStore("unused-test-path"));
        runtime.AddTestPlugin(plugin);
        runtime.StartControlLoop(default);
        var first = runtime.DisposeAsync().AsTask();
        try
        {
            await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposals = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                var task = runtime.DisposeAsync().AsTask();
                return (Task: task, Waiting: !task.IsCompleted);
            })));
            check(!first.IsCompleted && disposals.All(result => result.Waiting && ReferenceEquals(first, result.Task)),
                "concurrent disposers wait for the same in-flight release");
            var shared = runtime.DisposeAsync().AsTask();
            check(ReferenceEquals(first, shared), "repeat disposal returns the same cleanup task");
            allowRelease.TrySetResult();
            await Task.WhenAll(disposals.Select(result => result.Task).Append(first)).WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.DisposeAsync();
            check(plugin.ReleaseCount == 1 && plugin.DisposeCount == 1, "running control loop and concurrent repeat disposal release/dispose plugin once");
            var restartRejected = false;
            try { runtime.StartControlLoop(default); } catch (ObjectDisposedException) { restartRejected = true; }
            check(restartRejected, "control loop cannot start after disposal");
        }
        finally { allowRelease.TrySetResult(); }
    }

    private static async Task CheckPrivatePipeLifecycleAsync(Action<bool, string> check)
    {
        var pipeName = "LightOrder.SelfTest." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var finished = new SemaphoreSlim(0);
        await using var runtime = new ServiceRuntime(new ProfileStore("unused-test-path"));
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var served = 0;
        var peerAlreadyClosed = 0;
        var abruptClientClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServiceWorker.RunConnectionsAsync(pipe, async client =>
        {
            try
            {
                // Force a complete request's client to close before the server can write its response.
                if (Volatile.Read(ref served) == 2) await abruptClientClosed.Task.WaitAsync(stop.Token);
                await ServiceWorker.ServeClientAsync(client, runtime, stop.Token);
            }
            finally
            {
                if (!client.IsConnected) Interlocked.Increment(ref peerAlreadyClosed);
                Interlocked.Increment(ref served);
                finished.Release();
            }
        }, diagnostics.Enqueue, stop.Token);
        try
        {
            // Every next client must be accepted after the prior client has closed its stream.
            for (var index = 0; index < 10; index++)
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await client.ConnectAsync(timeout.Token);
                    if (index % 3 == 1)
                        await client.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\""), timeout.Token); // truncated frame
                    else
                    {
                        await client.WriteAsync(Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{index},\"method\":\"app.plugins\"}}\n"), timeout.Token);
                        if (index % 3 == 0)
                        {
                            var response = await new RpcFraming(client).ReadAsync(timeout.Token);
                            using var document = response is null ? null : JsonDocument.Parse(response);
                            check(document?.RootElement.GetProperty("id").GetInt32() == index,
                                "private pipe response before normal close " + index);
                        }
                        // Other iterations close immediately after sending, before reading a response.
                    }
                }
                if (index == 2) abruptClientClosed.TrySetResult();
                await finished.WaitAsync(timeout.Token);
                check(!server.IsCompleted, "private pipe listener survives closed client " + index);
            }
            check(served == 10 && peerAlreadyClosed > 0, "actual broken pipe state reset before accepting next client");
            check(diagnostics.Any(text => text.Contains(nameof(EndOfStreamException))), "partial frame close stays connection-local");
        }
        finally
        {
            stop.Cancel();
            try { await server.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        pipe.Dispose();
        var listenerFailurePropagated = false;
        try { await ServiceWorker.RunConnectionsAsync(pipe, _ => Task.CompletedTask, _ => { }, default); }
        catch (ObjectDisposedException) { listenerFailurePropagated = true; }
        check(listenerFailurePropagated, "listener lifetime errors remain fatal instead of retrying");
        var safe = ServiceWorker.SafeExceptionDetails(new IOException("SECRET_REQUEST", new InvalidOperationException("SECRET_INNER")));
        check(safe.Contains(nameof(IOException)) && safe.Contains(nameof(InvalidOperationException)) && safe.Contains("HResult=0x") &&
            !safe.Contains("SECRET"), "failure details retain exception chain and native code without arbitrary messages");
    }

    private static async Task CheckSessionSnapshotsAsync(PluginDescriptor descriptor, Action<bool, string> check)
    {
        var active = false;
        var status = "ready";
        var sampleCount = 0;
        var restorationVerified = false;
        var writable = true;
        var fan = new FakePlugin(descriptor with { Id = "test.cooling" })
        {
            DiscoverDevices = () => [new StandardDevice("test", "Cooling", "monitor",
                writable ? [StandardCapabilities.FanProfile, StandardCapabilities.Release] : [StandardCapabilities.Release],
                status, "Current cooling state: " + status,
                Wire.Element(new { session = new { active, status, sampleCount, restorationVerified } }))],
            CommandApplied = command =>
            {
                active = command.Capability == StandardCapabilities.FanProfile;
                status = active ? "active" : "ready";
                restorationVerified = !active;
                if (active) sampleCount++;
            }
        };
        var lighting = new FakePlugin(descriptor with { Id = "test.lighting" });
        var processes = new FakePlugin(descriptor with { Id = "test.processes" })
        {
            DiscoverDevices = () => [new StandardDevice("test", "Processes", "system", [StandardCapabilities.ProcessPolicy], "ready", "")]
        };
        await using var runtime = new ServiceRuntime(new ProfileStore("unused-test-path"));
        runtime.AddTestPlugin(fan);
        runtime.AddTestPlugin(lighting);
        runtime.AddTestPlugin(processes);
        async Task<ServiceSnapshot> Snapshot(string method = StandardMethods.Snapshot) =>
            (ServiceSnapshot)await runtime.HandleAsync(method, Wire.Element(new { }), 40, default);
        StandardDevice Cooling(ServiceSnapshot snapshot) => snapshot.Devices.Single(d => d.Id == "test.cooling::test");
        JsonElement Session(ServiceSnapshot snapshot) => Cooling(snapshot).Metadata!.Value.GetProperty("session");

        var snapshot = await Snapshot(StandardMethods.Refresh);
        check(fan.DiscoverCount == 1 && lighting.DiscoverCount == 1 && processes.DiscoverCount == 1,
            "refresh discovers each plugin once, including session descriptors");
        check(!Session(snapshot).GetProperty("active").GetBoolean() && Cooling(snapshot).Status == "ready",
            "initial cooling descriptor reports ready");
        await runtime.HandleAsync(StandardMethods.Apply, Wire.Element(new DeviceCommand("test.cooling::test",
            StandardCapabilities.FanProfile, Wire.Element(FanProfile.Silent))), 41, default);
        snapshot = await Snapshot();
        check(Cooling(snapshot).Status == "active" && Cooling(snapshot).Detail == "Current cooling state: active" &&
            Session(snapshot).GetProperty("active").GetBoolean() && Session(snapshot).GetProperty("status").GetString() == "active" &&
            Session(snapshot).GetProperty("sampleCount").GetInt32() == 1 && !Session(snapshot).GetProperty("restorationVerified").GetBoolean(),
            "ordinary snapshot refreshes descriptor and active session after fan command");
        sampleCount = 7; // Simulate background polling without another command or app.refresh.
        snapshot = await Snapshot();
        check(Session(snapshot).GetProperty("sampleCount").GetInt32() == 7,
            "ordinary snapshot sees background fan sample progress");
        await runtime.HandleAsync(StandardMethods.Apply, Wire.Element(new DeviceCommand("test.cooling::test",
            StandardCapabilities.Release, Wire.Element(new { }))), 42, default);
        snapshot = await Snapshot();
        check(Cooling(snapshot).Status == "ready" && !Session(snapshot).GetProperty("active").GetBoolean() &&
            Session(snapshot).GetProperty("restorationVerified").GetBoolean(),
            "ordinary snapshot sees released session and verified restoration");
        writable = false;
        status = "read-only";
        snapshot = await Snapshot();
        check(!Cooling(snapshot).Capabilities.Contains(StandardCapabilities.FanProfile) && Cooling(snapshot).Status == "read-only",
            "snapshot replaces capabilities when fan control becomes read-only");
        status = "restoration-unverified";
        restorationVerified = false;
        snapshot = await Snapshot();
        check(Cooling(snapshot).Status == "restoration-unverified" && !Session(snapshot).GetProperty("restorationVerified").GetBoolean(),
            "previously advertised fan plugin remains current after capability withdrawal");
        check(fan.DiscoverCount == 6 && lighting.DiscoverCount == 1 && processes.DiscoverCount == 1 &&
            fan.TelemetryCount == 6 && lighting.TelemetryCount == 6 && processes.TelemetryCount == 6,
            "ordinary snapshots rediscover only fan plugin and preserve telemetry for all plugins");

        fan.PendingDiscovery = true;
        var telemetryBeforePending = fan.TelemetryCount;
        snapshot = await Snapshot();
        check(snapshot.Devices.All(d => d.Id != "test.cooling::test") &&
            snapshot.Telemetry.All(t => t.GetProperty("deviceId").GetString() != "test.cooling::test") &&
            fan.TelemetryCount == telemetryBeforePending && snapshot.Diagnostics.Any(d => d.Contains("discovery pending")),
            "session discovery pending removes stale routes and skips telemetry without faulting");
        fan.PendingDiscovery = false;
        var discoveryBeforeRetry = fan.DiscoverCount;
        snapshot = await Snapshot();
        check(fan.DiscoverCount == discoveryBeforeRetry && fan.TelemetryCount == telemetryBeforePending,
            "ordinary snapshot preserves pending discovery retry scheduling");
        snapshot = await Snapshot(StandardMethods.Refresh);
        check(Cooling(snapshot).Status == status && fan.DiscoverCount == discoveryBeforeRetry + 1 &&
            snapshot.Diagnostics.Any(d => d.Contains("discovery ready")),
            "pending fan discovery can recover through existing refresh path");

        fan.FailDiscovery = true;
        var telemetryBeforeFault = fan.TelemetryCount;
        snapshot = await Snapshot();
        check(snapshot.Devices.All(d => d.Id != "test.cooling::test") &&
            snapshot.Telemetry.All(t => t.GetProperty("deviceId").GetString() != "test.cooling::test") &&
            fan.TelemetryCount == telemetryBeforeFault && snapshot.Diagnostics.Any(d => d.Contains("disconnected")),
            "failed session discovery faults plugin and omits stale device and telemetry");
        fan.FailDiscovery = false;
        var discoveryAfterFault = fan.DiscoverCount;
        _ = await Snapshot();
        snapshot = await Snapshot(StandardMethods.Refresh);
        check(fan.DiscoverCount == discoveryAfterFault && fan.TelemetryCount == telemetryBeforeFault &&
            snapshot.Devices.Length == 2 && snapshot.Telemetry.Length == 2,
            "faulted fan plugin is not retried while unrelated plugins remain available");
    }

    private sealed class MemoryAutomationStore : IAutomationStore
    {
        private byte[]? document, journal;
        public byte[]? LoadDocument() => document;
        public byte[]? LoadJournal() => journal;
        public void SaveDocument(byte[] bytes) => document = bytes.ToArray();
        public void SaveJournal(byte[] bytes) => journal = bytes.ToArray();
    }

    private sealed class FakePlugin(PluginDescriptor descriptor) : IServicePlugin
    {
        public PluginDescriptor Descriptor => descriptor;
        public StandardDevice[] Devices => DiscoverDevices?.Invoke() ?? [new("test", "Test device", "example", [StandardCapabilities.LightingStatic, StandardMethods.Release], "ready", "")];
        public Func<StandardDevice[]>? DiscoverDevices { get; init; }
        public Action<DeviceCommand>? CommandApplied { get; init; }
        public int DiscoverCount { get; private set; }
        public int TelemetryCount { get; private set; }
        public bool FailDiscovery { get; set; }
        public JsonElement Telemetry => Wire.Element(new[] { new { deviceId = "test", name = "Example", type = "temperature", value = 24.0, unit = "C", timestamp = DateTimeOffset.UtcNow } });
        public DeviceCommand? LastCommand { get; private set; }
        public bool FailExecute { get; set; }
        public bool PendingDiscovery { get; set; }
        public bool FailRelease { get; set; }
        public bool DelayExecute { get; set; }
        public int MaxConcurrentCalls { get; private set; }
        private int concurrentCalls;
        public int ExecuteCount { get; private set; }
        public bool Released { get; private set; }
        public bool Disposed { get; private set; }
        public int ReleaseCount { get; private set; }
        public int DisposeCount { get; private set; }
        public TaskCompletionSource? ReleaseEntered { get; init; }
        public Task? AllowRelease { get; init; }
        public async Task<JsonElement> CallAsync(string method, object parameters)
        {
            if (method == StandardMethods.Discover)
            {
                DiscoverCount++;
                if (PendingDiscovery) throw new PluginException("BackendDiscovering", "Fake inventory pending.");
                if (FailDiscovery) throw new IOException("Simulated discovery failure");
                return Wire.Element(Devices);
            }
            if (method == StandardMethods.Telemetry)
            {
                TelemetryCount++;
                return Telemetry;
            }
            if (method == StandardMethods.Release)
            {
                Released = true;
                ReleaseCount++;
                ReleaseEntered?.TrySetResult();
                if (AllowRelease is not null) await AllowRelease;
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
                    CommandApplied?.Invoke(LastCommand);
                    return Wire.Element(new ApplyResult("applied", "Fake readback"));
                }
                finally { Interlocked.Decrement(ref concurrentCalls); }
            }
            throw new InvalidOperationException("Unexpected fake method");
        }
        public ValueTask DisposeAsync() { Disposed = true; DisposeCount++; return ValueTask.CompletedTask; }
    }
}
