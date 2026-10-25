# Security / 安全

LightOrder runs trusted hardware plugins in a privileged local Windows service. Process isolation is not a sandbox. Install only plugins you trust, in the administrator-protected installation directory.

光序通过本机高权限服务运行受信硬件插件。进程隔离不是安全沙箱。仅安装来源可信的插件，程序和插件目录必须保持管理员写权限。

The named pipe accepts the installation owner, administrators and SYSTEM; it rejects remote clients. The service routes only declared capabilities to discovered devices. It does not expose arbitrary shell commands or raw register writes to the UI. Hard worker/OS crashes cannot guarantee cooling restoration.

Please report vulnerabilities privately using the repository's private security reporting feature when enabled. Do not post exploit details, credentials or personal hardware identifiers in public issues. If private reporting is unavailable, open a minimal issue requesting a private contact without including the vulnerability details.

Supported security updates initially target the latest release only. Unsigned release installers may trigger Windows SmartScreen; do not disable security protections to install an unverified binary. Verify release SHA-256 hashes and source provenance.
