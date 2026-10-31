# Hardware support and verification

[简体中文](../zh-CN/hardware-support.md) · [Home](../../README.md)

Discovery and command acceptance do not establish physical verification. This table separates current source behavior from historical evidence. Tests and screenshots for a release must identify the exact package and environment.

| Component | Current implementation | Evidence and limits |
| --- | --- | --- |
| IT5702 motherboard lighting | HID identity checks; static/brightness/off without flash writes; service breathing/rainbow through static frames. | Historical user observation includes blue/dimmed and off on the development system. No physical color readback. The 0.2 installed service ran breathing/rainbow transport checks on 2026-10-31; physical animation remains unobserved. |
| ENE SMBus DRAM | Default OpenRGB SDK v3 adapter, exact ENE DRAM filtering, identity checks, static/off plus service effects. | Historical discovery/cache checks and user observation on two modules. Cache is software evidence. No new physical animation evidence. |
| Additional OpenRGB devices | Explicit detector and exact SDK device identity rules can enable supported physical classes such as GPU, keyboard, mouse, or lighting accessories. | Default list is empty. Code/tests do not prove any added model. Match exact engine/inventory and validate each model before claiming support. No blanket all-hardware claim. |
| Radiator/case control | Continuous control on B650M AORUS ELITE AX ICE, IT8689E instance 0; radiator control 0, case control 3. | A 0.2 installed-service smoke ran 30 seconds with 32 updates and verified baseline-register restoration on release. Long workloads and real suspend/resume remain untested. |
| Pump RPM | Read-only `pump.rpm`, fan channel 4 on the allowlisted board. | Historical wiring/RPM evidence without visual physical-header confirmation. No pump writes. Unknown boards retain raw sensor names. |
| Other sensors | LibreHardwareMonitor telemetry with finite values/null and real timestamps. | Hardware, driver, and permissions affect availability. Missing readings are not fabricated. |
| Windows policy | Power schemes, process priority/affinity, conditional restoration; automation binds owner-scoped process identities. | The installed 0.2 service triggered an owner-scoped disposable process, with independent priority/affinity readback and restoration. Cross-account denial was not tested on a second account. |
| System information | Fixed CIM queries for CPU, board, RAM, GPU, disk, OS. | Failed categories report unavailable. This inventory path does not request serial numbers, UUIDs, machine/user names, or command lines. |

## Cooling safeguards

- Production control is continuous, without a fixed time or update-count expiry. Updating a profile preserves the active control baseline.
- Minimum duty: radiator 40%, case 25%. Curves contain 2–12 monotonic points and end at 80°C / 100%. Manual mode retains an independent thermal floor.
- Temperature rises act immediately; falls are limited to 2°C per update. Startup/restart evaluates conservatively from 100°C.
- A one-second loop requires unique, fresh samples no older than three seconds. Missing, nonfinite, stale, ambiguous, or invalid CPU temperature/controlled-fan RPM triggers restoration. After eight seconds, controlled RPM below 200 triggers restoration.
- Suspend notifications and monotonic control gaps over five seconds restore defaults, clear stale telemetry, and require fresh data before conservative restart. Slow reads/writes are also checked for gaps. Sensor/control faults remain latched after release.
- Restoration tries both channels and compares five relevant control registers with the baseline. Unverified restoration keeps responsibility and blocks control.
- A protected restoration intent is flushed before the first manual write and cleared only after verified restoration. An unresolved marker blocks automatic takeover; do not delete it without checking firmware control through a documented recovery procedure.
- A controller lease prevents overlapping instances of this controller; it is not universal bus ownership for third-party software.

Normal release, EOF, and cooperative shutdown attempt restoration. A shutdown ACK precedes disposal and does not prove recovery. Hard process termination, hung native I/O, OS crashes, power loss, or driver failure cannot guarantee restoration. The intent file is evidence and an interlock, not a hardware watchdog. **New long-duration, suspend/resume, and recovery hardware tests remain pending.** See [cooling](cooling.md).

## Evidence for the new code

Service, automation, monitor, OpenRGB, motherboard, Windows policy, setup, and UI test suites passed locally on 2026-10-31. Native screenshots connect to the installed service. Actual package hashes, final regression results, and CI belong in the release verification record.

Clean-machine installation/full uninstall, physical animations, additional OpenRGB models, continuous thermal load, and real suspend/resume restoration still need evidence. See [verification](verification.md) and the [build and release guide](build-and-release.md).

## Reading results correctly

`accepted` means accepted/sent. `readbackVerified` must identify its source: Windows state, control registers, backend cache, or another source. Cache matches cannot prove physical output. `partial`, `failed`, and `unknown` remain visible; `simulated` is test evidence only. Animation metadata reports service command intent.

A build, fake test, real IPC test, installed-service test, hardware readback, and physical observation answer different questions. CI success does not establish installed hardware behavior. Release notes must list only checks that actually ran.

## Adding support

Provide exact controller/board model, firmware/software versions, read-only discovery, protocol source and license. Do not infer fan/pump mappings from similar boards. Add adapter allowlists and failure tests before controlled hardware acceptance. See [contributing](../../CONTRIBUTING.md), [plugin development](plugin-development.md), and [automation semantics](automation.md).
