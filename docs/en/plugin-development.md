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
| `lighting.effect` | `{effect,color:"#RRGGBB",brightness:0..1,periodSeconds?:2..60,operationId?,phaseUtc?}`; effect is `breathing` or `rainbow`, service-generated frames |
| `fan.profile` | Shared `FanProfile`: mode, independent percentages, radiatorCurve/caseCurve point arrays |
| `device.release` | `{}` via `device.execute`; advertise only when supported |
| `power.windowsScheme` | `{schemeId}` for an existing Windows scheme |
| `process.policy` | `{pid,startTimeUtc,priority,affinityMask?}`; validate exact process identity |
| `system.info` | Read-only `metadata.components[]` with kind/name/manufacturer/capacityBytes |

`fan.profile` is currently a two-channel profile contract for radiator/case. Other topologies need a new versioned capability and suitable UI; simply inventing an identifier does not add controls. Pump writes are not provided. Metadata schemas are implemented in the supplied plugin discovery methods.

`accepted` means the command was sent, not physical verification. `applied` or `released` must carry meaningful evidence where supported. Partial operations must return a partial/failure result. Preserve baseline independently for each owned property; do not overwrite later external changes on release.

Use closed enums for internal finite states and centralized `StandardMethods` / `StandardCapabilities` for extensible wire identifiers. Unknown capability names must fail closed; do not coerce them to a default write.

## Service effects and automation

`lighting.effect` uses case-sensitive `breathing` or `rainbow`. Supply a valid `color` and `brightness` for both (rainbow generates its hue). The parser rejects missing `color` or `brightness`. `periodSeconds` defaults to 6; all numbers must be finite. Optional `phaseUtc` must be within one day of service time at validation and execution; automation replaces phase and operation IDs at each dispatch so saved commands remain reusable; direct calls still require a fresh phase. Optional `operationId` is at most 128 characters. Static/off use their own capabilities, not `lighting.effect` values.

A plugin advertising `lighting.static` is eligible for the service's standard effect adapter: discovery adds `lighting.effect`, `effects` entries, `animationSource:"service"`, and `animationFrameRate:4`. The service emits static frames at a target 4 fps, for at most 16 active devices; plugins need no animation timer. Failed frames stop that effect without retry. Static/off/release supersede that device's animation. Normal shutdown attempts a final static setting before release. This is command state, not physical readback.

A new adapter must keep stable identity and advertise only real capabilities. The service routes only to the discovering plugin and advertised capability (or the explicit static-to-effect adapter). UI controls and automation use the same standard `DeviceCommand`; unsupported capabilities do not gain controls. [Automation](automation.md) stores declarative command lists, durably reserves occurrences, and never invokes arbitrary scripts.

## OpenRGB device policy

`plugins/OpenRgb/backend.json` accepts `enableEneDram` (default true) and `additionalDevices` (default empty). Each extra rule requires an exact installed `detector`, SDK `type`, exact `name`, and at least `location` or `serial`. Optional `vendor` and `description` further constrain identity. Strings are case-sensitive; wildcards/control characters are forbidden. At most 128 rules are accepted. The selected detector authorizes probing; the SDK identity rule authorizes subsequent control. Enabling a detector does not permit every device it discovers.

Types follow OpenRGB: 0 motherboard, 1 memory, 2 GPU, 3 cooler, 4 LED strip, 5 keyboard, 6 mouse, 7 mousemat, 8 headset, 9 headset stand, 10 gamepad, 11 light, 12 speaker, 14 storage, 15 case, 16 microphone, 17 accessory, 18 keypad, 19 laptop, 20 monitor. Unknown and virtual types are rejected. For a keyboard, for example, fill `type:5` and the exact detector/name/location or serial observed from the specific trusted backend; do not guess a detector or reuse a sample identity.

The complete detector inventory must match the OpenRGB executable and contain every selected detector. The current validator also requires an `ENE SMBus DRAM` entry even when `enableEneDram:false`; that entry is disabled unless selected. All other unselected inventory entries are disabled. The adapter does not independently verify that the inventory is complete or matches the executable; the administrator must supply the matching inventory. HID safe mode stays on and ENE persistent saves stay off. Default ENE IDs remain compatible. Other IDs derive from type/location/name/serial; duplicate or incomplete identities are marked ambiguous and must not be written. The adapter rechecks device identity/layout before writes. An allowlisted device also needs a supported Direct/Static per-LED mode and at least one LED before the adapter advertises static/off control; allowlisting alone does not make it writable. This permits scoped adapters for supported physical classes, not an all-OpenRGB-hardware guarantee.

Sources: [device policy](../../plugins/OpenRgb/DevicePolicy.cs), [backend settings](../../plugins/OpenRgb/ManagedBackend.cs), [OpenRGB adapter](../../plugins/OpenRgb/OpenRgbPlugin.cs), [service effects](../../src/LightOrder.Service/LightingEffects.cs), and [capabilities](../../src/ControlCenter.Contracts/StandardCapabilities.cs). See [setup](getting-started.md#openrgb-and-pawnio) and the [evidence matrix](hardware-support.md).

## C# starting point

Reference `src/ControlCenter.Contracts/ControlCenter.Contracts.csproj`, implement `IDevicePlugin`, and run `await PluginServer.RunAsync(plugin)`. `plugins/ExampleDevice` demonstrates discovery, standard execution and no-hardware state. It also includes test-only crash/delay methods; the production manifest exposes only the four standard methods. Keep it disabled in real installations.

Tests should cover malformed input, boundaries, identity changes, hot unplug, partial writes, timeout uncertainty, restoration and EOF/shutdown. Hardware-free tests are not physical device acceptance. Submit a support matrix with exact tested model/firmware and evidence.
