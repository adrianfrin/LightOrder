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
    ├── OpenRgb → 本机 OpenRGB SDK → 允许列表内的实物设备（默认 ENE）
    ├── HardwareMonitor → 传感器与白名单风扇控制
    └── WindowsPolicy → Windows 策略与系统信息
```

这是实现说明，不是独立安全认证。进程隔离可以隔离许多故障，但不是操作系统沙箱。已安装插件继承服务权限，必须来自可信来源。

## 边界

命名管道为 `LightOrder.Control.v1`，仅允许安装所属 Windows SID、管理员和 SYSTEM，拒绝远程客户端。服务检查部署树和状态路径的所有者、写权限及重解析点。不能把普通用户可写的源码目录直接当作可信生产服务目录。

公开方法为 `app.snapshot`、`app.refresh`、`app.execute`、`app.plugins`、`profiles.read`、`profiles.write`、`app.automation.read` 和 `app.automation.write`。命令只能路由到发现了该设备且声明了对应能力的插件。公开设备 ID 为 `pluginId::localId`，插件收到本地 ID。不提供通用文件、Shell、注册表或任意进程执行 RPC。

WebView2 通过固定本地源提供内嵌资源，不开放特权 HTTP 服务。桌面壳禁用外部导航、下载、权限请求和任意宿主对象。

插件请求串行执行并有有限超时。超时表示结果可能未知，不会自动重放写入。断连插件的路由会失效。OpenRGB 的 `BackendDiscovering` 属于只读发现等待状态，服务每 15 秒重试发现，不重放命令。正常服务关闭先请求释放再销毁进程；强制结束或驱动故障不能证明恢复成功。

## 数据位置

| 位置 | 内容 |
| --- | --- |
| `%ProgramFiles%\LightOrder` | 应用、服务、运行时、安装入口和可信插件；普通用户仅有读取和执行权限。 |
| `%ProgramFiles%\LightOrder\plugins\<folder>\plugin.json` | 严格插件描述及方法白名单。 |
| `%ProgramData%\LightOrder\service.json` | 格式版本、安装所属 SID、显式启用的插件 ID；仅管理员/SYSTEM 可写。 |
| `%ProgramData%\LightOrder\profiles.json` | 服务保存的界面偏好和配置；原子替换，格式版本 1，上限 256 KiB。 |
| `%ProgramData%\LightOrder\automation.json` / `automation-journal.json` | 声明式规则（128 KiB）和持久执行日志（4 MiB）；受保护，原子替换。 |
| `%ProgramData%\LightOrder\FanControl\restoration-required.json` | 手动写入前刷盘的风扇基线/恢复意图；未解决时阻止自动接管。 |
| `%ProgramData%\LightOrder\WindowsPolicy\recovery-v1.json` | 服务适配器的策略恢复日志；启动时不自动回放。 |
| `%ProgramData%\LightOrder\OpenRgb` | 受管理 OpenRGB 的运行配置与诊断。 |
| `%LocalAppData%\LightOrder\WebView2` | 每个用户的 WebView2 数据。 |

当前生产配置由服务保存在 ProgramData，不使用旧应用的 LocalAppData 设置文件。`profiles.write` 只保存数据，不执行设备命令。界面的 **保存并应用** 分别调用保存和设备接口，并记录未完成的灯光操作。

## 服务控制循环

服务通过运行时锁串行处理界面命令、动画帧和自动化。250 ms 定时器以每秒 4 帧为目标生成呼吸/彩虹，最多 16 台设备；串行设备延迟可能降低实际帧率。声明 `lighting.static` 的适配器可获得服务的 `lighting.effect` 能力，帧仍经原静态能力路由，无需厂商分支。帧失败会停止该设备动画，不重试。

自动化约每秒检查一次，先持久化预留执行机会，再操作设备；不确定任务会暂停，不重放。定时使用准确本地分钟及保留的日期高水位。进程规则在启动/保存时建立已有实例基线，按所属 SID 筛选并使用 PID/创建时间身份。详见[自动化](automation.md)。

HardwareMonitor 自己串行执行每秒采样/控制。持续控制要求新鲜温度和转速反馈。挂起/间隔处理先恢复再保守重启；恢复意图是接管限制，不保证硬崩溃恢复。详见[散热](cooling.md)。

`restoreLightingOnStartup` 和 `restoreCoolingOnStartup` 是独立偏好，只有显式 true 才启用，默认 false。启动先发现再恢复；未完成灯光操作会阻止启动灯光恢复，不可用设备和失败会记录诊断、不重试。仅保存配置不会启动控制。

## 扩展方式

插件声明标准能力和元数据，服务无需了解厂商。新增静态颜色灯光适配器可以复用静态/关闭及服务呼吸/彩虹控件。全新能力仍需要参数契约与对应界面实现；只声明一个名字不会自动生成操作界面。详见[插件开发指南](plugin-development.md)。

源码入口：[契约](../../src/ControlCenter.Contracts/StandardProtocol.cs)、[配置](../../src/LightOrder.Service/Configuration.cs)、[路由](../../src/LightOrder.Service/ServiceRuntime.cs)、[管道安全](../../src/LightOrder.Service/ServiceSecurity.cs)、[桌面壳](../../src/LightOrder.App/MainWindow.cs)、[界面行为](../../src/LightOrder.App/Assets/app.js)、[界面样式](../../src/LightOrder.App/Assets/production.css)。参考 FlClash 设计的界面仍由原生 WPF/WebView2 承载。README 图片须由实际 App 采集并审阅，不能用设计预览代替。
