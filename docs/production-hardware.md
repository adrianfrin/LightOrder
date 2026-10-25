# Production monitor and Windows policy adapters

The two adapters implement protocol major 1: `device.discover`, `device.execute`, `telemetry.read`, and `device.release`. Discovery returns `StandardDevice[]`; its existing fields remain compatible with `DeviceInfo[]`. Commands use `{deviceId, capability, parameters}`. All identifiers below are plugin-local; the service adds `pluginId::` for public IDs.

## HardwareMonitor lifecycle and limits

Run `ControlCenter.HardwareMonitor.exe` with no arguments as a long-lived child of the privileged service. `plugin.hello` and construction do not open hardware. Discovery, telemetry, or a valid fan command opens LibreHardwareMonitor lazily. Sampling runs once per second. A single asynchronous gate serializes every hardware read, control write, release, and disposal operation. There is no per-action elevation, shell invocation, temporary profile file, or child controller in this production path.

The only control allowlist remains the previously validated B650M AORUS ELITE AX ICE board, IT8689E instance 0, radiator control 0 and case control 3. The plugin uses LibreHardwareMonitor `SetSoftware` and `SetDefault`; it does not add raw register writes. `FanSessionPolicy` captures and compares exactly the existing five relevant registers. Both channels receive independent restore attempts, followed by register verification. A failed readback returns failure, retains restore responsibility, and disables further control. Disposal retries restoration before closing the monitor. A global named controller lease prevents overlap with the retained CLI session/calibration code across Windows sessions; inaccessible or occupied leases refuse control.

Unknown boards, unavailable controls, invalid baseline reports, and missing permissions expose a read-only/unavailable device instead of guessing a mapping. An unavailable monitor returns an empty telemetry array. Hardware failures during sampling clear cached values and stop the polling loop; restart the plugin to reconnect read-only. No pump control is obtained or written. On the validated board only, `pump.rpm` reads fan channel 4 based on the existing documented historical wiring and measured RPM evidence in `docs/fans.md`; that evidence does not claim visual physical-header confirmation. Other boards retain raw sensor identifiers without inferring channel roles.

Sessions last **at most 30 minutes or 1,800 control updates**. Updating a profile during an active session retains the first baseline and original deadline. The controller restores at expiration; it does not renew itself. An explicit new command may start a new bounded session after verified release. This migration does not establish evidence for indefinite operation. Metadata exposes the bound, start/expiry times, status, and restoration result.

Temperature rises take effect immediately; temperature falls are limited to 2°C per control update. Startup begins conservatively from 100°C. Shared `FanProfile` enforces radiator/case floors of 40%/25%, monotonically increasing curves ending at 80°C/100%, and a separate thermal floor in manual mode. Missing/nonfinite CPU temperature or controlled-fan tachometer restores control. After eight seconds, either controlled fan below 200 RPM triggers restoration. A final deadline check precedes control writes.

Normal `device.release`, EOF, and cooperative plugin shutdown restore control. Hard worker termination, power loss, OS failure, hung native I/O, and driver failure cannot guarantee restoration. Service shutdown must allow the plugin to finish its release response before forcing termination; `plugin.shutdown` acknowledgment alone precedes disposal. Do not infer successful restoration from process exit alone.

## Canonical monitor metadata

Device `hardware-monitor` advertises `sensor.temperature`, `sensor.fanRpm`, and `device.release`. It adds `fan.profile` only when the board/control/baseline checks permit writes and no control fault has occurred.

- `schemaVersion: 1`, `readOnly`, `supportsRestore: true`.
- `channels`: `{id, name, readOnly, minPercent, maxPercent, telemetryName}` for `radiator`, `case`, and `pump`. The pump is always read-only and its `telemetryName` is `pump.rpm`; that sample is emitted only on the validated board.
- `profile`: shared `FanProfile` directly; `device.execute` parameters are that profile, without an extra wrapper.
- `sessionLimitSeconds: 1800`, `session: {active, startedUtc, expiresUtc, status, restorationVerified, sampleCount, sessionLimitSeconds}`.
- `parameterSchema`: shared model name, allowed modes, curve node limits, and terminal point constraints.
- `pollIntervalSeconds: 1`, `telemetryLimitSamples: 64`, `crashRecoveryGuaranteed: false`.

