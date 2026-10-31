# Third-party notices / 第三方声明

LightOrder source is GPL-3.0-or-later. Components retain their own copyrights and licenses. Binary releases must include the matching dependency-source assets listed below; a link alone is not a replacement for required corresponding source.

| Component | Version / source | License / scope |
|---|---|---|
| liquidctl RGB Fusion protocol | rgb_fusion2.py, CaseySJ, Jonas Malaco and contributors | GPL-3.0-or-later; derived IT5702 adapter attribution retained |
| LibreHardwareMonitorLib | 0.9.6, commit 3d331e3370efb858411f19511373eff65a218701 | MPL-2.0; unmodified NuGet library, [source](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701) |
| OpenRGB | 1.0, commit 81bbe18a84c2e507006f19dd252e397e40a56bfe | GPL-2.0-or-later; separate unmodified executable and SDK engine, corresponding source archive supplied with bundled releases |
| Qt | 6.8.3, QtBase / Qt5Compat / QtImageFormats | LGPL-3.0 / GPL alternatives and module notices; unmodified shared libraries, replaceable in installed runtime folder; corresponding module source archives supplied |
| PawnIO | 2.2.0, tag 2.2.0 / commit 5cdf470831fdfff3f7f1d06363ca6b230f3bf35a | GPL-2.0-or-later with upstream linking/device-IO exception; retain COPYING and README exception; source plus PawnPP submodule supplied |
| PawnPP | e64e4c37b2d8ba0d8ee57205faf8183aee12c438 | See upstream license in corresponding source archive |
| hidapi / libusb | runtime shipped with exact OpenRGB build | BSD/GPL alternatives for hidapi; LGPL-2.1-or-later for libusb; upstream notices retained in OpenRGB source |
| DirectX Shader Compiler | runtime shipped with OpenRGB Qt dependencies | Microsoft/LLVM open-source notices; [source and licenses](https://github.com/microsoft/DirectXShaderCompiler) |
| .NET runtime | SDK 10.0.402 self-contained published runtime | MIT and third-party notices from Microsoft .NET distribution |
| WebView2 SDK | 1.0.4314.35 | Microsoft WebView2 SDK license; evergreen browser runtime is separately installed, not bundled |
| LHM transitive libraries | NuGet lock/assets identify resolved versions: HidSharp, DiskInfoToolkit, RAMSPDToolkit-NDD, System.* | Their package licenses apply; no relicensing by LightOrder |

License texts are in `third-party/licenses`. OpenRGB source includes build definitions and bundled third-party headers. Qt shared libraries remain separate and replaceable; installing replacement binaries requires administrator access because all executable service dependencies are protected from unprivileged modification. LightOrder does not prohibit reverse engineering for debugging modifications to LGPL components.

## UI design inspiration / 界面设计灵感

The interface draws design inspiration from [FlClash](https://github.com/chen08209/FlClash), a GPL-3.0 project. This credits visual/product design inspiration. It does not assert copied FlClash code or a bundled FlClash dependency; any future code reuse must retain its actual source and applicable license notices.

界面设计参考 [FlClash](https://github.com/chen08209/FlClash)（GPL-3.0 项目）。此处致谢视觉与产品设计灵感，不表示复制代码或随包提供 FlClash 依赖；如后续复用代码，应按实际来源保留适用的许可证声明。

## 中文说明

主板协议适配保留liquidctl的GPL来源声明。硬件监控库维持MPL许可，OpenRGB是独立的GPL引擎，Qt共享库保留LGPL等上游条款。驱动PawnIO保留GPL及其特殊例外条款。发布包中二进制不因进程隔离而免除源码/许可证义务。

首次安装使用官方签名的PawnIO安装器；LightOrder不修改该驱动、不伪造签名、不要求关闭Windows安全功能。WebView2运行时不随包分发。确切包体、SHA-256、对应源码归档及已验收范围以Release记录为准。

Sources: [liquidctl](https://github.com/liquidctl/liquidctl), [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB), [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), [PawnIO](https://github.com/namazso/PawnIO), [Qt](https://download.qt.io/archive/qt/6.8/6.8.3/submodules/).
