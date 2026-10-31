# 插件开发

[English](../en/plugin-development.md)

插件是独立可执行进程，不是加载进界面的DLL。任何能处理UTF-8 JSON行的语言都可以接入。服务不包含品牌判断；插件负责设备发现、能力声明、参数校验、实际控制和恢复。

## 清单与安装

可信插件放在 `%ProgramFiles%/LightOrder/plugins/example/`，由管理员安装。在受保护的 `%ProgramData%/LightOrder/service.json` 中启用插件ID，释放现有控制后重启服务。插件继承服务权限，进程隔离不是沙箱。

```json
{
  "id":"example.vendor",
  "name":"示例外设适配器",
  "version":"1.0.0",
  "protocolMajor":1,
  "entryPoint":"Example.Plugin.exe",
  "arguments":[],
  "permissions":["hid:1234:5678"],
  "methods":["device.discover","device.execute","telemetry.read","device.release"],
  "enabled":true
}
```

四个标准方法均需实现。入口必须位于插件目录，禁止绝对路径、`..`、重解析点和普通用户可写的安装目录。permissions只是权限意图说明，不是操作系统强制沙箱。

## 协议

stdin/stdout每行一个JSON-RPC 2.0对象，stdout只放协议，诊断写stderr。`plugin.hello`返回 `{id,version,protocolMajor:1}`，必须与清单匹配。响应保留请求的数字id。服务限制UTF-8帧最大1MiB，并限制元数据及超时。写入超时代表结果未知，不得自动重发。

```json
{"jsonrpc":"2.0","id":1,"method":"device.discover","params":{}}
```

```json
{"jsonrpc":"2.0","id":1,"result":[{"id":"light-1","name":"机箱灯光","kind":"lighting","capabilities":["lighting.static","lighting.off"],"status":"ready","detail":"不支持物理颜色回读","metadata":{"schemaVersion":1,"effects":["static","off"],"current":null,"stateSource":"unknown","readback":false}}],"error":null}
```

失败返回 `error:{code:-32000,message:"可读原因",data:"错误类别"}`，result为null。服务对外使用`插件ID::本地设备ID`；插件始终接收本地ID。设备可能重排时不能只用枚举序号标识。

## 方法与能力

- `device.discover {}` → `StandardDevice[]`，不能自动应用设置。
- `device.execute {deviceId,capability,parameters}` → `ApplyResult {status,detail,readback?}`。
- `telemetry.read {}` → `{deviceId,name,type,value,unit,timestamp}[]`；value只能有限数值或null，时间为带时区ISO8601。
- `device.release {}` → 释放全部拥有的控制，返回恢复证据。
- `plugin.shutdown`先ACK再清理退出；ACK本身不能证明恢复成功。

| 能力 | 参数 |
|---|---|
| lighting.static | color为#RRGGBB，brightness为0–1，operationId用于去重 |
| lighting.off | operationId |
| lighting.effect | `{effect:"breathing"或"rainbow",color:"#RRGGBB",brightness:0..1,periodSeconds?:2..60,operationId?,phaseUtc?}`；服务生成静态帧 |
| fan.profile | 共享FanProfile：模式、独立百分比、冷排/机箱曲线节点 |
| device.release | 空参数，经device.execute调用；仅实际支持时声明 |
| power.windowsScheme | 已安装电源计划schemeId |
| process.policy | pid、精确startTimeUtc、priority、可选十六进制affinityMask |
| system.info | 只读metadata.components，类型/名称/厂商/容量等 |

当前fan.profile是冷排/机箱双通道契约；其它拓扑应定义版本化能力及对应界面，不能仅添加任意名字就自动出现功能。水泵不提供写入能力。

`accepted`只表示命令发送，不表示实物验收。`applied`、`released`应在可回读场景带证据；部分失败必须明确报告。逐属性保存基线，释放时不要覆盖其它程序后来写入的设置。

内部有限状态使用枚举；可扩展协议标识使用StandardMethods/StandardCapabilities集中定义。未知写能力拒绝执行，不映射到默认写操作。

## 服务灯效与自动化

