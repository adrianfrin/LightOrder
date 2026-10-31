# Verification scope

[简体中文](../zh-CN/verification.md) · [Home](../../README.md) · [Release checks](build-and-release.md) · [Hardware evidence](hardware-support.md)

The new 0.2 implementation adds service breathing/rainbow, durable schedule/process automation, continuous cooling safeguards, explicit OpenRGB device policies, startup restoration opt-ins, and full uninstall with optional preference retention. Source behavior is documented; it is not a claim that the final package has passed every test.

## Current code and tests

On 2026-10-31, the Windows 11 development system passed OpenRGB/motherboard self-tests, automation tests (99 assertions before the final dispatch regression), continuous-monitor tests (92 assertions), Windows policy tests (9 scenarios), setup lifecycle tests (42 checks), and UI checks. Service regression tests also cover ordinary snapshots updating fan session state without rescanning lighting or process inventories. See [commands and release checks](build-and-release.md). Final release results and exact hashes belong in the release record.

The 0.2 installer upgraded the existing installation successfully. An ordinary-user client discovered six entries, including both ENE DIMMs. Breathing and rainbow each stayed active for five seconds across three lighting endpoints, followed by saved-setting restoration; this is software/transport evidence, not physical observation. An owner-scoped disposable process trigger completed with independent priority/affinity readback and restoration. A 30-second continuous fan smoke completed with 32 updates and verified baseline-register restoration; the pump remained read-only. Native captures of six pages connected to the real service are included in the README.

Remaining evidence: clean-machine installation, full uninstall with both preference choices, shared-dependency/external-process preservation, physical breathing/rainbow, additional OpenRGB models, multi-hour thermal workloads, real suspend/resume, gap recovery, and injected hardware failures. Hard process termination, OS/driver failure, blocked I/O, and power loss cannot guarantee fan restoration.

## Historical v0.1.0-preview record

The previous verification document recorded the following on Windows 11 x64, B650M AORUS ELITE AX ICE, Ryzen 7 9700X, and two ENE RGB DIMMs. These observations were not repeated by this documentation change:

- Protected service installation and ordinary-user App connection; six device/function entries through the standard protocol.
- Motherboard and both DIMMs accepted static commands; RAM cache matched. Earlier user observation confirmed physical color/brightness changes.
- A service fan profile had live readings and baseline-register verification on release; pump stayed read-only.
- A disposable process had priority/affinity applied, independently read back, and restored.
- Protected profile persistence, native WebView2 screenshots, Chinese/English UI, and hardware-free interface checks.
- An earlier pre-service bounded run: 11 minutes, 643 samples, CPU peak 61.5°C, five registers restored. It is not continuous-control evidence.
- Historical reported counts: service 49 checks, monitor 21 fake assertions, fan policy 281 assertions, Windows policy 9 scenarios, plus OpenRGB/motherboard self-tests. These are not current suite counts or results.

Historical install integration fixed ZIP separators and BOM manifests. Current setup includes readiness checks and further lifecycle changes; it needs new package verification. Previous preview limitations around retained program files and unimplemented animations describe that version, not current behavior. Historical screenshots are not evidence for new pages/effects.

## Reporting a release

Publish only observed results, with exact installer/source hash, Windows/hardware/driver versions, commands, and failures. Distinguish build success, fake tests, live IPC, installation, register/cache readback, and physical observation. `accepted` and OpenRGB cache match do not prove light output. A shutdown ACK or deleted process does not prove restoration. Installer signing status must be stated for the actual asset `LightOrder-win-x64.exe`; do not infer signing from build success.
