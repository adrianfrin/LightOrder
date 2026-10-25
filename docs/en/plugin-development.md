# Plugin development

[简体中文](../zh-CN/plugin-development.md)

Plugins are local executable processes, not DLLs loaded into the UI. Any language supporting UTF-8 JSON lines can implement the protocol. The service has no vendor or model switch. A plugin discovers devices, advertises capabilities, validates commands and owns hardware restoration.

## Manifest

Install a trusted plugin under `%ProgramFiles%/LightOrder/plugins/example/`. Files must be administrator-owned and not writable by ordinary users. Enable its ID in the protected `%ProgramData%/LightOrder/service.json` configuration; restart the service after releasing controls. Installing a privileged plugin requires administrator approval. There is no untrusted plugin marketplace or sandbox.

```json
{
  "id": "example.vendor",
  "name": "Example device adapter",
  "version": "1.0.0",
  "protocolMajor": 1,
  "entryPoint": "Example.Plugin.exe",
  "arguments": [],
  "permissions": ["hid:1234:5678"],
  "methods": ["device.discover", "device.execute", "telemetry.read", "device.release"],
  "enabled": true
}
```

All four standard methods are required. Entry points must stay within the plugin folder; absolute paths, `..`, reparse points and writable deployment trees are rejected. Permission strings document intent; they are not an OS sandbox.

## Wire protocol

Each stdin/stdout line contains one JSON-RPC 2.0 request or response. Stdout is protocol only; write diagnostics to stderr. Protocol major is negotiated with `plugin.hello`, returning `{id,version,protocolMajor:1}`. Requests carry numeric IDs; responses preserve them. The service has a 1 MiB UTF-8 frame bound, bounded metadata and timeouts. Never automatically retry writes after a timeout: their outcome is unknown.

```json
{"jsonrpc":"2.0","id":1,"method":"device.discover","params":{}}
```

```json
{"jsonrpc":"2.0","id":1,"result":[{"id":"light-1","name":"Case light","kind":"lighting","capabilities":["lighting.static","lighting.off"],"status":"ready","detail":"Static color; no physical readback","metadata":{"schemaVersion":1,"effects":["static","off"],"current":null,"stateSource":"unknown","readback":false}}],"error":null}
```

Error: `{"jsonrpc":"2.0","id":1,"result":null,"error":{"code":-32000,"message":"Device disconnected","data":"DeviceUnavailable"}}`.

The service prefixes public IDs with `pluginId::localId`. Plugins always receive their own local ID. Never derive identity solely from an enumeration index if hardware can reorder.

## Standard methods

| Method | Input | Result |
|---|---|---|
| `device.discover` | `{}` | `StandardDevice[]`; no settings are applied |
| `device.execute` | `{deviceId,capability,parameters}` | `ApplyResult {status,detail,readback?}` |
| `telemetry.read` | `{}` | Array of `{deviceId,name,type,value,unit,timestamp}` |
| `device.release` | `{}` on shutdown | `ApplyResult`; release all owned controls |
| `plugin.shutdown` | `{}` | ACK, followed by disposal and process exit |

`value` must be finite or null; timestamp is ISO8601 with offset. No NaN/Infinity or stale sample relabeled as current. Canonical telemetry names include `cpu.temperature`, `cpu.load`, `radiator.rpm`, `case.rpm`, `pump.rpm`; vendor sensors can use their stable identifier.

## Built-in capabilities

| Capability | Parameters / metadata |
|---|---|
| `lighting.static` | `{color:"#RRGGBB",brightness:0.0..1.0,operationId}` |
| `lighting.off` | `{operationId}` |
| `fan.profile` | Shared `FanProfile`: mode, independent percentages, radiatorCurve/caseCurve point arrays |
| `device.release` | `{}` via `device.execute`; advertise only when supported |
| `power.windowsScheme` | `{schemeId}` for an existing Windows scheme |
| `process.policy` | `{pid,startTimeUtc,priority,affinityMask?}`; validate exact process identity |
| `system.info` | Read-only `metadata.components[]` with kind/name/manufacturer/capacityBytes |

`fan.profile` is currently a two-channel profile contract for radiator/case. Other topologies need a new versioned capability and suitable UI; simply inventing an identifier does not add controls. Pump writes are not provided. Metadata schemas are implemented in the supplied plugin discovery methods.

`accepted` means the command was sent, not physical verification. `applied` or `released` must carry meaningful evidence where supported. Partial operations must return a partial/failure result. Preserve baseline independently for each owned property; do not overwrite later external changes on release.

Use closed enums for internal finite states and centralized `StandardMethods` / `StandardCapabilities` for extensible wire identifiers. Unknown capability names must fail closed; do not coerce them to a default write.

## C# starting point

Reference `src/ControlCenter.Contracts/ControlCenter.Contracts.csproj`, implement `IDevicePlugin`, and run `await PluginServer.RunAsync(plugin)`. `plugins/ExampleDevice` demonstrates discovery, standard execution and no-hardware state. It also includes test-only crash/delay methods; the production manifest exposes only the four standard methods. Keep it disabled in real installations.

Tests should cover malformed input, boundaries, identity changes, hot unplug, partial writes, timeout uncertainty, restoration and EOF/shutdown. Hardware-free tests are not physical device acceptance. Submit a support matrix with exact tested model/firmware and evidence.
