# Build, test and release

[简体中文](../zh-CN/build-and-release.md)

Use Windows 11 x64, .NET SDK 10.0.402 or a compatible patch, Node.js for UI checks, and PowerShell. Clone this repository; do not run build tools from an untrusted downloaded source folder.

```powershell
dotnet build src/LightOrder.App/LightOrder.App.csproj -c Release
dotnet run --project tests/LightOrder.Service.Tests/LightOrder.Service.Tests.csproj -c Release
node src/LightOrder.App/verify-ui.cjs
dotnet run --project plugins/GigabyteIt5702/GigabyteIt5702.csproj -- --self-test
dotnet run --project plugins/OpenRgb/ControlCenter.OpenRgb.csproj -- --self-test
dotnet run --project plugins/HardwareMonitor/HardwareMonitor.csproj -- --service-self-test
dotnet run --project plugins/WindowsPolicy/ControlCenter.WindowsPolicy.csproj -- --self-test
```

Create the self-contained x64 installer:

```powershell
./scripts/Publish-Production.ps1 -Dotnet (Get-Command dotnet).Source
```

Optional `-OpenRgbDirectory`, `-DetectorConfig`, `-PawnInstaller` include the existing compatible external runtime, exact detector inventory, and Authenticode-valid PawnIO installer. They are build-time inputs, not source hardcoded paths. The resulting `artifacts/installer/LightOrder.exe` embeds an installation payload. It installs several files, a Windows service and an uninstall entry. It does not bundle the WebView2 evergreen runtime.

For the memory plugin, a complete OpenRGB runtime goes in `plugins/OpenRgb/runtime`, with `detectors.json` alongside it. The supplied backend enables only ENE SMBus DRAM and binds the SDK to 127.0.0.1:16742. Starting a second external OpenRGB is refused. New detector inventories must be matched to the engine version.

## Release checklist

1. Run builds and hardware-free tests; review privileged changes.
2. Verify installation from the EXE, ordinary-user pipe access, discovery and actual device operations. Verify restoration separately from command acceptance.
3. Verify update and uninstall behavior. Do not overwrite a running UI; stop device control before replacing plugins.
4. Capture screenshots from the installed App with `--capture-dir <absolute-output-directory>`. This captures fixed application pages from WebView2, not mock pages. Inspect every image for personal data before publishing.
5. Include GPL/MPL/LGPL and other dependency notices, exact version/source records and corresponding source archives for distributed binaries. Do not assume IPC separation removes redistribution obligations.
6. Publish installer SHA-256, source tag, support matrix and known limitations. Installer is unsigned unless release notes explicitly say otherwise.

GitHub Actions builds/tests without hardware on Windows; CI success is not live hardware validation. Source and build artifacts are not automatically a stable release.
