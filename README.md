# MirrorPulse

MirrorPulse brings local and remote storage into one Windows Explorer sync root.
MirrorPulse is in development. The first release will include Adapters for local
directories, WebDAV, and SMB.

## Getting started

Use a Microsoft-supported Windows 11 desktop release, version 24H2 or later,
and choose the package for your system's x64 or ARM64 architecture. See
[Windows support](docs/windows-support.md).

After installing MirrorPulse, run `mp --help` in PowerShell. The
[CLI reference](docs/cli.md) shows how to install a signed Adapter package,
connect a storage source, and check synchronization status. See
[capabilities and limits](docs/cli-capabilities.md) for supported operations.

Use the [contribution guide](CONTRIBUTING.md) to build from source.

## Privacy

MirrorPulse keeps configuration, credentials, logs, and synchronization state on the user's device. It does not upload telemetry or diagnostics. Synchronization transfers files only to sources the user configures and authorizes.

## License

MirrorPulse is available under the [Apache License 2.0](LICENSE). See
[NOTICE](NOTICE) and the [third-party notices](docs/third-party-notices.md) for
attribution.
