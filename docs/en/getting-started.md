# Getting started

[简体中文](../zh-CN/getting-started.md) · [Home](../../README.md)

## Install and launch

1. Use Windows 11 x64 and install [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). Setup checks for it; it does not silently download it.
2. Download `LightOrder-win-x64.exe` from [Releases](https://github.com/adrianfrin/LightOrder/releases). Read that release's dependencies, verification notes, and SHA-256 hash.
3. Run setup from your ordinary Windows account and approve administrator access. A protected worker installs `LightOrder.Service`, plugins, and the app. It then tries to launch the app with that account's existing unelevated Explorer token. If this is unavailable, setup reports completion and asks you to reopen LightOrder.
4. Check the service connection in Settings. Discovery reads devices. **Restore lighting on startup** and **Restore cooling on startup** are separate opt-ins, both false by default. Enabled restoration requests saved settings only for eligible discovered devices, without retrying failures. Pending incomplete lighting operations suppress startup lighting restoration.
5. Review each device's available controls before applying a change. OpenRGB discovery may start the configured backend, but does not itself set an effect.

Self-contained packages include .NET. WebView2 remains a prerequisite. A successful build does not establish clean-machine installation, update, or removal.

## Lighting

Select a device and Static, Breathing, Rainbow, or Off, adjust color/brightness, then **Save & apply**. Draft edits do not write hardware. Brightness scales RGB output; it is not measured light output. Breathing/rainbow use service-generated static frames at a target 4 fps, on at most 16 devices, with periods of 2–60 seconds (default 6). Device latency can lower the rate. A failed frame stops that device's animation without retry.

Sync is opt-in and needs at least two eligible devices with known independent settings. Save an independent setting first when its prior state is unknown. Select members, edit the group, and save. Disable sync and save/apply to restore known pre-sync settings. These are LightOrder's known settings, not unreadable factory colors. Synchronized animations can share a phase; this is not a hardware timing guarantee.

Use the save menu to create a scene for reuse or automation. A partial failure remains visible; inspect each device before trying again. Accepted commands and matching OpenRGB cache values do not establish physical output. Uncertain writes are not automatically replayed.

## Cooling

Only devices advertising `fan.profile` offer editing. Select radiator or case, choose a curve or manual percentage, then save/apply. Curves need 2–12 points, increasing temperature, nondecreasing duty, and an 80°C / 100% endpoint. Minimum duty is 40% radiator and 25% case. Manual mode retains thermal protection. Pump RPM is read-only; duty is not a fixed RPM.

Control is **continuous**, without a fixed duration or update limit, and continues with the UI closed. **Restore original control**, service shutdown, and safety faults attempt restoration. Check the reported result.

Missing, stale, ambiguous, or invalid temperature/RPM feedback and fan faults trigger restoration. Suspend notifications and control gaps over five seconds restore before conservative restart with fresh telemetry. An unresolved durable restoration intent blocks new takeover. Hard crashes cannot guarantee restoration; long-duration and sleep/resume hardware verification of the new code remains pending. See [cooling and recovery](cooling.md).

## Automation

In **Automation**, create a schedule or exact-process rule and explicitly enable it. New rules default to disabled. Rules combine saved lighting scenes, supported cooling presets, power schemes, and process policies. Schedules run only in the exact local minute; process rules apply to newly observed identities belonging to the installation owner. Exit actions use settings captured when saving. Read [automation](automation.md) for at-most-once dispatch, missed triggers, multiple instances, pause states, and recovery.

## System policies and preferences

My computer shows available hardware information and Windows policies. Choose a power scheme, or a process and supported priority; affinity is offered only where supported. PID plus creation time identifies a process. RealTime priority and unsupported multi-group affinity writes are rejected.

**Restore original policies** restores only properties still matching LightOrder's writes, preserving later external changes. Reused PIDs do not inherit an old process identity. Settings provides language, appearance, startup restoration opt-ins, service diagnostics, and setup/repair access.

## Close, restore, update, remove

- Closing the window hides it to the tray. **Exit UI** leaves continuous cooling, lighting animations, automation, and the service active.
- **Restore & exit** attempts controller release and known pre-sync lighting restoration. Failures keep the window open. It does not stop the service or disable automation; enabled rules can apply later settings. Disable rules first if future changes should stop.
- Update/repair with the downloaded installer and `--install`. Opening it without arguments launches an existing installation. A protected staged worker waits for its launcher to exit, stops the service, closes exact installed processes, and replaces files. Existing owner SID, enabled plugins, and preferences survive. Setup tries to reopen the app without elevation; reopen it manually if prompted.
- Remove through Windows Installed apps or installed `LightOrder.exe --uninstall`. The elevated worker stops/deletes the service, closes installed processes, deletes program files and the uninstall entry, and removes exact LightOrder shortcuts/startup commands. The dialog offers to keep or delete ProgramData preferences, profiles, automation/backend data and the owner's local LightOrder data. Owner-local data deletion requires a resolvable Windows profile; owner-specific Run-entry cleanup requires that owner's registry hive to be loaded. Shared PawnIO and WebView2 remain installed. The staged helper is scheduled for reboot cleanup. Unresolved fan restoration intent or incomplete service deletion blocks file cleanup and retains evidence for recovery.

These lifecycle descriptions reflect source behavior; package-specific clean-machine testing is still required. A stop or process exit alone does not prove hardware restoration.

## OpenRGB and PawnIO

The dedicated OpenRGB SDK v3 endpoint is `127.0.0.1:16742`. ENE SMBus DRAM is enabled by default. Additional physical devices require explicit administrator configuration. The managed backend needs a compatible executable and the complete detector inventory for that exact build. A running PawnIO driver is required when enabled detectors use SMBus.

Releases must identify whether OpenRGB and PawnIO are bundled. Source builds do not fetch them. If packaging supplies a signed PawnIO installer, setup can invoke it and start the installed driver; release maintainers must still verify provenance and compatibility. No Windows security feature needs to be disabled.

Administrators can place `backend.json` beside the installed OpenRGB plugin:

```json
{
  "executablePath": "C:/Program Files/OpenRGB/OpenRGB.exe",
  "sourceConfigPath": "C:/Program Files/LightOrder/plugins/OpenRgb/detectors.json",
  "dataDirectory": "C:/ProgramData/LightOrder/OpenRgb",
  "requestTimeoutMs": 5000,
  "enableEneDram": true,
  "additionalDevices": []
}
```

Paths must be absolute, local, protected paths; never select a user-writable executable. Without this file, defaults are plugin-local `runtime/OpenRGB.exe` and `detectors.json`, with state under `%ProgramData%\LightOrder\OpenRgb`. CLI/environment overrides do not bypass protected deployment.

The adapter disables detectors outside the configured allowlist, enables HID safe mode, and disables ENE persistent saves. Every configured detector must exist in the matched complete inventory; an old inventory is insufficient. See [OpenRGB device policy](plugin-development.md#openrgb-device-policy) for exact additional-device rules. Configuration is not a claim of all-hardware support. Close competing RGB controllers yourself; LightOrder refuses a competing OpenRGB instance and does not terminate it.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Service unavailable | Windows Services → `LightOrder.Service`, then Settings → Setup & repair. Reconnect as the installation owner. |
| WebView2 error | Install/update Evergreen Runtime and reopen setup. |
| Plugin disconnected or animation stopped | Record diagnostics. Check device state and uncertain prior writes before restarting. |
| OpenRGB device absent | If `BackendDiscovering` is reported, discovery retries every 15 seconds without setting effects. Otherwise check exact allowlist, inventory, protected paths, required PawnIO status, and competing processes. |
| Fan editor absent | Board, mapping, permissions, baseline, or unresolved restoration intent may prevent control. Read-only mode is intentional. |
| Rule paused | Resolve diagnostics, then explicitly save. Do not delete its execution journal to reset it. See [automation recovery](automation.md#failures-and-bounds). |
| Restoration unverified | Keep details; verify actual fan behavior and normal firmware control. Process exit/restart is not proof. |

Include versions, Windows version, hardware, operation, error category, and whether evidence is software or physical. Remove SIDs, personal paths, process names, and other private details from public logs/screenshots.
