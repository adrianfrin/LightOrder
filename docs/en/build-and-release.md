# Build, test and release

[简体中文](../zh-CN/build-and-release.md) · [Evidence matrix](hardware-support.md)

Use Windows 11 x64, .NET SDK 10.0.402 or a compatible patch, Node.js for UI checks, and PowerShell. Build from a trusted checkout.

```powershell
dotnet build src/LightOrder.App/LightOrder.App.csproj -c Release
dotnet build src/LightOrder.Service/LightOrder.Service.csproj -c Release
dotnet run --project tests/LightOrder.Service.Tests/LightOrder.Service.Tests.csproj -c Release
dotnet run --project tests/LightOrder.Automation.Tests/LightOrder.Automation.Tests.csproj -c Release
node src/LightOrder.App/verify-ui.cjs
dotnet run --project plugins/GigabyteIt5702/GigabyteIt5702.csproj -- --self-test
dotnet run --project plugins/OpenRgb/ControlCenter.OpenRgb.csproj -- --self-test
dotnet run --project plugins/HardwareMonitor/HardwareMonitor.csproj -- --service-self-test
dotnet run --project plugins/WindowsPolicy/ControlCenter.WindowsPolicy.csproj -- --self-test
dotnet run --project src/LightOrder.Setup/LightOrder.Setup.csproj -- --self-test
```

These are commands to run, not a record of passing checks. Service tests include software lighting effects; automation tests use deterministic time/process/store fakes. HardwareMonitor tests exercise continuous control, safety failures, and recovery with fake backends. OpenRGB tests cover protocol/allowlist behavior. Setup self-tests use temporary trees and fixtures; they do not install or remove a real service. Confirm results on the final revision after concurrent implementation finishes.

## Package

```powershell
./scripts/Publish-Production.ps1 -Dotnet (Get-Command dotnet).Source
```

Optional `-OpenRgbDirectory`, `-DetectorConfig`, and `-PawnInstaller` supply the compatible runtime, exact detector inventory, and Authenticode-valid PawnIO installer. These are build inputs, not hardcoded developer paths. The internal output is `artifacts/installer/LightOrder.exe`; publish the Windows x64 download as **`LightOrder-win-x64.exe`**. The embedded payload installs several files, a service, and an uninstall entry. WebView2 Evergreen Runtime is separate.

The OpenRGB runtime goes in `plugins/OpenRgb/runtime`, with `detectors.json` alongside it. Default configuration enables ENE SMBus DRAM only, with SDK on `127.0.0.1:16742`. Additional devices need explicit detector and exact identity rules in protected `backend.json`; verify every selected model separately. Match the complete detector inventory to the engine version. See [device policy](plugin-development.md#openrgb-device-policy).

## Current verification boundary

New source/test implementations are distinct from historical hardware observations. This documentation pass inspected current source and did not run builds, tests, setup, or hardware control. The following still need current release evidence: native screenshots, final test suite results, clean-machine install/update/full uninstall, owner-scoped real process triggers, physical breathing/rainbow, continuous thermal-load control, suspend/resume, restoration failures, and any added OpenRGB model. Historical static/off and bounded-fan records do not validate those behaviors.

## Release verification

1. Run the builds and hardware-free tests on the final revision. Record commands, exit codes, source revision, and package hash. Review privileged routing, process ownership, protected paths, and shutdown behavior.
2. Install the actual EXE on a clean supported Windows machine. Check WebView2 handling, UAC ownership (including a different admin account), ordinary-user pipe access, discovery, and default-false startup restoration. Verify explicit opt-ins separately.
3. Exercise [automation](automation.md): exact-minute/no-catch-up schedules, duplicate suppression across restart, owner-scoped process start/exit with multiple instances and PID reuse, paused failures, and durable journal handling. Do not infer success from rule save.
4. Verify physical lighting and continuous cooling separately from command acceptance. Exercise sustained thermal load, stale sensors, suspend/resume, control gaps, normal release, and unresolved intent refusal. Record hardware, drivers, timing, register/cache readbacks, and physical observations as separate evidence. Hard crashes remain outside guaranteed restoration.
5. Verify update with changed runtime/plugin files and preserved owner/plugin selection/preferences. Verify removal from Installed apps and from the installed launcher, both keep/delete preferences choices, exact startup integration removal, shared PawnIO/WebView2 retention, and external-process preservation. An unresolved fan restoration marker must block unsafe lifecycle cleanup until recovery is verified.
6. Capture native App screenshots with `--capture-dir <absolute-output-directory>`, then inspect each for privacy. Replace `docs/images/lighting.png`, `cooling.png`, and `computer.png` only with reviewed native captures. Add overview/automation images only when actually captured and linked. Existing images and UI previews are not new package/hardware evidence.
7. Include dependency notices, exact version/source records, and required corresponding-source archives for distributed GPL/MPL/LGPL components. IPC separation does not remove redistribution obligations. Preserve [FlClash design credit](../../THIRD-PARTY-NOTICES.md) without claiming code reuse absent evidence.
8. Publish SHA-256, source tag, support matrix, actual results, pending tests, and known limits. Installer is unsigned unless release notes establish otherwise. Do not require users to disable Windows security features.

The installed-service integration harness can capture a read-only snapshot:

```powershell
dotnet run --project tests/LightOrder.Integration/LightOrder.Integration.csproj -c Release -- snapshot <absolute-output.json>
```

It also accepts `rpc <standard-method> <parameters.json> <result.json>`. Write-capable RPCs operate the installed service and may change hardware/policies; prepare device-specific acceptance and use reviewed parameters. A snapshot or live pipe round trip alone does not establish physical control/restoration.

CI covers only the checks actually configured and executed there. It is not installed-service or hardware acceptance. Source/build artifacts are not automatically a stable release.
