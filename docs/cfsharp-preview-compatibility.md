# CfSharp Preview Compatibility Report

MirrorPulse pins `CfSharp` and `CfSharp.Storage.Sqlite` to `0.1.0-preview.4` from
NuGet.org. The integration project owns CfSharp types and lifecycle; Core exposes
MirrorPulse contracts to Adapters and the product UI.

## Current boundary

| Area | Implemented behavior and evidence |
|---|---|
| Operating system | Windows 11 desktop 24H2 (build 26100) or later, while Microsoft-supported, x64/ARM64. Windows 10 and Server are excluded; see the [product support matrix](windows-support.md). CfSharp's broader API minimum does not lower the product minimum. |
| Architectures | The Host and CLI publish for `win-x64` and `win-arm64`. Windows 11 ARM64 CI exercises the signed Local Worker, CLI offline upload, remote apply and restart path. Protected confirmation has separate native evidence on ARM64 desktop and x64 Server. The manual native suite includes complete rescan recovery; native x64 desktop release acceptance remains open. |
| State | One official CfSharp SQLite database lives outside the single sync root. MirrorPulse keeps product catalog data separately and does not duplicate CfSharp's local journal, remote batches, conflicts, checkpoints, or echo records. |
| Registration | The Host starts a real CfSharp session with a current-user Shell and Cloud Files registration. Ordinary shutdown preserves registration and database state. |
| Demand | The integration provider maps bounded Adapter directory pages and range reads to CfSharp callbacks. A separate consumer process has enumerated an online-only placeholder, hydrated it, compared bytes, and read it after provider session restart. |
| Local changes | CfSharp's journal is the source for pending uploads, retry metadata, acknowledgement, and remote revision updates. |
| Remote changes | Adapter batches map to `CloudRemoteChangeBatch`; CfSharp owns application, partial progress, conflict IDs, echo suppression, and named checkpoints. |

The Host starts independent Worker EXEs for enabled instances and routes
directory pages, range reads, and supported local journal uploads through their
current-user Named Pipes. A signed Local Adapter release has been installed and
exercised with two simultaneous instances, separate first-level directories,
the CfSharp demand-provider boundary, range reads, and an upload. Shell registration in an
interactive installed MSIX session remains open. Active polling, durable pending
batch replay, the upload journal and full-rescan confirmation are integrated.
Their acceptance requires the same-commit native, signed and installed gates;
the complete protocol/file-operation release matrix remains open.

## Validation

The default CI gate runs locked restore, formatting, platform checks, Release
build and tests, and Host publishes for both architectures. On a Windows machine
with Cloud Files support, run `pwsh ./eng/verify-native.ps1` after the Release
build. The GitHub Actions workflow exposes this additional step through manual
dispatch. It is not part of pull-request CI because registration and external
consumer behavior need a suitable Windows user session.

The native set includes external-process directory enumeration and hydration,
official SQLite reopen, journal retry and acknowledgement, remote metadata echo
suppression, durable batch cursor behavior, and a second process that exits with
an open SQLite transaction. The recovery test checks committed journal, partial
remote batch, conflict, echo, and checkpoint records; it verifies the unfinished
transaction rolled back and `PRAGMA integrity_check` returned `ok`.

## Preview status

The earlier `CF-001`, `API-001`, and `API-002` gaps were retested after pinning
preview.2. MirrorPulse uses the public CfSharp batch and conflict contracts and
does not read or mutate CfSharp's private SQLite tables or conflict payload
format. Preview.4 remains a prerelease dependency until CfSharp 1.0.0 is published
and validated for the release candidate.

Preview.2 also exposes a confirmation limitation: coordination USNs can be zero,
and independently queried current/refreshed USNs were rejected in the isolated
conditional experiment. A protected same-handle alternative passed 15/15 native
cases per architecture. Preview.3 now publishes `CloudItemSnapshot.LocalBinding`
for ordinary files and placeholders, and `CloudFile.ConfirmUploadedContentAsync`
with guarded preparation, segmented protected reading and explicit native and
official-store projection receipts. Its content proof requires the actual root
policy to be exactly `None`; the library default remains `TrackAll`.

MirrorPulse registers the [content-only policy](content-sync-policy.md) and uses
the public managed confirmation contract. The package upgrade does
not establish complete product recovery or close the conditional-USN limitation.
See [protected content confirmation](protected-content-confirmation.md). Product
code keeps the anti-corruption layer and does not own a native confirmation reader.

Preview.4 packages identify source commit
`d9e0d7f8948156b70d01934c0df326c5ed43a682`. The public release adds finite
sequence scans through `CloudLocalChangeFeed.BeginScanAsync` and `ReadPageAsync`,
and recoverable directory moves through `CloudDirectory.PrepareMoveAsync` and
`ReconcileMoveAsync`. The official SQLite provider supplies the corresponding
paging and directory projection contracts. Product integration and native
acceptance of these capabilities remain separate from upgrading the dependency.

The managed root rename coordinator persists the exact public move proof before
external authorization, verifies its stable root binding on replay, and uses the
public reconciliation result before changing the product Label. Legacy records
without the original encoded proof remain fenced; recovery cannot replace them
with the identity currently found at a path. The native gate verifies external
renames for active and disabled roots across an owner restart. Host callback,
CLI command and remote polling integration are still separate pending work.
