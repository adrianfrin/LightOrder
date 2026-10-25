# v0.1.0-preview verification

This is a preview release, not an assertion that all hardware or long-term cooling scenarios are supported.

Verified on Windows 11 x64 with B650M AORUS ELITE AX ICE, Ryzen 7 9700X, two ENE RGB DIMMs:

- Installed protected Windows service and ordinary-user App connection. Service discovered six device/function entries through the standard protocol.
- Motherboard and both RAM devices accepted static lighting commands; RAM cache matched. Prior user observation confirmed actual color/brightness changes.
- Standard service fan profile ran with live readings, then released with baseline register verification true. Pump remained read-only.
- Disposable process priority and affinity were applied, independently read back and restored; no remaining policy changes.
- Profile persistence through the protected service, actual native WebView2 screenshots, Chinese/English UI and hardware-free interface tests.
- Earlier bounded cooling run: 11 minutes, 643 control samples, CPU peak61.5°C, original five registers restored. This predates the service migration and does not replace the service-specific test above.
- Service49 checks, monitor21 fake-backend assertions, fan281 policy assertions, Windows policy9 scenarios, OpenRGB and motherboard protocol selftests pass.

Installation integration caught and fixed ZIP directory separator handling and UTF8-BOM manifests. The final installer checks a successful service snapshot before claiming ready. Final exact installer hash is in Release assets.

Not verified: clean OS VM installation, full uninstall cleanup (files retained by design), 30-minute expiry on real hardware, multi-hour workloads, sleep/resume, forced worker termination, OS crashes, all third-party devices. Dynamic breathing/rainbow and scheduled/application-triggered automation are not implemented. Installer is unsigned.
