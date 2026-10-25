# 架构与本地数据

[English](../en/architecture.md) · [首页](../../README.zh-CN.md)

光序安装三个层次的组件。普通用户权限的 WPF/WebView2 桌面应用显示设备能力。特权 Windows 服务负责设备发现、路由和保存。独立插件进程负责设备访问和恢复。

```text
LightOrder.App（普通用户；内嵌本地界面）
    │ 按账户限制的命名管道，JSON-RPC 2.0
    ▼
LightOrder.Service（特权 Windows 服务）
    │ 各子进程标准输入/输出，JSON-RPC 2.0
    ├── GigabyteIt5702 → 受支持的 HID 灯控器
    ├── OpenRgb → 本机 OpenRGB SDK → 受支持的 ENE 内存
    ├── HardwareMonitor → 传感器与白名单风扇控制
    └── WindowsPolicy → Windows 策略与系统信息
```

这是实现说明，不是独立安全认证。进程隔离可以隔离许多故障，但不是操作系统沙箱。已安装插件继承服务权限，必须来自可信来源。

## 边界

命名管道为 `LightOrder.Control.v1`，仅允许安装所属 Windows SID、管理员和 SYSTEM，拒绝远程客户端。服务检查部署树和状态路径的所有者、写权限及重解析点。不能把普通用户可写的源码目录直接当作可信生产服务目录。

公开方法为 `app.snapshot`、`app.refresh`、`app.execute`、`app.plugins`、`profiles.read` 和 `profiles.write`。命令只能路由到发现了该设备且声明了对应能力的插件。公开设备 ID 为 `pluginId::localId`，插件收到本地 ID。不提供通用文件、Shell、注册表或任意进程执行 RPC。

WebView2 通过固定本地源提供内嵌资源，不开放特权 HTTP 服务。桌面壳禁用外部导航、下载、权限请求和任意宿主对象。

插件请求串行执行并有有限超时。超时表示结果可能未知，不会自动重放写入。断连插件的路由会失效。正常服务关闭先请求释放再销毁进程；强制结束或驱动故障不能证明恢复成功。

## 数据位置

| 位置 | 内容 |
| --- | --- |
| `%ProgramFiles%\LightOrder` | 应用、服务、运行时、安装入口和可信插件；普通用户仅有读取和执行权限。 |
| `%ProgramFiles%\LightOrder\plugins\<folder>\plugin.json` | 严格插件描述及方法白名单。 |
| `%ProgramData%\LightOrder\service.json` | 格式版本、安装所属 SID、显式启用的插件 ID；仅管理员/SYSTEM 可写。 |
| `%ProgramData%\LightOrder\profiles.json` | 服务保存的界面偏好和配置；原子替换，格式版本 1，上限 256 KiB。 |
| `%ProgramData%\LightOrder\WindowsPolicy\recovery-v1.json` | 服务适配器的策略恢复日志；启动时不自动回放。 |
| `%ProgramData%\LightOrder\OpenRgb` | 受管理 OpenRGB 的运行配置与诊断。 |
| `%LocalAppData%\LightOrder\WebView2` | 每个用户的 WebView2 数据。 |

当前生产配置由服务保存在 ProgramData，不使用旧应用的 LocalAppData 设置文件。`profiles.write` 只保存数据，不执行设备命令。界面的 **保存并应用** 分别调用保存和设备接口，并记录未完成的灯光操作。

## 扩展方式

插件声明标准能力和元数据，服务无需了解厂商。新增灯光适配器可以复用静态/关闭控件。全新能力仍需要参数契约与对应界面实现；只声明一个名字不会自动生成操作界面。详见[插件开发指南](plugin-development.md)。

源码入口：[契约](../../src/ControlCenter.Contracts/StandardProtocol.cs)、[配置](../../src/LightOrder.Service/Configuration.cs)、[路由](../../src/LightOrder.Service/ServiceRuntime.cs)、[管道安全](../../src/LightOrder.Service/ServiceSecurity.cs)、[桌面壳](../../src/LightOrder.App/MainWindow.cs)。