Telemetry is an array of `{deviceId,name,type,value,unit,timestamp}`. Canonical names are `cpu.temperature`, `cpu.load`, `radiator.rpm`, `case.rpm`, and `pump.rpm`. Types are `temperature`, `load`, `fanRpm`; units are `°C`, `%`, `RPM`. Other sensors use their actual LHM identifier as name. Values are finite numbers or null. Only the latest snapshot, capped at 64 entries, is retained in the plugin; the service/UI may maintain separately bounded chart history. A sample retains its capture timestamp, so readers can detect stale values.

Explicit restoration through the generic service route uses `device.execute` with capability `device.release` and parameters `{}`. The raw `device.release` method remains supported.

## WindowsPolicy and system information

Run `ControlCenter.WindowsPolicy.exe --service` for the installed service. This selects `%ProgramData%\LightOrder\WindowsPolicy\recovery-v1.json`. **The installer must protect the LightOrder state root against non-administrator writes before starting this privileged process.** The no-argument CLI retains its original per-user journal path. Journals are never automatically replayed on startup; the existing explicit recovery methods and conditional ownership checks remain in force.

Device `windows-policy` advertises `power.windowsScheme`, `process.policy`, and `device.release`, alongside its existing legacy capabilities. Standard execute delegates to the existing transaction/readback/rollback functions. Power parameters are `{schemeId}`; process parameters are `{pid,startTimeUtc,priority,affinityMask?}`. Process identity remains pinned by native handle and creation time. RealTime and unsupported multi-group affinity writes remain rejected. Release restores only properties that still match this plugin's writes and preserves external changes.

Metadata uses `schemaVersion:1`, `supportsRestore:true`, `recoveryPending`, `diagnostics`, `parameterSchemas`, plus:

- `power: {schemes:[{id,name,isActive}],activeSchemeId,readbackVerified}`.
- `process: {processes:[{pid,startTimeUtc,name,priority,affinityMask,systemAffinityMask,status,errorCode,detail,readbackVerified}],processorGroupCount,affinityWriteSupported,allowedPriorities,truncated,totalProcessCount}`.

Standard discovery caps process rows at 256 and reports truncation. The legacy `process.list` method retains its full enumeration. `telemetry.read` returns an empty array because policy state is represented in metadata and command readback.

Logical device `system-info` advertises read-only `system.info`. Its metadata is `{schemaVersion:1,readOnly:true,components:[...],diagnostics:[...]}`. Components contain `kind`, product/model `name`, and applicable `manufacturer`, `capacityBytes`, or OS `version`. Fixed System.Management CIM projections read CPU, board, physical memory, GPU, disk, and OS information. They never request serial numbers, UUIDs, machine names, user names, or command lines. Query failures emit only a component category plus “information unavailable”. Each category is capped at 16 components; queries use a two-second enumeration timeout. System information is cached for the plugin lifetime. It is not a writable capability.

## Compatibility and verification

The old HardwareMonitor CLI flags `--snapshot`, `--fan-session`, `--fan-test`, and `--fan-self-test` remain. The older session file/parent checks remain confined to that explicit legacy CLI. WindowsPolicy old methods and `--self-test` remain. Shared contracts and host/UI code were not changed by this adapter migration.

Verification performed during this migration uses builds and hardware-free tests only:

```text
dotnet build plugins/HardwareMonitor/HardwareMonitor.csproj -c Release
dotnet build plugins/WindowsPolicy/ControlCenter.WindowsPolicy.csproj -c Release
dotnet plugins/HardwareMonitor/bin/Release/net10.0-windows/ControlCenter.HardwareMonitor.dll --fan-self-test
dotnet plugins/HardwareMonitor/bin/Release/net10.0-windows/ControlCenter.HardwareMonitor.dll --service-self-test
dotnet plugins/WindowsPolicy/bin/Release/net10.0-windows/ControlCenter.WindowsPolicy.dll --self-test
```

The original fan policy tests check register parsing and restoration comparison. The service tests use a fake backend and clock for lazy opening, protocol compatibility, invalid profile rejection, conservative startup, unchanged session deadline on updates, expiration, release, disposal, partial write/sensor failures, and failed restoration. Windows tests use memory-only policy/journal/system-information fakes and retain original ownership, PID reuse, rollback, stale-journal, and power checks while adding standard adapter coverage.

No real hardware, CIM inventory query, installed service, power scheme, process priority, or affinity changes were performed for this migration. Actual service lifecycle, physical channel response, driver behavior, and live register restoration require separate authorized acceptance. Compilation and fake tests are not that hardware evidence.
