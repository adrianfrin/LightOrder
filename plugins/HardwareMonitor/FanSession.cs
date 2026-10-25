using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ControlCenter.Contracts;
using LibreHardwareMonitor.Hardware;

static class FanSession
{
    public static async Task<int> RunAsync(string statePath, int parentId, string profile)
    {
        if (profile is not ("balanced" or "quiet" or "custom")) return 2;
        FanProfile? custom = null;
        var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndependentControlCenter", "FanSessions"));
        var full = Path.GetFullPath(statePath);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var stop = full + ".stop";
        var historyPath = full + ".samples.jsonl";
        var sessionId = Guid.NewGuid().ToString("N");
        var computer = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true };
        IControl? radiator = null, chassis = null;
        FileStream? owner = null, history = null;
        ControllerLease? serviceLease = null;
        Process? parent = null;
        bool wroteRadiator = false, wroteCase = false, restored = false, restorationVerified = false;
        bool radiatorRestoreSucceeded = false, caseRestoreSucceeded = false;
        long? parentStartUtcTicks = null;
        string? error = null, restorationError = null, telemetryError = null, cleanupError = null, stopReason = null;
        object? sample = null;
        Dictionary<string, byte>? baselineRegisters = null, restoredRegisters = null;
        var sampleCount = 0;
        long historyBytes = 0;
        const long maximumHistoryBytes = 8 * 1024 * 1024;
        var startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();

        object State(string status) => new
        {
            status, timestamp = DateTimeOffset.UtcNow, profile, sample, error, restored,
            restorationVerified, stopReason, restorationError, telemetryError, cleanupError,
            baselineRegisters, restoredRegisters,
            restoreCommands = new { radiator = radiatorRestoreSucceeded, @case = caseRestoreSucceeded },
            sessionId, startedAt, parentId, parentStartUtcTicks, sampleCount, historyPath, historyBytes,
            maximumHistoryBytes, maximumSamples = FanSessionPolicy.MaximumSamples,
            maximumSessionMinutes = FanSessionPolicy.MaximumSessionMinutes, pollIntervalSeconds = 1,
            pumpWritten = false, crashRecoveryGuaranteed = false
        };

