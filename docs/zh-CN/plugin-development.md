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
| fan.profile | 共享FanProfile：模式、独立百分比、冷排/机箱曲线节点 |
| device.release | 空参数，经device.execute调用；仅实际支持时声明 |
| power.windowsScheme | 已安装电源计划schemeId |
| process.policy | pid、精确startTimeUtc、priority、可选十六进制affinityMask |
| system.info | 只读metadata.components，类型/名称/厂商/容量等 |

当前fan.profile是冷排/机箱双通道契约；其它拓扑应定义版本化能力及对应界面，不能仅添加任意名字就自动出现功能。水泵不提供写入能力。

`accepted`只表示命令发送，不表示实物验收。`applied`、`released`应在可回读场景带证据；部分失败必须明确报告。逐属性保存基线，释放时不要覆盖其它程序后来写入的设置。

内部有限状态使用枚举；可扩展协议标识使用StandardMethods/StandardCapabilities集中定义。未知写能力拒绝执行，不映射到默认写操作。

## 开发与验证

C#插件引用ControlCenter.Contracts，实现IDevicePlugin，通过PluginServer.RunAsync运行。`plugins/ExampleDevice`提供无硬件示例，也含仅供测试的崩溃/延时方法；生产清单只允许四个标准方法，真实安装默认不启用示例插件。

测试应覆盖无效输入、边界、身份变化、热拔插、部分写入、超时、恢复和EOF。纯测试通过不能替代硬件验收。新增支持必须说明确切型号、固件、控制范围、恢复方式和实测证据。更完整字段说明参见源码中的StandardDevice、DeviceCommand、FanProfile与各插件Discover实现。
