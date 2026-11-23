# Security policy

## Reporting a vulnerability

Use [GitHub private vulnerability reporting](https://github.com/MirrorPulse/MirrorPulse/security/advisories/new)
to contact the repository maintainers. Do not put exploit details, credentials,
private keys, user files or diagnostic archives in public issues.

Include the affected commit/package version, Windows build and architecture,
expected behavior, reproducible steps using synthetic data, and the security
boundary involved. Share a minimal redacted reproduction first. Maintainers
will coordinate investigation and disclosure through the private report.

For ordinary bugs, use the issue form without sensitive data. A reproducible
CfSharp defect should identify its exact package version and native HRESULT;
keep library findings separate from MirrorPulse policy defects.

## Supported versions

MirrorPulse is in development and has no supported production release yet.
Security fixes target the current development branch. Published preview
packages are not a substitute for production acceptance.

A future release will publish its support window. See
[capabilities and limits](docs/cli-capabilities.md) for current product behavior.

## Data and signing

MirrorPulse does not automatically upload telemetry or diagnostic data.
Synchronization communicates with user-authorized storage. Diagnostic export
is local and must be reviewed before it is shared.

Do not commit tokens, passwords or private signing material. Adapter inventory
signatures, MSIX testing certificates and Microsoft Store signing serve
different purposes. Signing proves package provenance and integrity, not
that executable code is harmless.

Adapter Workers run with the current Windows user's access. Install packages
from a publisher you trust.
