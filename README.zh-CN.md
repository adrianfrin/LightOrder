# 光序 · LightOrder

**Windows 灯光、散热与系统控制工具。**

[English](README.md) · [开始使用](docs/zh-CN/getting-started.md) · [硬件支持](docs/zh-CN/hardware-support.md) · [插件开发](docs/zh-CN/plugin-development.md)

光序把受支持的电脑灯光、有时限的风扇控制、实时遥测和 Windows 策略放在同一个桌面应用中。设备适配器在本地 Windows 服务管理的独立进程中运行。界面根据设备声明的能力提供操作；新适配器可以复用现有控件，无需向主界面添加厂商判断。

## 获取光序

[Releases](https://github.com/adrianfrin/LightOrder/releases) 中提供安装包时，请下载 Windows x64 **`LightOrder.exe`**。源码压缩包和 CI 产物不等于已验收的发行版；安装与硬件验证范围以对应版本的发布说明为准。

- 目标平台为 **Windows 11 x64**。
- 安装前需要 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。自包含安装包已包含 .NET 运行时。
- 首次配置需要管理员授权，以安装服务和插件。之后桌面界面的日常设备操作无需逐次弹出 UAC。安装、修复、更新和卸载仍可能需要提权。
- 内存灯光需要兼容的 OpenRGB 后端和可用的 PawnIO。是否随包提供取决于具体发行版，不能默认认为已经包含。详见[依赖与安装](docs/zh-CN/getting-started.md)。

单个下载文件会把多个组件安装到 `%ProgramFiles%\LightOrder`。它不是无需安装的单文件硬件控制器。

## 功能

- **灯光：** 为受支持的设备设置静态颜色、亮度或关闭。显式选择同步成员，并保存成员加入前的已知独立设置，以便恢复。
- **散热：** 分别编辑冷排与机箱曲线，或使用保留高温保护的手动百分比；查看温度与转速历史。控制仅开放给已验证的主板和通道。
- **我的电脑：** 读取硬件信息，选择 Windows 电源计划，设置支持的进程优先级和处理器亲和性。
- **本地服务：** 受保护的安装目录、限制账户访问的命名管道、进程隔离插件。关闭窗口会隐藏到托盘；退出界面后服务仍继续运行。
- **中英文界面：** 在设置中切换简体中文和英语。

当前灯光效果只有 **静态和关闭**。尚未实现呼吸、彩虹、定时规则或应用触发灯效。

## 应用截图

[![光序灯光页](docs/images/lighting.png)](docs/images/lighting.png)

*灯光：选择设备，调整颜色与亮度，显式管理同步成员。*

[![光序散热页](docs/images/cooling.png)](docs/images/cooling.png)

*散热：分别编辑冷排和机箱设置，查看温度与风扇转速历史。*

[![光序我的电脑页](docs/images/computer.png)](docs/images/computer.png)

*我的电脑：硬件信息与可用的系统策略。*

## 硬件范围与控制限制

| 项目 | 当前范围 |
| --- | --- |
| 主板灯光 | IT5702 适配器；写入前验证身份。开发机上已有静态颜色与关闭的实物观察记录。 |
| 内存灯光 | OpenRGB SDK v3 桥接，仅接受 ENE SMBus DRAM；后端缓存不等于物理颜色回读。 |
| 风扇控制 | 仅 B650M AORUS ELITE AX ICE、IT8689E 实例 0，以及已验证的冷排和机箱通道。其他系统保持只读或不可用。 |
| 硬件监控 | 读取 LibreHardwareMonitor 提供的传感器；可用性取决于硬件、驱动与权限。 |
| 显卡、键盘、鼠标灯光 | 尚未提供支持的控制适配器。 |

**散热会话最长 30 分钟或 1,800 次控制更新。** 修改配置不会延长原有截止时间。冷排最低 40%，机箱最低 25%，两者在 80°C 达到 100%。水泵只读。不支持固件或设备持久配置写入。

正常释放会尝试恢复，并对支持回读的控制进行验证。强制结束进程、系统崩溃、断电或驱动故障不能保证恢复。睡眠与唤醒、无限期无人值守散热均 **未经验证**。灯光命令已接受或 OpenRGB 缓存匹配，不能证明实物已经变化。详见[支持与证据矩阵](docs/zh-CN/hardware-support.md)。

## 从源码构建

需要 Windows、PowerShell 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。在仓库根目录运行：

```powershell
dotnet build src/LightOrder.App/LightOrder.App.csproj -c Release
dotnet build src/LightOrder.Service/LightOrder.Service.csproj -c Release
dotnet run --project tests/LightOrder.Service.Tests/LightOrder.Service.Tests.csproj -c Release
```

生成自包含安装包：

```powershell
./scripts/Publish-Production.ps1 -Dotnet (Get-Command dotnet).Source
```

脚本发布应用、服务和四个生产插件，然后生成 `artifacts/installer/LightOrder.exe`。OpenRGB、与其版本一致的检测器清单，以及已签名的 PawnIO 安装程序是可选构建输入。分发前请阅读[构建、测试与发布指南](docs/zh-CN/build-and-release.md)。

## 文档与贡献

- [开始使用与故障排查](docs/zh-CN/getting-started.md)
- [硬件支持与验证边界](docs/zh-CN/hardware-support.md)
- [架构与数据位置](docs/zh-CN/architecture.md)
- [插件开发与协议 v1](docs/zh-CN/plugin-development.md)
- [构建、测试与安装包](docs/zh-CN/build-and-release.md)
- [参与贡献](CONTRIBUTING.md) · [安全政策](SECURITY.md)

欢迎在 [Issues](https://github.com/adrianfrin/LightOrder/issues) 提交问题或硬件支持建议。请提供应用和插件版本，并区分软件回读与实物观察。不要公开个人标识或安全漏洞利用细节。

## 许可证

LightOrder 使用 **GNU GPL-3.0-or-later**。IT5702 协议实现派生自 liquidctl，并保留原作者声明。LibreHardwareMonitor 及相关 MPL-2.0 组件保留各自许可证；本仓库不会重新授权 OpenRGB 或其他依赖。详见[第三方声明与分发义务](THIRD-PARTY-NOTICES.md)及 [GNU GPL v3 正文](https://www.gnu.org/licenses/gpl-3.0.html)。