`lighting.effect` 只接受区分大小写的 `breathing` 或 `rainbow`。调用方应为两者显式提供有效 `color` 与 `brightness`（彩虹自行生成色相）。解析器拒绝缺少 `color` 或 `brightness`。`periodSeconds` 默认 6，所有数字必须有限。可选 `phaseUtc` 在校验和执行时与服务时间相差均不得超过一天；自动化每次派发会替换相位和操作编号，使保存动作可重复使用；直接调用仍须提供新鲜相位。可选 `operationId` 最长 128 字符。静态/关闭使用各自能力，不是 `lighting.effect` 的取值。

声明 `lighting.static` 的插件可使用服务标准灯效适配：发现结果增加 `lighting.effect`、effects 条目、`animationSource:"service"` 和 `animationFrameRate:4`。服务以每秒 4 帧为目标输出静态帧，最多 16 台设备；插件无需动画计时器。失败后停止该效果，不重试。静态、关闭或释放会替代该设备动画。正常关闭尝试最终静态设置再释放。这些是命令状态，不是物理回读。

新适配器应保留稳定身份，只声明实际能力。服务只路由到发现设备的插件及其声明能力（或明确的静态转动画适配）。界面与自动化共用标准 DeviceCommand；不支持的能力不会自动生成控件。[自动化](automation.md)保存声明式动作列表，先持久化预留，不执行任意脚本。

## OpenRGB 设备策略

`plugins/OpenRgb/backend.json` 支持 `enableEneDram`（默认 true）和 `additionalDevices`（默认空）。每条扩展规则必须填写准确的已安装 `detector`、SDK `type`、准确 `name`，以及 `location` 或 `serial` 至少一项。可选 `vendor`、`description` 进一步限制身份。字符串区分大小写，禁止通配符和控制字符，最多 128 条。检测器名称授权探测，SDK 身份规则授权后续控制；启用检测器不代表允许其发现的所有设备。

类型遵循 OpenRGB：0 主板、1 内存、2 显卡、3 散热器、4 灯带、5 键盘、6 鼠标、7 鼠标垫、8 耳机、9 耳机架、10 手柄、11 灯、12 音箱、14 存储、15 机箱、16 麦克风、17 配件、18 小键盘、19 笔记本、20 显示器。未知类型与虚拟设备拒绝。以键盘为例，使用 `type:5`，其余检测器、名称、位置或序列号必须来自该可信后端的准确发现结果，不能猜测或照抄示例身份。

完整检测器清单必须匹配 OpenRGB 程序，且包含全部选中检测器。当前校验即使在 `enableEneDram:false` 时也要求存在 `ENE SMBus DRAM` 条目；未选中时该条目被关闭。清单内其他未选中检测器也被关闭。适配器不会独立验证清单是否完整或是否匹配程序，管理员必须提供匹配清单。HID 安全模式保持开启，ENE 持久保存关闭。默认 ENE ID 保持兼容，其他 ID 根据类型/位置/名称/序列号生成。重复或不完整身份标记为歧义，不能写入；写入前复核身份和布局。允许列表内的设备还必须具有受支持的 Direct/Static 逐 LED 模式及至少一颗 LED，适配器才声明静态/关闭能力；加入列表本身不会使设备可写。这提供受限的实物设备适配，不保证全部 OpenRGB 硬件兼容。

源码：[设备策略](../../plugins/OpenRgb/DevicePolicy.cs)、[后端设置](../../plugins/OpenRgb/ManagedBackend.cs)、[OpenRGB 适配器](../../plugins/OpenRgb/OpenRgbPlugin.cs)、[服务灯效](../../src/LightOrder.Service/LightingEffects.cs)、[能力标识](../../src/ControlCenter.Contracts/StandardCapabilities.cs)。另见[安装配置](getting-started.md#openrgb-与-pawnio)和[证据矩阵](hardware-support.md)。

## 开发与验证

C#插件引用ControlCenter.Contracts，实现IDevicePlugin，通过PluginServer.RunAsync运行。`plugins/ExampleDevice`提供无硬件示例，也含仅供测试的崩溃/延时方法；生产清单只允许四个标准方法，真实安装默认不启用示例插件。

测试应覆盖无效输入、边界、身份变化、热拔插、部分写入、超时、恢复和EOF。纯测试通过不能替代硬件验收。新增支持必须说明确切型号、固件、控制范围、恢复方式和实测证据。更完整字段说明参见源码中的StandardDevice、DeviceCommand、FanProfile与各插件Discover实现。
