# Continuous cooling control

The HardwareMonitor plugin runs service fan profiles continuously. Applying a profile no longer starts a 30-minute / 1800-update session. Discovery reports `controlMode: "continuous"`; the session reports `mode: "continuous"`, with `expiresUtc` and `sessionLimitSeconds` set to null. A profile update preserves the current hardware baseline. Explicit release, plugin EOF, normal shutdown, cancellation during a write, or a sensor/control failure triggers restoration.

The legacy `--fan-session` diagnostic remains bounded to 30 minutes and 1800 samples. It is separate from service control.

Only the validated B650M AORUS ELITE AX ICE motherboard, IT8689E `/lpc/it8689e/0`, and radiator/case channels 0 and 3 are writable. Pump channel 4 stays read-only. Unrecognized hardware stays read-only. All adapters and safety logic live in plugins; no raw-register write fallback is used.

## Suspend and telemetry safety

The plugin registers Windows suspend/resume callbacks without requiring a UI message loop. A delivered suspend callback attempts to restore defaults before returning. Control stays paused until resume. A resume callback also restores if no suspend callback was delivered.

As a fallback, each active cycle checks elapsed monotonic time. A gap greater than 5 seconds triggers default restoration and register verification before the next sample or write. Sensor I/O taking more than 5 seconds discards its result and leaves defaults in place until a later fresh cycle. A control call taking more than 5 seconds triggers restoration as soon as it returns. These thresholds detect suspend and stalls; they do not make blocking driver calls interruptible.

Restart requires one unambiguous, finite CPU temperature and both controlled fan tachometers. Each control sample must belong to this device, have a timestamp no more than 3 seconds old, and not be in the future. CPU temperature must be 0–110°C. Missing or invalid data latches control read-only. Both fans must reach at least 200 RPM after an 8-second startup grace period. Startup and resume use freshly validated actual temperature, without a synthetic 100°C starting value.

Silent fixes radiator duty at 40% and case duty at 25% through normal temperature changes. Balanced and custom curves use an exponential filter with a 5-second time constant, 2°C deadband, 3-second sustained-rise confirmation, and slew limits of +5/-2 percentage points per second. Raw CPU temperature at 80°C bypasses every comfort filter and immediately requests full duty. Protection ends after 10 continuous seconds at or below 75°C, followed by gradual descent. Fixed duty is not fixed RPM and does not guarantee a particular noise level.

The service retains only the latest 64 telemetry values, no accumulating history. The lifetime update counter saturates at the maximum 64-bit value. Diagnostic file history retains its existing finite budget.

## Verified restoration and abnormal exits

Restoration attempts `SetDefault()` independently for both channels, then reads the LHM report and compares bank 0 registers `0x13`, `0x15`, `0x63`, `0x7B`, and `0x7F` with the captured baseline. Successful setter calls alone are insufficient. A missing/mismatched readback or setter exception reports `restore-failed`, blocks further writes, and retains the restoration obligation for a later release/disposal attempt.

Before the first manual write, the plugin creates and flushes a restoration-intent record under `%ProgramData%/LightOrder/FanControl/restoration-required.json`. Before creation, inspection or deletion it rejects reparse points, untrusted owners, writable state directories/files, and ancestor ACLs that permit untrusted replacement. New state directories receive an Administrators/SYSTEM ACL at creation. Unsafe pre-existing directories are refused rather than repaired. Only verified restoration clears it. Service, diagnostic and calibration controllers all check this record through the shared controller lease. An unresolved record blocks takeover; it must not be deleted automatically or treated as permission to reuse current register values as a baseline. After an abnormal exit, use a documented hardware recovery procedure to establish and verify firmware fan control before manually clearing the record. The file is evidence and an interlock, not an automatic recovery mechanism.

No independent watchdog is supplied. LHM 0.9.6 saves restoration values in the process that first calls `SetSoftware`; a fresh process cannot restore those values through `SetDefault` alone. A new process must not claim recovery by calling defaults without the original state. The plugin does not bypass this limitation with raw register writes.

Software cannot guarantee fan restoration after process termination, OS/driver failure, blocked hardware I/O, power loss, or hardware failure. Windows notifications may be omitted or callbacks may not finish before suspend. Time-gap recovery only runs when execution resumes. Persisted intent also depends on the OS/storage completing its flush. Baseline register equality verifies those observed registers at that instant; it does not prove physical fan operation or all firmware state. `crashRecoveryGuaranteed` remains false. Firmware thermal protection and hardware watchdogs remain outside this software guarantee.

## Fake-backend verification

Build `plugins/HardwareMonitor/HardwareMonitor.csproj`, then run the resulting plugin with `--service-self-test` and `--fan-self-test`. These commands use fake backends or pure policy/report parsing. They do not open a Computer instance or interact with hardware. Coverage includes more than 1800 continuous updates, profile updates, power signals, monotonic gaps, slow I/O, stale/invalid sensors, low RPM, release, cancellation, disposal, real RPC EOF/shutdown cleanup, restoration-intent persistence and register-verification failures. Native callback delivery and physical hardware restoration require separate authorized hardware acceptance.
