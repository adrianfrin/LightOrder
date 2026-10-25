# Hardware support and verification

[简体中文](../zh-CN/hardware-support.md) · [Home](../../README.md)

A device appearing in discovery is not evidence that every advertised operation has been physically verified. The table separates implementation from the available evidence. Release-specific tests must identify the exact binary and environment.

| Component | Implemented behavior | Evidence and limits |
| --- | --- | --- |
| IT5702 motherboard lighting | HID identity validation, static color/brightness and off across the adapter's channels; no flash/save command. | Earlier development-system records include user-observed blue/dimmed and off behavior. The adapter cannot read physical color. This is not broad IT5702 board certification or validation of a new installer. |
| ENE SMBus DRAM | OpenRGB SDK v3, DRAM type and exact ENE description filtering, identity checks, static/off. | Earlier records include discovery, cache checks, and user observation on two modules. Cache colors remain software evidence. Other memory controllers are not supported. |
| Radiator/case control | B650M AORUS ELITE AX ICE, IT8689E instance 0; radiator control 0 and case control 3. | Existing short, bounded hardware evidence applies to this mapping. Production migration tests use a fake backend; they do not establish installed-service hardware behavior. |
| Pump RPM | Read-only `pump.rpm` on the validated board, mapped to fan channel 4. | Based on historical wiring and measured RPM evidence, without visual physical-header confirmation. No pump writes. Unknown boards keep raw sensor names. |
| Other sensors | LibreHardwareMonitor telemetry, finite values or null, real timestamps. | Sensor and driver availability varies. A missing reading is not replaced with fabricated data. |
| Windows policy | Power schemes, process priority and supported affinity; conditional restoration. | Adapter tests use memory-only fakes. Real policy changes and installed-service behavior need separate evidence. |
| System information | Read-only CPU, board, RAM, GPU, disk, and OS metadata from fixed CIM queries. | Failed categories report unavailable. Serial numbers, UUIDs, machine/user names, and command lines are not requested by this inventory path. |
| GPU/keyboard/mouse lighting | No supplied supported adapter. | Detection by another product does not establish LightOrder support. |

## Cooling safeguards and limits

- Sessions end at 30 minutes or 1,800 control updates, whichever occurs first. Updating a live profile preserves its baseline and original deadline. There is no automatic renewal.
- Radiator minimum duty is 40%; case minimum is 25%. Curves are monotonic, contain 2–12 points, and end at 80°C / 100%. Manual mode retains an independent thermal floor.
- Temperature rises act immediately; falls are limited to 2°C per update. Startup evaluates conservatively from 100°C.
- Missing/nonfinite CPU temperature or controlled-fan tachometer readings trigger restoration. After an eight-second grace period, a controlled fan below 200 RPM triggers restoration.
- Release tries both channels independently and compares the five relevant control registers with the baseline. Failed readback retains restoration responsibility and disables new control.
- A controller lease prevents overlapping instances of this controller. This is not a universal bus-ownership mechanism for all third-party tools.

Normal release, EOF, and cooperative shutdown attempt restoration. A shutdown acknowledgement precedes disposal and is not proof of completed recovery. Hard worker termination, hung native I/O, OS crashes, power loss, and driver failure cannot guarantee restoration. **Sleep/resume, indefinite operation, and crash recovery have not been established by the production adapter migration.**

## Reading results correctly

`accepted` means a command was accepted or sent. `readbackVerified` must identify exactly what was read: Windows state, control registers, backend cache, or another source. A cache match cannot prove physical light output. `partial`, `failed`, and `unknown` must remain visible and must not be summarized as full success. `simulated` is test evidence only.

A build, fake test, live IPC test, installed-service test, hardware readback, and physical observation answer different questions. Public release notes should list which of these actually ran. The CI workflow covers builds and hardware-free tests; it installs no service or driver and exercises no hardware.

## Adding support

Submit the controller and board model, firmware/software versions, read-only discovery results, and a protocol source with its license. Do not infer a fan or pump mapping from a similar board. Add allowlists and failure tests in the adapter before any controlled hardware acceptance. See [contributing](../../CONTRIBUTING.md) and the [plugin guide](plugin-development.md).
