# 构建、测试与发布

[English](../en/build-and-release.md)

环境：Windows 11 x64、.NET SDK 10.0.402或兼容补丁、Node.js、PowerShell。仓库不依赖开发者机器的绝对路径。

```powershell
dotnet build src/LightOrder.App/LightOrder.App.csproj -c Release
dotnet run --project tests/LightOrder.Service.Tests/LightOrder.Service.Tests.csproj -c Release
node src/LightOrder.App/verify-ui.cjs
dotnet run --project plugins/GigabyteIt5702/GigabyteIt5702.csproj -- --self-test
dotnet run --project plugins/OpenRgb/ControlCenter.OpenRgb.csproj -- --self-test
dotnet run --project plugins/HardwareMonitor/HardwareMonitor.csproj -- --service-self-test
dotnet run --project plugins/WindowsPolicy/ControlCenter.WindowsPolicy.csproj -- --self-test
```

生成自包含x64安装EXE：

```powershell
./scripts/Publish-Production.ps1 -Dotnet (Get-Command dotnet).Source
```

可选参数 `-OpenRgbDirectory`、`-DetectorConfig`、`-PawnInstaller` 分别传入兼容引擎目录、与引擎匹配的检测器清单及签名有效的驱动安装器。输出 `artifacts/installer/LightOrder.exe`。一个下载文件安装多个组件、Windows服务和卸载入口；WebView2运行时需已安装。

内存插件引擎位于 `plugins/OpenRgb/runtime`，检测清单为同级`detectors.json`。仅启用ENE内存检测，SDK仅监听本机16742端口。不同时启动第二个OpenRGB实例。

发布前必须：构建与纯测试、真实EXE安装、普通用户IPC、设备实际控制、停止恢复、更新/卸载、对应源码/许可证核对及SHA-256记录。通过App的`--capture-dir <绝对目录>`采集实际页面截图，逐张检查隐私后放进README。不能把设计稿或CI成功当实机验收。

分发第三方二进制时须携带其许可证和精确版本来源，并提供相应源码归档。发布说明明确支持范围、未验证项及签名状态。除非发布说明另有说明，安装器未做商业代码签名，不能要求用户关闭系统保护。
