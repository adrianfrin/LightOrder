using System.Text.Json;
using ControlCenter.Contracts;
using ControlCenter.Core;

namespace LightOrder.Service;

public interface IServicePlugin : IAsyncDisposable
{
    PluginDescriptor Descriptor { get; }
    Task<JsonElement> CallAsync(string method, object parameters);
}

public sealed class ProcessPlugin(PluginDescriptor descriptor, PluginClient client) : IServicePlugin
{
    public PluginDescriptor Descriptor => descriptor;
    public Task<JsonElement> CallAsync(string method, object parameters)
    {
        if (!descriptor.Methods.Contains(method, StringComparer.Ordinal))
            throw new PluginException("MethodNotAllowed", "Method is not in the plugin manifest whitelist.");
        return client.CallAsync(method, parameters, timeoutSeconds: 10);
    }
    public ValueTask DisposeAsync() => client.DisposeAsync();
}

public sealed class DeviceRouter
{
    private readonly Dictionary<string, (IServicePlugin Plugin, StandardDevice Local)> routes = new(StringComparer.Ordinal);
    public StandardDevice[] Devices => routes.Select(p => LightingEffects.Advertise(p.Value.Local with { Id = p.Key })).ToArray();
    public void Clear() => routes.Clear();
    public void Remove(IServicePlugin plugin)
    {
        foreach (var key in routes.Where(p => ReferenceEquals(p.Value.Plugin, plugin)).Select(p => p.Key).ToArray()) routes.Remove(key);
    }
    public void Replace(IServicePlugin plugin, StandardDevice[] devices)
    {
        if (devices is null || devices.Length > 256) throw new InvalidDataException("Invalid discovery result or too many devices.");
        var localIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (device is null || !ValidDeviceId(device.Id) || !localIds.Add(device.Id) ||
                string.IsNullOrWhiteSpace(device.Name) || device.Name.Length > 256 ||
                string.IsNullOrWhiteSpace(device.Kind) || device.Kind.Length > 128 ||
                device.Status is null || device.Status.Length > 256 || device.Detail is null || device.Detail.Length > 4096 ||
                device.Capabilities is null || device.Capabilities.Length > 64 ||
                device.Capabilities.Any(c => !ManifestValidation.ValidId(c)) ||
                device.Capabilities.Distinct(StringComparer.Ordinal).Count() != device.Capabilities.Length ||
                JsonSerializer.SerializeToUtf8Bytes(device, Wire.Json).Length > 128 * 1024)
                throw new InvalidDataException("Plugin discovery returned an invalid device descriptor.");
        }
        Remove(plugin);
        var currentBytes = JsonSerializer.SerializeToUtf8Bytes(Devices, Wire.Json).Length;
        var nextBytes = JsonSerializer.SerializeToUtf8Bytes(devices.Select(d => d with { Id = plugin.Descriptor.Id + "::" + d.Id }), Wire.Json).Length;
        if (routes.Count + devices.Length > 1024 || currentBytes + nextBytes > 384 * 1024)
            throw new InvalidDataException("Discovery exceeds the service device budget.");
        foreach (var device in devices) routes.Add(plugin.Descriptor.Id + "::" + device.Id, (plugin, device));
    }
    public static bool ValidDeviceId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 &&
        !id.Contains("::", StringComparison.Ordinal) && !id.Any(char.IsControl);

    public (IServicePlugin Plugin, DeviceCommand Command, string Method) Resolve(DeviceCommand command)
    {
        if (command is null || command.DeviceId is null || !routes.TryGetValue(command.DeviceId, out var route))
            throw new PluginException("DeviceUnavailable", "Device was not discovered or its plugin is disconnected. Refresh discovery.");
        if (command.Capability is null || !(command.Capability == StandardCapabilities.LightingEffect && LightingEffects.CanAnimate(route.Local)) && !route.Local.Capabilities.Contains(command.Capability, StringComparer.Ordinal))
            throw new PluginException("CapabilityNotSupported", "The discovered device does not advertise this capability.");
        if (command.Parameters.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(command.Parameters, Wire.Json).Length > 65536)
            throw new PluginException("InvalidArgument", "Device parameters must be an object of at most 64 KiB.");
        var method = StandardMethods.Execute;
        if (!route.Plugin.Descriptor.Methods.Contains(method, StringComparer.Ordinal))
            throw new PluginException("MethodNotAllowed", "Plugin does not permit this method.");
        return (route.Plugin, command with { DeviceId = route.Local.Id }, method);
    }

    public JsonElement[] ValidateTelemetry(IServicePlugin plugin, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > 1024)
            throw new InvalidDataException("Telemetry must be a bounded array.");
        var samples = new List<JsonElement>();
        foreach (var sample in result.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object || !sample.TryGetProperty("deviceId", out var device) || device.ValueKind != JsonValueKind.String ||
                !routes.TryGetValue(plugin.Descriptor.Id + "::" + device.GetString(), out var route) || !ReferenceEquals(route.Plugin, plugin) ||
                !sample.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString()!.Length > 256 ||
                !sample.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString()!.Length > 128 ||
                !sample.TryGetProperty("unit", out var unit) || unit.ValueKind != JsonValueKind.String || unit.GetString()!.Length > 64 ||
                !sample.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out _) ||
                !sample.TryGetProperty("value", out var value) || (value.ValueKind != JsonValueKind.Null &&
                    (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))))
                throw new InvalidDataException("Plugin telemetry contains invalid data or an undiscovered device.");
            samples.Add(Wire.Element(new { deviceId = plugin.Descriptor.Id + "::" + device.GetString(),
                name = name.GetString(), type = type.GetString(), value = value.Clone(), unit = unit.GetString(), timestamp = timestamp.GetString() }));
        }
        return samples.ToArray();
    }
}