        async Task Save(string status)
        {
            // Same-directory rename prevents readers seeing a partially written JSON document.
            var temporary = full + "." + sessionId + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(State(status), Wire.Json));
                File.Move(temporary, full, overwrite: true);
            }
            finally { try { File.Delete(temporary); } catch { /* Preserve the original telemetry error. */ } }
        }
        async Task Append(string status, bool final = false)
        {
            if (history is null) throw new IOException("Session history is unavailable");
            // A final evidence record is reserved even when the sample budget is exhausted.
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(State(status), Wire.Json) + "\n");
            if (historyBytes + bytes.Length > maximumHistoryBytes - (final ? 0 : 32768)) throw new IOException("Session history byte limit reached");
            await history.WriteAsync(bytes);
            await history.FlushAsync();
            historyBytes += bytes.Length;
        }
        string? RequestedStop()
        {
            if (parent!.HasExited) return "parent-exited";
            if (File.Exists(stop)) return "stop-requested";
            if (timer.Elapsed >= FanSessionPolicy.MaximumDuration || sampleCount >= FanSessionPolicy.MaximumSamples) return "duration-limit";
            return null;
        }
        void Fail(string reason, string message)
        {
            stopReason = reason;
            throw new InvalidOperationException(message);
        }
        try
        {
            if (profile == "custom")
            {
                var config = full + ".profile.json";
                if (!File.Exists(config) || new FileInfo(config).Length > 16384) throw new InvalidDataException("自定义曲线文件缺失或过大");
                custom = JsonSerializer.Deserialize<FanProfile>(await File.ReadAllTextAsync(config), Wire.Json) ?? throw new InvalidDataException("曲线为空");
                custom.Validate();
            }
            serviceLease = new ControllerLease();
            owner = new FileStream(Path.Combine(Path.GetTempPath(), "IndependentControlCenter.Fan.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            parent = Process.GetProcessById(parentId);
            // Retain the OS handle for the whole session: PID reuse cannot replace this owner.
            _ = parent.SafeHandle;
            parentStartUtcTicks = parent.StartTime.ToUniversalTime().Ticks;
            if (parent.HasExited || parentStartUtcTicks > startedAt.UtcTicks ||
                (!string.Equals(parent.ProcessName, "IndependentControlCenter", StringComparison.OrdinalIgnoreCase) && !string.Equals(parent.ProcessName, "powershell", StringComparison.OrdinalIgnoreCase)))
                Fail("owner-invalid", "Unrecognized or exited session owner");
            // A reused state path cannot append unlimited history or overwrite earlier evidence.
            try
            {
                history = new FileStream(historyPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
                await Append("starting");
                await Save("starting");
            }
            catch (Exception e) { telemetryError = e.Message; Fail("telemetry-error", "Unable to initialize session evidence"); }

            if ((stopReason = RequestedStop()) is null)
            {
                computer.Open();
                var board = computer.Hardware.Single(h => h.HardwareType == HardwareType.Motherboard);
                if (!board.Name.Contains("B650M AORUS ELITE AX ICE", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unvalidated motherboard");
                var chip = board.SubHardware.Single(h => h.Identifier.ToString() == "/lpc/it8689e/0");
                var cpu = computer.Hardware.Single(h => h.HardwareType == HardwareType.Cpu);
                chip.Update(); cpu.Update();
                baselineRegisters = FanSessionPolicy.ReadRegisters(computer.GetReport());
                if (!FanSessionPolicy.ValidBaseline(baselineRegisters)) throw new InvalidOperationException("Fan baseline not verified");
                radiator = chip.Sensors.Single(s => s.Identifier.ToString() == "/lpc/it8689e/0/control/0").Control ?? throw new InvalidOperationException("Radiator control unavailable");
                chassis = chip.Sensors.Single(s => s.Identifier.ToString() == "/lpc/it8689e/0/control/3").Control ?? throw new InvalidOperationException("Case control unavailable");
                var previousTemperature = 100f;
                var controlling = Stopwatch.StartNew();
                while ((stopReason = RequestedStop()) is null)
                {
                    chip.Update(); cpu.Update();
                    var raw = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Tctl"))?.Value;
                    if (!FanSessionPolicy.ValidTemperature(raw)) Fail("temperature-invalid", "CPU temperature unavailable or out of range; restoring prior control");
                    var fans = chip.Sensors.Where(s => s.SensorType == SensorType.Fan)
                        .Select(s => new { id = s.Identifier.ToString(), rpm = FanSessionPolicy.ValidRpm(s.Value) ? s.Value : null, valid = FanSessionPolicy.ValidRpm(s.Value) }).ToArray();
                    var rpm0 = fans.SingleOrDefault(s => s.id == "/lpc/it8689e/0/fan/0")?.rpm;
                    var rpm3 = fans.SingleOrDefault(s => s.id == "/lpc/it8689e/0/fan/3")?.rpm;
                    if (!FanSessionPolicy.ValidRpm(rpm0) || !FanSessionPolicy.ValidRpm(rpm3)) Fail("tachometer-invalid", "Controlled fan tachometer missing or nonfinite; restoring prior control");
                    if (controlling.Elapsed >= TimeSpan.FromSeconds(8) && (!FanSessionPolicy.RunningRpm(rpm0) || !FanSessionPolicy.RunningRpm(rpm3))) Fail("tachometer-low", "Controlled fan tachometer below 200 RPM; restoring prior control");
                    var temperature = Math.Max(raw!.Value, previousTemperature - 2);
                    var duties = custom is not null ? custom.Evaluate(temperature) :
                        (FanSessionPolicy.Next(raw.Value, previousTemperature, profile).Duty, FanSessionPolicy.Next(raw.Value, previousTemperature, profile).Duty);
                    previousTemperature = temperature;
                    // Recheck after sensor I/O before either control write.
                    if ((stopReason = RequestedStop()) is not null) break;
                    wroteRadiator = true; radiator.SetSoftware(duties.Item1);
                    wroteCase = true; chassis.SetSoftware(duties.Item2);
                    sample = new { timestamp = DateTimeOffset.UtcNow, elapsedSeconds = timer.Elapsed.TotalSeconds, cpu = raw, controlTemperature = temperature, radiatorDuty = duties.Item1, caseDuty = duties.Item2, fans };
                    sampleCount++;
                    try { await Append("active"); await Save("active"); }
                    catch (Exception e) { telemetryError = e.Message; Fail("telemetry-error", "Session evidence write failed; restoring prior control"); }
                    var remaining = FanSessionPolicy.MaximumDuration - timer.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
                }
            }
        }
        catch (Exception e) { error = e.Message; stopReason ??= "session-error"; }
        finally
        {
            // No telemetry operation precedes these independent restoration attempts.
            if (wroteRadiator || wroteCase)
            {
                var failures = new List<string>();
                try { radiator!.SetDefault(); radiatorRestoreSucceeded = true; } catch (Exception e) { failures.Add("radiator: " + e.Message); }
                try { chassis!.SetDefault(); caseRestoreSucceeded = true; } catch (Exception e) { failures.Add("case: " + e.Message); }
                try
                {
                    restoredRegisters = FanSessionPolicy.ReadRegisters(computer.GetReport());
                    restorationVerified = baselineRegisters is not null && FanSessionPolicy.Restored(baselineRegisters, restoredRegisters);
                    if (!restorationVerified) failures.Add("Bank 0 register readback differs from the captured baseline");
                }
                catch (Exception e) { failures.Add("Register readback unavailable: " + e.Message); }
                restored = radiatorRestoreSucceeded && caseRestoreSucceeded && restorationVerified;
                if (failures.Count > 0) restorationError = string.Join("; ", failures);
                if (!restored) error = string.Join("; ", new[] { error, "Prior-control restoration failed or could not be verified" }.Where(s => s is not null));
            }
            try { computer.Close(); } catch (Exception e) { cleanupError = e.Message; error ??= "Hardware monitor cleanup failed"; }
            // Keep the exclusive controller lock until readback and close have completed.
            try { owner?.Dispose(); } catch (Exception e) { cleanupError ??= e.Message; }
            serviceLease?.Dispose();
            parent?.Dispose();
            var status = error is null ? "released" : "failed";
            try { if (history is not null) await Append(status, final: true); }
            catch (Exception e) { telemetryError ??= e.Message; }
            try { if (history is not null) await history.DisposeAsync(); }
            catch (Exception e) { telemetryError ??= e.Message; }
            try { await Save(status); }
            catch (Exception e) { telemetryError ??= e.Message; }
            // If disk output is unavailable, the process still reports restoration separately.
            try { Console.WriteLine(JsonSerializer.Serialize(State(status), Wire.Json)); } catch { }
        }
        return error is null && telemetryError is null ? 0 : 1;
    }
}
