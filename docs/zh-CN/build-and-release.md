# 构建、测试与发布

[English](../en/build-and-release.md) · [证据矩阵](hardware-support.md)

环境：Windows 11 x64、.NET SDK 10.0.402 或兼容补丁、用于界面检查的 Node.js、PowerShell。请从可信检出目录构建。

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

以上是待运行命令，不是通过记录。服务测试包含软件动画，自动化测试使用确定性时钟/进程/存储假实现。HardwareMonitor 使用假后端检查持续控制、安全故障与恢复；OpenRGB 检查协议/允许列表。安装自测只操作临时目录和样例，不安装或删除真实服务。并行实现完成后，应在最终版本确认结果。

## 打包

```powershell
./scripts/Publish-Production.ps1 -Dotnet (Get-Command dotnet).Source
```

可选 `-OpenRgbDirectory`、`-DetectorConfig`、`-PawnInstaller` 分别提供兼容运行时、准确检测器清单与 Authenticode 有效的 PawnIO 安装器。它们是构建输入，不是硬编码开发机路径。内部输出为 `artifacts/installer/LightOrder.exe`，公开 Windows x64 下载命名为 **`LightOrder-win-x64.exe`**。内嵌负载安装多个文件、服务和卸载项。WebView2 Evergreen Runtime 独立安装。

OpenRGB 运行时位于 `plugins/OpenRgb/runtime`，旁边放置 `detectors.json`。默认仅启用 ENE SMBus DRAM，SDK 监听 `127.0.0.1:16742`。其他设备需在受保护 `backend.json` 显式配置检测器和精确身份，逐型号验证。完整检测器清单必须匹配引擎版本。详见[设备策略](plugin-development.md#openrgb-设备策略)。

## 当前验证边界

新增源码/测试与历史硬件观察是不同证据。本次文档工作阅读当前源码，没有运行构建、测试、安装或硬件控制。当前发行仍需：原生截图、最终测试结果、干净机器安装/更新/完整卸载、真实所属账户进程触发、实物呼吸/彩虹、持续热负载、睡眠唤醒、恢复失败和新增 OpenRGB 型号的证据。历史静态/关闭及有界风扇记录不能验证这些新行为。

## 发行验证

1. 在最终版本运行构建与无硬件测试，记录命令、退出码、源码版本和包体哈希，审查特权路由、进程归属、保护路径和关闭行为。
2. 在干净的受支持 Windows 上安装真实 EXE，检查 WebView2、UAC 归属（含使用不同管理员账户）、普通用户管道、发现及默认 false 的启动恢复。另测主动开启恢复。
3. 验证[自动化](automation.md)：准确分钟/不补执行、跨重启防重、所属账户进程启动/退出、多实例/PID 复用、失败暂停和持久执行日志。不能从规则保存成功推断执行成功。
4. 分别验证实物灯光和持续散热，覆盖持续热负载、过期传感器、挂起唤醒、控制间隔、正常释放及遗留意图拒绝。记录硬件、驱动、时序、寄存器/缓存和实物观察，区分证据。硬崩溃仍不保证恢复。
5. 用变更的运行时/插件文件验证更新，确认 owner/插件选择/偏好保留。分别从已安装应用和安装后启动器卸载，测试保留/删除偏好、准确启动项清理、共享 PawnIO/WebView2 保留及外部进程不受影响。遗留风扇恢复标记应阻止不安全清理，直到核实恢复。
6. 使用 App 的 `--capture-dir <绝对输出目录>` 采集原生截图，逐张检查隐私。仅用检查后的原生图替换 `docs/images/lighting.png`、`cooling.png`、`computer.png`。概览/自动化截图在实际采集后再添加引用。旧图和界面预览不能证明新包或硬件行为。
7. 分发 GPL/MPL/LGPL 组件时提供许可证、精确版本来源与所需对应源码归档。进程隔离不免除义务。保留 [FlClash 设计致谢](../../THIRD-PARTY-NOTICES.md)，没有证据时不宣称代码复用。
8. 发布 SHA-256、源码标签、支持矩阵、实际结果、待测项目和已知限制。除非发布说明有证据，否则安装器视为未签名。不能要求关闭 Windows 安全功能。

安装服务集成程序可保存只读快照：

```powershell
dotnet run --project tests/LightOrder.Integration/LightOrder.Integration.csproj -c Release -- snapshot <绝对输出.json>
```

也支持 `rpc <标准方法> <参数.json> <结果.json>`。写接口操作已安装服务，可能改变硬件/策略，应先准备设备验收并核对参数。快照或真实管道往返不能单独证明物理控制/恢复。

CI 仅覆盖实际配置并运行的检查，不等于服务安装或硬件验收。源码/构建产物不自动成为稳定发行版。
