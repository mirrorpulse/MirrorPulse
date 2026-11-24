# MirrorPulse repository guidance

MirrorPulse is a Windows Cloud Files product for personal storage. Each edition
exposes one current-user sync root and keeps configuration, catalog, caches,
and diagnostics in its own private data root. The repository uses English for
code, public documentation, commit messages, and GitHub metadata.

## Ownership boundaries

- The Host owns the Cloud Files session, the official CfSharp SQLite store, the
  MirrorPulse product catalog, credentials, and Adapter Worker lifecycles.
- CLI and WinUI clients use the versioned `MirrorPulse.Control.Client` contract.
  Do not open the Host databases from a client or duplicate sync logic in UI.
- Keep CfSharp behind `MirrorPulse.CloudFiles.CfSharp`. Use its public, mature
  APIs and SQLite provider; retain the anti-corruption layer for product policy.
- Each enabled Adapter instance has its own Worker process and current-user
  Named Pipe. Installed `.mpadapter` packages contain the Worker executable and
  dependencies. The Host owns settings, credential references, and saved
  authorization results. Use the shared control contract for CLI and WinUI.
- The first release supports one declared root per Adapter package and deployment.
  A package can be deployed multiple times. Preserve stable IDs, isolated state,
  explicit disabled-root behavior, and recoverable legacy multi-root data.
- Standard and custom editions can run under the same Windows user. Preserve
  independent package identity, CLI alias, Host session, credentials, and
  Explorer registration through install, upgrade, and uninstall.

## Changes and verification

- Make each commit independently reviewable and state the behavior it changes.
- Prefer focused tests that exercise a real boundary. Use a clean Windows test
  user for Cloud Files registration or installed MSIX checks.
- Run the relevant locked restore, Release build, tests, and formatting gate.
  R1 desktop acceptance uses a clean Windows 11 ARM64 environment. Verify
  x64 builds, signed packages, publication, and actual Worker execution.
- Treat CfSharp preview behavior as version-specific. Report reproducible
  library defects with the package version and native error before adding a
  long-term workaround.
- Do not commit private keys, passwords, tokens, user files, or diagnostic data.
  Credential values may be accepted by CLI arguments but must not be echoed or
  recorded in ordinary logs.
