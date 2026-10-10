# CfSharp Preview Compatibility Report

MirrorPulse pins `CfSharp` and `CfSharp.Storage.Sqlite` to `0.1.0-preview.5` from
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
format. Preview.5 remains a prerelease dependency until CfSharp 1.0.0 is published
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
renames for active and disabled roots across an owner restart. Host polling and
streamed remote changes use the router's current mapping within instance
scheduling. Runtime startup replays original pending proofs before starting the
upload pump; missing legacy proofs and recoverable per-root failures retain their
namespace fences. Explicit recovery shares the same instance scheduler as uploads
and remote application. `mp root recover OPERATION_ID` uses that same Host service
and reports incomplete recovery as pending. Callback authorization, preparation of
a new rename from the CLI and the full fault recovery matrix remain pending work.

## Strict tree protection status

Preview.5 packages identify source commit
`adad62f2eb232ec381e537cd5c4c9e45eccc1095`. The public protected local operation
contract retains one native object and official store lifetime across awaited
local work. It provides same-object conversion, inspection and Access-only
descriptor operations, with distinct native, projection, application and drain
receipts. Directory metadata mode does not freeze descendants. This package
upgrade alone does not establish MirrorPulse permission or product acceptance.
Local preparation identities use a null remote revision, retaining any existing
official ItemId and RemoteId. An empty revision string is still a revision value
and cannot introduce remote acceptance through this operation.

`MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync` adapts the public
scope to the existing permission coordinator contract. CfSharp performs
same-object conversion, fresh Access descriptor reads and writes, inspection and
draining. MirrorPulse retains ancestor names and reads the original object's owner
through a separate read-only metadata reference, comparing its complete native
volume/file ID with the scoped object before reading the owner. That reference
cannot write a DACL or open file content. Callers must persist original evidence
and hold product application admission before conversion or permission application. The Host does
not yet enable strict protection through this component.

The native component probe checks competing writes and hardlinks inside the
scope, real descriptor application, cancellation and callback failure, and
official-store reopen. It also checks cold-file metadata without hydration and
directory metadata with the original binding. It retains the Windows overwrite regression after scope release:
an edited placeholder can become an ordinary file, requiring fresh same-object
preparation before subsequent permission work. No permanent ordinary-file alias
freeze or whole-tree readiness is inferred from a completed scope.

The product policy requires protection of every managed root and descendant,
including ordinary files containing edits that have not been uploaded. Namespace
changes will use Host-controlled operations; in-place content editing remains a
requirement. Root delete callbacks alone do not provide recursive protection.

A disposable native gate now combines inherited delete ACLs with actual demand
hydration, public content confirmation and reopening the official store. This
component gate does not establish production protection or controlled operations.
Same-volume ordinary file moves can retain their previous ACL instead of the
destination's inherited protection. Controlled creation and import, persistent
permission ownership, concurrent access and crash recovery therefore also need
acceptance before enabling strict protection in the product.

`MirrorPulseNamespaceExecutionSession` provides an ephemeral execution role that
retains the current local user and flows across asynchronous namespace work. Its
owner drains active operations before disposal; source access and Worker RPC must
remain outside that context. A disposable native probe combines the role with
closed ACLs, hydration, content confirmation and store restart. Host permission
application and controlled-operation recovery remain pending. Creating a
session alone neither changes permissions nor enables product protection.

The session captures the Host's normal Windows identity at construction.
`RunNormalUserOperationAsync` explicitly uses that identity for a source boundary,
including when called from an active local namespace role. Both contexts drain
before their tokens are released. The owner must construct the session in its
normal context and wrap every actual source operation; returning a lazy object
does not authorize later reads outside that boundary. This API does not change
Worker process identity or prove the identity of native provider callbacks.

Native directory-population and cold-read acceptance uses a separate ordinary
consumer process. The consumer verifies its actual user and architecture and
rejects the namespace role. Provider-process directory enumeration returned no
source callback in the recorded native regression; it cannot substitute for the
external-consumer boundary.

The production Host now owns this session and routes all demand, polling, rescan,
upload, stat and mutation transports through `MirrorPulseNormalUserWorkerTransport`.
The boundary reads and disposes lazy source ranges as the normal user, returning
only a detached buffer bounded by the Worker range limit. The Host releases the
identities after Cloud Files and Worker teardown. This source integration does
not apply the strict namespace ACLs or deliver controlled local operations.

The ARM64 CI job runs the dedicated namespace suite using the native ARM64 test
process and disposable Cloud Files roots. Its report requires the execution
lifecycle, role, strict ACL, permission ownership, alias and protected-operation
probes, with no skips. Installed product
permission ownership and operation recovery still require separate acceptance.
