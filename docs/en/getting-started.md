# Getting started

[简体中文](../zh-CN/getting-started.md) · [Home](../../README.md)

## Install and launch

1. Use Windows 11 x64 and install the Microsoft Edge WebView2 Evergreen Runtime from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/). Setup checks for it; it does not silently download it.
2. Download `LightOrder.exe` from the project's [release page](https://github.com/adrianfrin/LightOrder/releases). Read that release's dependency list, verification notes, and SHA-256 hash.
3. Run the installer from your ordinary Windows account. Approve the initial administrator prompt. Setup installs `LightOrder.Service`, protected plugins, and the desktop app, then launches the app without elevation.
4. Check the service connection in Settings. Startup discovers devices and reads state; it does not automatically apply saved lighting or fan profiles. The OpenRGB adapter may start its configured backend during discovery, but discovery does not set a lighting effect.
5. Select a device and review its supported controls before applying a change.

Self-contained packages include .NET. WebView2 remains a separate prerequisite. A successful build does not establish that installation, update, or removal has been verified on a clean machine.

## Lighting

Choose a device, select Static or Off, adjust color/brightness, then use **Save & apply**. Editing a draft does not write to hardware. Brightness scales RGB output; it is not measured light output.

Sync is opt-in and requires at least two eligible devices with known independent settings. Save an independent setting first if a device's prior state is unknown. Choose sync members, edit the group, and save. Disabling sync restores the known pre-sync settings when you save and apply. This restores settings known to LightOrder, not an unreadable factory or pre-installation color.

A partial device failure stays visible. Check each device before trying again. An accepted command or matching OpenRGB cache is not physical verification. There is no automatic retry after an uncertain write.

## Cooling

Only a controller that advertises `fan.profile` has editable controls. Select radiator or case, edit a curve or manual percentage, then save and apply. Curves require 2–12 points, increasing temperatures, nondecreasing duty, and a final point of 80°C / 100%. Minimum duty is 40% radiator and 25% case. Manual mode retains a thermal floor. Pump speed is read-only.

Each session lasts at most **30 minutes or 1,800 updates**. Changing a profile keeps the original expiry. The controller attempts restoration at expiry or on **Restore original control**. Check the reported restoration result. Percentage is duty, not a fixed RPM.

Missing temperature/RPM feedback or a fan fault triggers a restoration attempt. Do not use this bounded implementation as an indefinite unattended cooling controller. Sleep/resume and crash recovery remain unverified; hard crashes cannot guarantee restoration.

## System policies

My computer shows available hardware information and Windows policies. Choose a power scheme or a process and supported priority; affinity is offered only when supported. A process is identified by PID and creation time. RealTime priority and unsupported multi-group affinity changes are rejected.

**Restore original policies** restores only properties still matching LightOrder's writes, preserving later changes made by other software. A restarted process with a reused PID is not treated as the original process.

## Close, restore, update, remove

- Closing the window hides it to the tray.
- **Exit UI** closes the desktop app and leaves the service running. It does not renew a fan session's deadline.
- **Restore & exit** in Settings attempts to restore supported controllers and known pre-sync lighting. Failures keep the window open with details. This does not stop the Windows service or restore unknown original lighting.
- To update or repair, exit the UI and run the downloaded installer with `--install`. Opening it without arguments launches an existing installation. Setup stops the service before replacing files; read the specific release's update notes.
- Remove through Windows Installed apps or run the installed `LightOrder.exe --uninstall`. Removal requires elevation and stops/deletes the service. The current implementation retains preferences and program files; exit all LightOrder processes before manually removing retained files. Removal of separately installed dependencies is a separate operation.

## OpenRGB and PawnIO

Memory lighting uses a dedicated OpenRGB SDK v3 endpoint at `127.0.0.1:16742`, restricted to ENE SMBus DRAM. A compatible OpenRGB executable, a complete detector inventory for that exact build, and a running PawnIO driver are required for the managed backend.

The release must say whether OpenRGB and a PawnIO installer are included. Source-only builds do not fetch them automatically. When a signed PawnIO installer is supplied to packaging, setup can invoke it and start the installed driver; that does not establish its version, publisher identity, or hardware compatibility. Release maintainers must verify provenance. No Windows security feature needs to be disabled.

An administrator can configure `backend.json` beside the installed OpenRGB plugin. Use protected executable/configuration paths and a protected data directory; never point the privileged backend at a user-writable executable. Example for an independently installed, trusted OpenRGB build:

```json
{
  "executablePath": "C:/Program Files/OpenRGB/OpenRGB.exe",
  "sourceConfigPath": "C:/Program Files/LightOrder/plugins/OpenRgb/detectors.json",
  "dataDirectory": "C:/ProgramData/LightOrder/OpenRgb",
  "requestTimeoutMs": 5000
}
```

Without this file, defaults are `runtime/OpenRGB.exe` and `detectors.json` beside the plugin, with state under `%ProgramData%\LightOrder\OpenRgb`. The service deploys trusted plugins; older adapter CLI/environment settings are not a supported way to bypass protected deployment. See [release packaging](build-and-release.md) for optional supply paths.

The detector inventory must contain every detector from that exact OpenRGB version. The adapter enables only `ENE SMBus DRAM`, uses HID safe mode, and disables ENE persistent saves. Missing new detectors can be enabled by upstream defaults, so an old inventory is not adequate. Close competing RGB controllers yourself; LightOrder does not terminate another user's OpenRGB instance.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Service unavailable | Check `LightOrder.Service` in Windows Services, then Settings → Setup & repair. Reconnect using the installation owner's account. |
| WebView2 error | Install/update Evergreen Runtime, then reopen setup. |
| Plugin disconnected | Record its diagnostic category. Check the device and any uncertain prior write before restarting the service; reconnect reads state first. |
| Memory lighting absent | Check release dependencies, PawnIO status, protected OpenRGB paths, detector inventory, and competing OpenRGB processes. |
| Fan editor absent | The board, control mapping, permissions, or baseline did not pass the plugin's checks. Read-only mode is intentional. |
| Restoration unverified | Keep the error details; do not infer recovery from process exit or restart. Check actual fan behavior and the hardware's normal control path. |

Reports should include app/plugin versions, Windows version, hardware model, operation, error category, and whether the observation came from software or the physical device. Remove account SIDs, personal paths, process names, and other private details before posting logs or screenshots.