public sealed class ServiceRuntime(ProfileStore profiles) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<IServicePlugin> plugins = [];
    private readonly List<PluginDescriptor> descriptors = [];
    private readonly HashSet<IServicePlugin> faulted = [];
    private readonly HashSet<IServicePlugin> discoveryPending = [];
    // Keep tracking a fan plugin even if a later descriptor becomes read-only and drops fan.profile.
    private readonly HashSet<IServicePlugin> sessionDescriptors = [];
    private DateTimeOffset nextDiscoveryRetry;
    private readonly DeviceRouter router = new();
    private readonly Queue<string> diagnostics = new();
    private bool stopping;
    private bool disposed;
    private readonly LightingEffects effects = new();
    private AutomationEngine? automation;
    private CancellationTokenSource? controlLifetime;
    private Task? controlLoop;

    public void ConfigureAutomation(AutomationEngine engine) => automation = engine;

    public void ValidateCommand(DeviceCommand command)
    {
        _ = router.Resolve(command);
        if (command.Capability == StandardCapabilities.LightingEffect)
            _ = LightingAnimation.Parse(command.Parameters, DateTimeOffset.UtcNow);
        else if (command.Capability == StandardCapabilities.FanProfile)
            Wire.Read<FanProfile>(command.Parameters).Validate();
        else if (command.Capability == StandardCapabilities.LightingStatic)
        {
            var p = command.Parameters;
            if (!p.TryGetProperty("color", out var color) || color.ValueKind != JsonValueKind.String ||
                !System.Text.RegularExpressions.Regex.IsMatch(color.GetString()!, "^#[0-9a-fA-F]{6}$") ||
                !p.TryGetProperty("brightness", out var brightness) || !brightness.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 1)
                throw new PluginException("InvalidArgument", "Invalid static color or brightness.");
        }
        else if (command.Capability == StandardCapabilities.WindowsPowerScheme)
        {
            if (!command.Parameters.TryGetProperty("schemeId", out var scheme) || scheme.ValueKind != JsonValueKind.String || !Guid.TryParse(scheme.GetString(), out _))
                throw new PluginException("InvalidArgument", "Power scheme ID must be a GUID.");
        }
        else if (command.Capability == StandardCapabilities.ProcessPolicy)
        {
            var p = command.Parameters;
            if (!p.TryGetProperty("priority", out var priority) || priority.ValueKind != JsonValueKind.String ||
                priority.GetString() is not ("Idle" or "BelowNormal" or "Normal" or "AboveNormal" or "High"))
                throw new PluginException("InvalidArgument", "Invalid process priority.");
            if (p.TryGetProperty("pid", out var pid) && (!pid.TryGetInt32(out var id) || id <= 0) ||
                p.TryGetProperty("startTimeUtc", out var start) && (start.ValueKind != JsonValueKind.String || !start.TryGetDateTimeOffset(out _)))
                throw new PluginException("InvalidArgument", "Invalid process identity.");
            if (p.TryGetProperty("affinityMask", out var mask) && (mask.ValueKind != JsonValueKind.String ||
                !System.Text.RegularExpressions.Regex.IsMatch(mask.GetString()!, "^(0x)?[0-9a-fA-F]{1,16}$") ||
                mask.GetString()!.Replace("0x", "", StringComparison.Ordinal).All(c => c == '0')))
                throw new PluginException("InvalidArgument", "Invalid CPU affinity mask.");
        }
    }

    public Task<ApplyResult> ExecuteAutomatedAsync(DeviceCommand command) => ExecuteCommandAsync(command, 0);

    public void StartControlLoop(CancellationToken token)
    {
        if (controlLoop is not null) throw new InvalidOperationException("Control loop already started.");
        controlLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        controlLoop = RunControlLoopAsync(controlLifetime.Token);
    }

    private async Task RunControlLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var nextAutomation = DateTimeOffset.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await gate.WaitAsync(token);
                try
                {
                    if (stopping) return;
                    await effects.TickAsync(command => ExecutePluginAsync(command, 0), AddDiagnostic);
                    var now = DateTimeOffset.UtcNow;
                    if (discoveryPending.Count > 0 && now >= nextDiscoveryRetry)
                    {
                        nextDiscoveryRetry = now.AddSeconds(15);
                        foreach (var plugin in discoveryPending.ToArray()) await DiscoverPluginAsync(plugin);
                    }
                    if (automation is not null && now >= nextAutomation)
                    {
                        nextAutomation = now.AddSeconds(1);
                        try { await automation.TickAsync(now, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception error) { AddDiagnostic("Automation tick failed: " + Category(error)); }
                    }
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public string[] Diagnostics
    {
        get { lock (diagnostics) return diagnostics.ToArray(); }
    }

    public void AddDiagnostic(string text)
    {
        lock (diagnostics)
        {
            diagnostics.Enqueue($"{DateTimeOffset.UtcNow:O} {text[..Math.Min(text.Length, 512)]}");
            while (diagnostics.Count > 64) diagnostics.Dequeue();
        }
    }

    public async Task StartAsync(ServiceSettings settings, CancellationToken token)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "plugins");
        // Includes the host executable, assemblies and runtime dependencies, not just manifests.
        ServiceSecurity.ValidateTree(AppContext.BaseDirectory);
        var manifests = ManifestValidation.Load(root, settings);
        descriptors.AddRange(manifests.Select(p => p.Descriptor));
        foreach (var (descriptor, path) in manifests.Where(p => p.Descriptor.Enabled))
        {
            token.ThrowIfCancellationRequested();
            try { plugins.Add(new ProcessPlugin(descriptor, await PluginClient.StartAsync(path))); }
            catch (Exception e)
            {
                var index = descriptors.FindIndex(d => d.Id == descriptor.Id);
                descriptors[index] = descriptor with { Enabled = false };
                AddDiagnostic($"Plugin {descriptor.Id} failed to start: {Category(e)}.");
            }
        }
        await RefreshAsync(token);
        await RestoreStartupPreferencesAsync(token);
    }

    private async Task RestoreStartupPreferencesAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            JsonElement saved;
            try { saved = profiles.Read(); }
            catch (Exception error) { AddDiagnostic("Startup preferences unavailable: " + Category(error)); return; }
            bool Enabled(string key) => saved.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
            if (Enabled("restoreLightingOnStartup") &&
                !(saved.TryGetProperty("pendingLighting", out var pending) && pending.ValueKind is not JsonValueKind.Null) &&
                saved.TryGetProperty("appliedLighting", out var lighting) && lighting.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in lighting.EnumerateObject().Take(16))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var effect = item.Value.GetProperty("effect").GetString();
                        var capability = effect switch
                        {
                            "off" => StandardCapabilities.LightingOff,
                            "static" => StandardCapabilities.LightingStatic,
                            "breathing" or "rainbow" => StandardCapabilities.LightingEffect,
                            _ => throw new PluginException("InvalidArgument", "Unknown saved lighting effect.")
                        };
                        var parameters = new Dictionary<string, object?>
                        {
                            ["color"] = item.Value.GetProperty("color").GetString(),
                            ["brightness"] = item.Value.GetProperty("brightness").GetDouble(),
                            ["operationId"] = Guid.NewGuid().ToString("N")
                        };
                        if (capability == StandardCapabilities.LightingEffect)
                        {
                            parameters["effect"] = effect;
                            parameters["periodSeconds"] = item.Value.TryGetProperty("periodSeconds", out var period) ? period.GetDouble() : 6;
                        }
                        await ExecuteCommandAsync(new DeviceCommand(item.Name, capability, Wire.Element(parameters)), 0);
                        AddDiagnostic("Startup lighting restored for " + item.Name + "; physical readback unavailable.");
                    }
                    catch (Exception error) { AddDiagnostic("Startup lighting skipped for " + item.Name + ": " + Category(error) + ". No retry."); }
                }
            }
            if (Enabled("restoreCoolingOnStartup") && saved.TryGetProperty("fanProfiles", out var fans) && fans.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in fans.EnumerateObject().Take(16))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await ExecuteCommandAsync(new DeviceCommand(item.Name, StandardCapabilities.FanProfile, item.Value.Clone()), 0);
                        AddDiagnostic("Startup cooling profile requested for " + item.Name + ".");
                    }
                    catch (Exception error) { AddDiagnostic("Startup cooling skipped for " + item.Name + ": " + Category(error) + ". No retry."); }
                }
            }
        }
        finally { gate.Release(); }
    }

    // Also used by the hardware-free test executable. No process is launched here.
    public void AddTestPlugin(IServicePlugin plugin)
    {
        if (plugins.Any(p => p.Descriptor.Id == plugin.Descriptor.Id)) throw new InvalidDataException("Duplicate plugin.");
        plugins.Add(plugin);
        descriptors.Add(plugin.Descriptor);
    }

    public async Task<object> HandleAsync(string method, JsonElement parameters, long correlationId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (stopping) throw new PluginException("ServiceStopping", "The service is restoring controls and stopping.");
            switch (method)
            {
                case StandardMethods.Refresh:
                    await DiscoverAsync(token);
                    return await SnapshotAsync(token, refreshSessionDescriptors: false);
                case StandardMethods.Snapshot:
                    return await SnapshotAsync(token);
                case StandardMethods.Plugins:
                    return descriptors.Select(d => faulted.Any(p => p.Descriptor.Id == d.Id) ? d with { Enabled = false } : d).ToArray();
                case StandardMethods.Apply:
                    DeviceCommand command;
                    try { command = parameters.Deserialize<DeviceCommand>(ServiceSettings.StrictJson) ?? throw new JsonException(); }
                    catch (JsonException) { throw new PluginException("InvalidArgument", "app.execute requires a DeviceCommand."); }
                    return await ExecuteCommandAsync(command, correlationId);
                case StandardMethods.AutomationRead:
                    if (automation is null) throw new PluginException("AutomationUnavailable", "Automation has not been configured.");
                    var document = automation.Read();
                    return JsonSerializer.SerializeToElement(document, AutomationValidation.Json);
                case StandardMethods.AutomationWrite:
                    if (automation is null) throw new PluginException("AutomationUnavailable", "Automation has not been configured.");
                    return automation.Write(parameters);
                case StandardMethods.ProfilesRead: return profiles.Read();
                case StandardMethods.ProfilesWrite: return profiles.Write(parameters);
                default: throw new PluginException("MethodNotAllowed", "Unknown or forbidden service method.");
            }
        }
        finally { gate.Release(); }
    }

    private async Task<ApplyResult> ExecuteCommandAsync(DeviceCommand command, long correlationId)
    {
        ValidateCommand(command);
        if (command.Capability == StandardCapabilities.LightingEffect)
            return await effects.StartAsync(command, frame => ExecutePluginAsync(frame, correlationId));
        if (command.Capability is StandardCapabilities.LightingStatic or StandardCapabilities.LightingOff or StandardCapabilities.Release)
            effects.Stop(command.DeviceId);
        return await ExecutePluginAsync(command, correlationId);
    }

    private async Task<ApplyResult> ExecutePluginAsync(DeviceCommand command, long correlationId)
    {
        var route = router.Resolve(command);
        try
        {
            var raw = await route.Plugin.CallAsync(route.Method, route.Command);
            var applied = Wire.Read<ApplyResult>(raw);
            if (string.IsNullOrWhiteSpace(applied.Status) || applied.Status.Length > 128 || applied.Detail is null || applied.Detail.Length > 4096)
                throw new InvalidDataException("Invalid command result.");
            // Animation frames are frequent; diagnostics record user/rule commands and failures only.
            if (correlationId != 0) AddDiagnostic($"Command {correlationId}: {route.Plugin.Descriptor.Id} {command.Capability} {applied.Status}.");
            return applied;
        }
        catch (Exception error)
        {
            if (IsTransportFailure(error)) Fault(route.Plugin, error);
            AddDiagnostic($"Command {correlationId}: {route.Plugin.Descriptor.Id} {Category(error)}. No retry.");
            throw ToPublicError(error);
        }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await DiscoverAsync(token); }
        finally { gate.Release(); }
    }
    private async Task DiscoverAsync(CancellationToken token)
    {
        router.Clear();
        foreach (var plugin in plugins.Where(p => !faulted.Contains(p)))
        {
            token.ThrowIfCancellationRequested();
            await DiscoverPluginAsync(plugin);
        }
    }
    private async Task DiscoverPluginAsync(IServicePlugin plugin)
    {
        try
        {
            var devices = Wire.Read<StandardDevice[]>(await plugin.CallAsync(StandardMethods.Discover, new { }));
            router.Replace(plugin, devices);
            if (devices.Any(d => d.Capabilities.Contains(StandardCapabilities.FanProfile, StringComparer.Ordinal)))
                sessionDescriptors.Add(plugin);
            if (discoveryPending.Remove(plugin)) AddDiagnostic("Device discovery ready for " + plugin.Descriptor.Id + ". No commands replayed.");
        }
        catch (PluginException error) when (error.Code == "BackendDiscovering")
        {
            router.Remove(plugin);
            if (discoveryPending.Add(plugin)) AddDiagnostic("Device discovery pending for " + plugin.Descriptor.Id + "; read-only retry every 15 seconds.");
        }
        catch (Exception error) { discoveryPending.Remove(plugin); Fault(plugin, error); }
    }
    private async Task<ServiceSnapshot> SnapshotAsync(CancellationToken token, bool refreshSessionDescriptors = true)
    {
        var telemetry = new List<JsonElement>();
        var telemetryBytes = 0;
        foreach (var plugin in plugins.Where(p => !faulted.Contains(p) && !discoveryPending.Contains(p)))
        {
            token.ThrowIfCancellationRequested();
            // Fan session state lives in Discover metadata; other plugins may perform expensive scans.
            if (refreshSessionDescriptors && sessionDescriptors.Contains(plugin))
            {
                await DiscoverPluginAsync(plugin);
                if (faulted.Contains(plugin) || discoveryPending.Contains(plugin)) continue;
            }
            try
            {
                var samples = router.ValidateTelemetry(plugin, await plugin.CallAsync(StandardMethods.Telemetry, new { }));
                var size = JsonSerializer.SerializeToUtf8Bytes(samples, Wire.Json).Length;
                if (telemetryBytes + size > 384 * 1024) throw new InvalidDataException("Telemetry snapshot exceeds the service budget.");
                telemetry.AddRange(samples);
                telemetryBytes += size;
            }
            catch (Exception e) { Fault(plugin, e); }
        }
        string[] messages;
        lock (diagnostics) messages = diagnostics.Concat(automation?.Diagnostics ?? []).TakeLast(64).ToArray();
        return new ServiceSnapshot("1.0.0", router.Devices.Select(effects.Describe).ToArray(), telemetry.ToArray(), messages);
    }
    private void Fault(IServicePlugin plugin, Exception e)
    {
        faulted.Add(plugin);
        router.Remove(plugin);
        AddDiagnostic($"Plugin {plugin.Descriptor.Id} disconnected: {Category(e)}. Restart service after checking the device; restoration may be unverified.");
    }
    private static string Category(Exception e) => e is PluginException p ? p.Code : e.GetType().Name;
    private static bool IsTransportFailure(Exception e) => e is not PluginException ||
        e is PluginException { Code: "TimeoutOutcomeUnknown" };
    private static PluginException ToPublicError(Exception e) => e is PluginException p
        ? new PluginException(p.Code, p.Code == "TimeoutOutcomeUnknown" ? "Plugin timed out. Outcome unknown; no retry. Check the device before restarting the service." : "Plugin rejected the request: " + p.Code)
        : new PluginException("PluginUnavailable", "Plugin communication failed. Outcome may be unknown; no retry. Refresh the device list.");

    public async ValueTask DisposeAsync()
    {
        if (controlLifetime is not null) await controlLifetime.CancelAsync();
        if (controlLoop is not null)
        {
            try { await controlLoop; }
            catch (Exception error) { AddDiagnostic("Control loop stopped: " + Category(error) + ". Continuing restoration."); }
        }
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            stopping = true;
            await effects.FinishAsync(command => ExecutePluginAsync(command, 0), AddDiagnostic);
            controlLifetime?.Dispose();
            // Release every worker even after an earlier worker fails. No automatic command replay.
            foreach (var plugin in plugins.AsEnumerable().Reverse())
            {
                try
                {
                    var result = Wire.Read<ApplyResult>(await plugin.CallAsync(StandardMethods.Release, new { }));
                    AddDiagnostic($"Release {plugin.Descriptor.Id}: {result.Status}; verification is reported by the plugin.");
                }
                catch (Exception e) { AddDiagnostic($"Release {plugin.Descriptor.Id} unverified: {Category(e)}."); }
                finally
                {
                    try { await plugin.DisposeAsync(); }
                    catch (Exception e) { AddDiagnostic($"Cleanup {plugin.Descriptor.Id}: {Category(e)}."); }
                }
            }
            router.Clear();
            plugins.Clear();
            disposed = true;
        }
        finally { gate.Release(); }
    }
}
