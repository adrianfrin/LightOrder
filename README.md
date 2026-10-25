# LightOrder

**Lighting, cooling, and system controls for Windows.**

[简体中文](README.zh-CN.md) · [Getting started](docs/en/getting-started.md) · [Hardware support](docs/en/hardware-support.md) · [Plugin development](docs/en/plugin-development.md)

LightOrder brings supported PC lighting, bounded fan control, live telemetry, and Windows policies into one desktop app. Device adapters run in separate processes behind a local Windows service. The interface uses the capabilities each device reports, so new adapters can reuse existing controls without adding vendor logic to the app.

## Get LightOrder

Download the Windows x64 **`LightOrder.exe`** installer from [Releases](https://github.com/adrianfrin/LightOrder/releases), when an installer is available. Source downloads and CI artifacts are not tested releases; consult the individual release notes for installation and hardware verification.

- **Windows 11 x64** is the target platform.
- Install [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) before setup. Self-contained packages include the .NET runtime.
- First setup requests administrator approval to install the service and plugins. The desktop app then runs without per-action UAC prompts. Installation, repair, updates, and removal can require elevation.
- Memory lighting needs a compatible OpenRGB backend and a working PawnIO installation. Whether these are included depends on the release; automatic bundling is not assumed. See [dependencies and setup](docs/en/getting-started.md).

One download installs multiple files under `%ProgramFiles%\LightOrder`. It is not a portable, single-file hardware controller.

## What it does

- **Lighting:** static color, brightness, and off for supported devices. Explicit sync groups preserve each member's known independent setting for later restoration.
- **Cooling:** independent radiator and case curves, manual duty with thermal protection, and temperature/RPM history. Control is limited to the validated board and channels.
- **My computer:** read-only component information, available Windows power schemes, and supported process priority/affinity controls.
- **Local service:** protected installation, account-restricted named pipe, and process-isolated plugins. Closing the window hides it to the tray; exiting the UI leaves the service running.
- **English and Simplified Chinese:** switch the interface language in Settings.

The current lighting effects are **static and off**. Breathing, rainbow, scheduled rules, and application-triggered effects are not implemented.

## Screenshots

[![LightOrder lighting page](docs/images/lighting.png)](docs/images/lighting.png)

*Lighting — device selection, color and brightness, and explicit synchronization.*

[![LightOrder cooling page](docs/images/cooling.png)](docs/images/cooling.png)

*Cooling — separate radiator and case settings with temperature and fan-speed history.*

[![LightOrder computer page](docs/images/computer.png)](docs/images/computer.png)

*My computer — hardware information and available system policies.*

## Hardware and control limits

| Area | Current scope |
| --- | --- |
| Motherboard lighting | IT5702 adapter; identity checked before writes. Static color/off has prior physical observation on the development system. |
| Memory lighting | OpenRGB SDK v3 bridge limited to ENE SMBus DRAM devices; backend cache is not physical color readback. |
| Fan control | B650M AORUS ELITE AX ICE, IT8689E instance 0, validated radiator/case channels only. Other systems remain read-only or unavailable. |
| Monitoring | Available LibreHardwareMonitor sensors; availability depends on hardware, driver, and permissions. |
| GPU, keyboard, mouse lighting | No supported control adapters supplied. |

**Cooling sessions stop after at most 30 minutes or 1,800 control updates.** Updating a profile does not extend its deadline. Radiator duty has a 40% floor; case duty has a 25% floor; both reach 100% at 80°C. Pump control is read-only. No firmware or persistent device-profile writes are supported.

Normal release attempts restoration and verifies the controls that support it. Hard process termination, OS crashes, power loss, and driver failure cannot guarantee restoration. Sleep/resume and indefinite unattended cooling operation are **not verified**. Lighting command acceptance or a matching OpenRGB cache does not prove the physical light changed. See the [support and evidence matrix](docs/en/hardware-support.md).

## Build from source

Use Windows, PowerShell, and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). From the repository root:

```powershell
dotnet build src/LightOrder.App/LightOrder.App.csproj -c Release
dotnet build src/LightOrder.Service/LightOrder.Service.csproj -c Release
dotnet run --project tests/LightOrder.Service.Tests/LightOrder.Service.Tests.csproj -c Release
```

Create a self-contained installer payload with:

```powershell
./scripts/Publish-Production.ps1 -Dotnet (Get-Command dotnet).Source
```

The script publishes the app, service, and four production plugins, then produces `artifacts/installer/LightOrder.exe`. OpenRGB, its matching detector inventory, and a signed PawnIO installer are optional build inputs. Review the [build, test, and release guide](docs/en/build-and-release.md) before distributing a package.

## Documentation and contribution

- [Getting started and troubleshooting](docs/en/getting-started.md)
- [Hardware support and verification boundaries](docs/en/hardware-support.md)
- [Architecture and data locations](docs/en/architecture.md)
- [Plugin development and protocol v1](docs/en/plugin-development.md)
- [Build, tests, and release packaging](docs/en/build-and-release.md)
- [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)

Bug reports and hardware support proposals are welcome in [Issues](https://github.com/adrianfrin/LightOrder/issues). Include the app/plugin version and distinguish software readback from physical observations. Do not post personal identifiers or security exploits publicly.

## License

LightOrder is licensed under **GNU GPL-3.0-or-later**. The IT5702 protocol implementation derives from liquidctl and retains its attribution. LibreHardwareMonitor and related MPL-2.0 components keep their own licenses; OpenRGB and other dependencies are not relicensed by this repository. See [third-party notices and distribution obligations](THIRD-PARTY-NOTICES.md) and the [GNU GPL v3 text](https://www.gnu.org/licenses/gpl-3.0.html).
