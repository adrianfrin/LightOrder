# LightOrder

**Lighting, cooling, automation, and system controls for Windows.**

[简体中文](README.zh-CN.md) · [Getting started](docs/en/getting-started.md) · [Automation](docs/en/automation.md) · [Hardware support](docs/en/hardware-support.md) · [Plugin development](docs/en/plugin-development.md)

LightOrder brings supported PC lighting, continuous protected fan control, live telemetry, and Windows policies into one desktop app. Device adapters run in separate processes behind a local Windows service. The interface uses each device's advertised capabilities, so new adapters can reuse controls without adding vendor logic to the app.

## Get LightOrder

Download the Windows x64 **`LightOrder-win-x64.exe`** installer from [Releases](https://github.com/adrianfrin/LightOrder/releases), when available. Source downloads and CI artifacts are not tested releases; consult the release notes for installation and hardware verification.

- **Windows 11 x64** is the target platform.
- Install [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) before setup. Self-contained packages include .NET.
- First setup requests administrator approval to install the service and plugins. Daily desktop operations then run without per-action UAC prompts. Installation, repair, updates, and removal can require elevation.
- ENE memory lighting needs compatible OpenRGB and PawnIO. Bundling depends on the release; see [dependencies and setup](docs/en/getting-started.md).

One download installs multiple files under `%ProgramFiles%\LightOrder` and creates desktop and Start menu shortcuts. Remove it through Windows Installed apps; double-click a newly downloaded installer to update. The installed launcher is `LightOrder.exe`.

## What it does

- **Lighting:** static, breathing, rainbow, brightness, and off on eligible devices. Explicit sync groups preserve known independent settings. Saved scenes can be reused in rules.
- **Cooling:** Silent fixes radiator/case duty at 40%/25%; Balanced filters temperature with hysteresis and bounded speed changes. Custom curves remain editable. Every mode retains immediate full-duty protection at 80°C, live temperature/RPM history, and explicit restoration. Writes remain limited to the allowlisted board and channels.
- **Automation:** exact local-minute schedules and exact-process triggers combine lighting scenes, cooling presets, power schemes, and supported process policies. The service executes rules with durable at-most-once dispatch and visible pause states. See [automation](docs/en/automation.md).
- **My computer:** read-only component information, Windows power schemes, and supported process priority/affinity controls.
- **Local service:** protected installation, account-restricted named pipe, process-isolated plugins, and operation diagnostics. Closing the window hides it to the tray; exiting the UI leaves the service active.
- **Preferences:** English/Simplified Chinese, appearance, and separate startup restoration options for lighting and cooling. Both restoration options default to off.

Breathing and rainbow use service-generated static frames at a target **4 fps**, on at most **16 devices**, with a 2–60 second period. Device latency can reduce the effective rate. They continue with the UI closed; failed frames stop that device's effect without retry.

The interface draws design inspiration from [FlClash](https://github.com/chen08209/FlClash), a GPL-3.0 project. This is design credit; it does not assert that FlClash code was copied. See [third-party notices](THIRD-PARTY-NOTICES.md).

## Screenshots

Native Windows App captures from the 0.2 implementation, connected to the local hardware service. Lighting previews show configuration; they do not prove physical color output.

[![LightOrder lighting page](docs/images/lighting.png)](docs/images/lighting.png)

*Lighting — devices, effects, brightness, and explicit synchronization.*

[![LightOrder cooling page](docs/images/cooling.png)](docs/images/cooling.png)

*Cooling — radiator/case settings and temperature/RPM history.*

[![LightOrder automation page](docs/images/automation.png)](docs/images/automation.png)

*Automation — local schedules and exact-process rules.*

[![LightOrder computer page](docs/images/computer.png)](docs/images/computer.png)

*My computer — hardware information and available system policies.*

## Hardware and control limits

| Area | Current scope |
| --- | --- |
| Motherboard lighting | IT5702 adapter; identity checked before writes. Static/off has prior physical observation on the development system. |
| OpenRGB lighting | SDK v3; ENE SMBus DRAM enabled by default. Additional physical devices require explicit detector and exact-device allowlists. Backend cache is not physical color readback. |
| GPU, keyboard, mouse, other lighting | Can use explicitly configured OpenRGB device adapters where supported; this is not blanket compatibility or new physical validation. |
| Fan control | B650M AORUS ELITE AX ICE, IT8689E instance 0, validated radiator/case channels only. Other systems remain read-only or unavailable. |
| Monitoring | Available LibreHardwareMonitor sensors; availability depends on hardware, driver, and permissions. |

**Production cooling now runs continuously until release, shutdown, or a safety fault.** Radiator duty has a 40% floor; case duty has a 25% floor; both reach 100% at 80°C. Pump control is read-only. No firmware or persistent device-profile writes are supported.

Suspend notifications and control gaps trigger restoration before restarting from fresh actual telemetry. A durable restoration intent blocks unsafe takeover after unresolved writes. Hard process termination, OS crashes, power loss, and driver failure cannot guarantee restoration. Long-duration and sleep/resume hardware testing of the new implementation remains pending. Lighting command acceptance or a matching OpenRGB cache does not prove physical output. See the [support and evidence matrix](docs/en/hardware-support.md) and [cooling guide](docs/en/cooling.md).

## Build from source

Use Windows, PowerShell, and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). From the repository root:

```powershell
dotnet build src/LightOrder.App/LightOrder.App.csproj -c Release
dotnet build src/LightOrder.Service/LightOrder.Service.csproj -c Release
dotnet run --project tests/LightOrder.Service.Tests/LightOrder.Service.Tests.csproj -c Release
dotnet run --project tests/LightOrder.Automation.Tests/LightOrder.Automation.Tests.csproj -c Release
```

Create a self-contained installer payload with:

```powershell
./scripts/Publish-Production.ps1 -Dotnet (Get-Command dotnet).Source
```

The script publishes the app, service, and four production plugins, with an internal installer at `artifacts/installer/LightOrder.exe`. The public download asset is named `LightOrder-win-x64.exe`. OpenRGB, its matching detector inventory, and a signed PawnIO installer are optional build inputs. Review the [build, test, and release guide](docs/en/build-and-release.md) before distributing a package.

## Documentation and contribution

- [Getting started and troubleshooting](docs/en/getting-started.md)
- [Automation: schedules and process rules](docs/en/automation.md)
- [Cooling and recovery](docs/en/cooling.md)
- [Hardware support and verification boundaries](docs/en/hardware-support.md)
- [Architecture and data locations](docs/en/architecture.md)
- [Plugin development and protocol v1](docs/en/plugin-development.md)
- [Build, tests, and release packaging](docs/en/build-and-release.md)
- [Current verification and historical evidence](docs/en/verification.md)
- [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)

Bug reports and hardware proposals are welcome in [Issues](https://github.com/adrianfrin/LightOrder/issues). Include app/plugin versions and distinguish software readback from physical observations. Do not post personal identifiers or security exploits publicly.

## License

LightOrder is **GNU GPL-3.0-or-later**. The IT5702 implementation derives from liquidctl and retains its attribution. LibreHardwareMonitor and related MPL components keep their own licenses; OpenRGB and other dependencies are not relicensed. See [third-party notices and distribution obligations](THIRD-PARTY-NOTICES.md) and the [GNU GPL v3 text](https://www.gnu.org/licenses/gpl-3.0.html).
