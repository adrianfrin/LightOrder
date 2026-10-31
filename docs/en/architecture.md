# Architecture and local data

[简体中文](../zh-CN/architecture.md) · [Home](../../README.md)

LightOrder has three installed layers. The ordinary-user WPF/WebView2 desktop app presents device capabilities. A privileged Windows service owns discovery, routing, and persistence. Separate plugin processes own device-specific access and restoration.

```text
LightOrder.App (ordinary user; embedded local UI)
    │ JSON-RPC 2.0 over account-restricted named pipe
    ▼
LightOrder.Service (privileged Windows service)
    │ JSON-RPC 2.0 over each child's stdin/stdout
    ├── GigabyteIt5702 → supported HID lighting controller
    ├── OpenRgb → loopback OpenRGB SDK → allowlisted physical devices (ENE default)
    ├── HardwareMonitor → sensors and allowlisted fan controls
    └── WindowsPolicy → Windows policies and system information
```

This describes the implementation, not independent security certification. Process isolation contains many failures but is not an operating-system sandbox. Installed plugins inherit service privileges and must be trusted.

## Boundaries

The pipe is `LightOrder.Control.v1`. Access is limited to the installation owner's Windows SID, administrators, and SYSTEM; remote clients are rejected. The service validates its deployment tree and state paths, including owners, write permissions, and reparse points. A development build in a writable checkout cannot simply be started as a trusted production service.

The public methods are `app.snapshot`, `app.refresh`, `app.execute`, `app.plugins`, `profiles.read`, `profiles.write`, `app.automation.read`, and `app.automation.write`. Commands are routed only to a plugin that discovered the target and advertised the requested capability. Public device IDs are `pluginId::localId`; plugins receive local IDs. There is no generic file, shell, registry, or arbitrary-process execution RPC.

The WebView2 UI serves embedded assets through a fixed local origin. It does not expose a privileged HTTP server. External navigation, downloads, permissions, and arbitrary host objects are disabled in the shell.

Plugin calls are serial with finite timeouts. A timeout means the outcome may be unknown; writes are not automatically replayed. A disconnected plugin loses its routes. OpenRGB `BackendDiscovering` is a pending read-only discovery state: the service retries discovery every 15 seconds without replaying commands. Normal service shutdown requests release before disposal. Forced termination and driver faults cannot establish restoration.

## Locations

| Location | Contents |
| --- | --- |
| `%ProgramFiles%\LightOrder` | App, service, installed runtime, setup entry point, trusted plugins; ordinary users have read/execute access. |
| `%ProgramFiles%\LightOrder\plugins\<folder>\plugin.json` | Strict plugin descriptor and method whitelist. |
| `%ProgramData%\LightOrder\service.json` | Schema version, installation owner SID, explicitly enabled plugin IDs; administrator/SYSTEM protected. |
| `%ProgramData%\LightOrder\profiles.json` | Service-managed UI preferences and profiles, atomically replaced; schema version 1, at most 256 KiB. |
| `%ProgramData%\LightOrder\automation.json` / `automation-journal.json` | Declarative rules (128 KiB) and durable execution journal (4 MiB); protected atomic file replacement. |
| `%ProgramData%\LightOrder\FanControl\restoration-required.json` | Fan baseline/restoration intent, flushed before manual writes; unresolved intent blocks automatic takeover. |
| `%ProgramData%\LightOrder\WindowsPolicy\recovery-v1.json` | Policy recovery journal for the service adapter; not automatically replayed at startup. |
| `%ProgramData%\LightOrder\OpenRgb` | Managed OpenRGB working configurations and diagnostics. |
| `%LocalAppData%\LightOrder\WebView2` | Per-user WebView2 data. |

Current production profiles are service-managed under ProgramData, not the legacy application's LocalAppData settings file. Saving through `profiles.write` does not execute device commands. The UI's explicit **Save & apply** operation combines separate persistence and device calls and tracks incomplete lighting operations.

## Service control loops

The service serializes UI commands, animation frames, and automation ticks through its runtime gate. A 250 ms timer targets 4 fps for breathing/rainbow, capped at 16 devices; sequential device latency can reduce the rate. Adapters advertising `lighting.static` gain the service's `lighting.effect` capability. Frames route back through the existing static capability, without vendor branches. A failed frame stops that device's animation without retry.

Automation checks approximately once a second. It reserves execution durably before device side effects and pauses uncertain work rather than replaying it. Daily rules use exact local-minute matching and a retained date high-water mark. Process rules baseline existing instances on startup/save and use owner-SID-scoped PID/creation-time identities. See [automation](automation.md).

HardwareMonitor has its own serialized one-second sampling/control loop. Continuous control requires fresh temperature and fan feedback. Suspend/gap handling restores before conservative restart; the restoration intent is an interlock, not guaranteed hard-crash recovery. See [cooling](cooling.md).

`restoreLightingOnStartup` and `restoreCoolingOnStartup` are independent profile opt-ins, false unless explicitly true. Startup discovery precedes restoration. Incomplete pending lighting suppresses lighting restoration; unavailable devices and failures are diagnosed without retry. Saved profiles alone do not start control.

## Extensibility

A plugin declares standard capabilities and metadata; the service stays vendor-neutral. A new static-color lighting adapter can reuse Static/Off and service breathing/rainbow controls. An entirely new capability needs a defined parameter contract and a corresponding UI implementation; advertising an arbitrary name alone does not create controls. See the [plugin guide](plugin-development.md).

Source anchors: [contracts](../../src/ControlCenter.Contracts/StandardProtocol.cs), [configuration](../../src/LightOrder.Service/Configuration.cs), [routing](../../src/LightOrder.Service/ServiceRuntime.cs), [pipe security](../../src/LightOrder.Service/ServiceSecurity.cs), [desktop shell](../../src/LightOrder.App/MainWindow.cs), [UI behavior](../../src/LightOrder.App/Assets/app.js), and [UI styles](../../src/LightOrder.App/Assets/production.css). The FlClash-inspired interface remains a native WPF/WebView2 application; README image paths require actual reviewed App captures, not design previews.
