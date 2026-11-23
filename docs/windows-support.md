# Windows support

MirrorPulse requires a Microsoft-supported **Windows 11 desktop** release,
**version 24H2 (build 26100) or later**, on **x64 or ARM64**. Home, Pro,
Enterprise, and Education are supported while the installed release is in
Microsoft support. Choose the package that matches your system architecture.

Windows 10, Windows 11 releases before 24H2, Windows Server, and 32-bit Windows
are unsupported. Use a supported desktop release for regular use.

On an unsupported system, `mp` product commands return exit code `8` and
`mp.platform.unsupported` in JSON mode. `mp --help` and `mp --version` remain
available.
