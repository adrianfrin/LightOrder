# Contributing / 贡献指南

Please open a focused issue before adding a new hardware write protocol. Include device model, USB/PCI IDs, firmware version, protocol source, and a reproducible read-only discovery log. Remove serial numbers and personal paths.

新增硬件写协议前，请先提交范围明确的 Issue，包含型号、硬件ID、固件版本、协议来源和可复现的只读发现日志；删除序列号与个人路径。

- Keep vendor knowledge inside plugins. The service and UI consume advertised capabilities.
- Use enums for closed state/mode sets; central constants or typed identifiers for extensible protocol keys.
- Preserve JSON protocol major compatibility. Reject unknown writable operations.
- Do not turn unsupported hardware into a successful mock result.
- Build and test without hardware first. Document actual hardware tests separately.
- Keep English and Chinese documentation aligned. Include screenshots for visible UI changes.

See [plugin development](docs/en/plugin-development.md) / [插件开发](docs/zh-CN/plugin-development.md).

Contributions are licensed under GPL-3.0-or-later. Preserve third-party attribution. Never submit credentials, private machine dumps, signed vendor binaries without redistribution rights, or instructions to disable platform protections.
