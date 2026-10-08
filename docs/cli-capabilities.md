# Product capability baseline

This inventory describes current implementation boundaries. It is not a
production-release completion claim.

- **Implemented**: a real entry and behavior exist with boundary evidence.
- **Partial**: an entry exists, but its completion conditions are not all met.
- **Unsupported**: no complete product path is connected.

## CLI and Control

| Capability | Status | CLI / Control | Current behavior |
| --- | --- | --- | --- |
| Host and sync status | Implemented | host status; status; sync status / host.status, sync.status | Reads the current Host snapshot; large-queue queries still need bounds. |
| Host auto-start / stop | Implemented | host start, stop; ordinary command auto-start | Installed MSIX alias and matching Host are exercised by the manual installation gate. |
| Host restart | Partial | host restart / host.restart | Stops and waits for the old process. A later ordinary command starts the replacement. |
| Adapter inventory | Implemented | adapter list / adapter.list | Reports installations, versions, instances and roots. |
| Managed root status | Implemented | root list / root.list | Reads Host-owned stable root IDs, labels, availability, required rescans and pending rename phases through Control. Native root rename and recovery execution are still being integrated. |
| Official signed file installation | Implemented | adapter install, update / adapter.install | Embedded and legacy detached signatures work; extracted-file hashes are verified. |
| Latest product update / default bundled packages | Unsupported | update currently takes a local path | CI aggregation downloads releases; product downloading and offline bundled registration are not connected. |
| External publisher / unsigned installation | Unsupported | developer-mode setting | Current Host uses the official trust anchor and signed-only installation. The setting does not enable unsigned packages. |
| Instance creation | Implemented | instance create / instance.create | Independent IDs and credential references exist; new instances start after Host restart. Configuration/secret validation needs hardening. |
| Configure / enable / select version | Partial | instance.* | Catalog changes require restart; live desired/effective behavior is incomplete. |
| Package removal | Partial | adapter remove, uninstall / adapter.remove | Reference checks exist; full removal, crash recovery and user-file preservation need acceptance. |
| Conflict inventory / snooze | Implemented | conflict list, show, snooze / conflict.list, conflict.snooze | show selects a record from list; paging and presentation paths are incomplete. |
| Conflict resolution | Partial | conflict resolve / conflict.resolve | Outcomes report pending/resolved/failed and a command ID; queued actions return exit 11. Remote Retry/DeleteLocal/DeleteRemote still need an executor; preserve-path/side are not fully propagated. |
| Operation get / watch / cancel | Partial | operation.* | watch reads one snapshot; cancel persists state without stopping the underlying work. |
| Settings / startup | Partial | config, developer-mode, startup / settings.get, settings.set | Persistence exists; some settings lack runtime consumers and no Windows startup task is registered. |
| Diagnostics | Partial | diagnostics / diagnostics.collect | Stored and exported logs use registered descriptions and validated fields; legacy messages and unknown fields are omitted. Export bounds and cancellation still need completion; no automatic upload. |
| CLI timeout / inputs | Partial | global options and instance commands | Timeout is parsed but not propagated. Credential helpers and strict parsing are not complete. |

Production entry points:
[CLI](../src/MirrorPulse.Cli/MirrorPulseCliHostOperations.cs) →
[Control client](../src/MirrorPulse.Control/Client/MirrorPulseControlClient.cs) →
[Host dispatch](../src/MirrorPulse.Host/MirrorPulseHostApplication.cs).
Installation uses the [production verifier](../src/MirrorPulse.Core/Packaging/SignedProcessAdapterInstaller.cs);
instance creation uses the [Host provisioner](../src/MirrorPulse.Core/Host/MirrorPulseAdapterInstanceProvisioner.cs).
The [remote conflict actions](../src/MirrorPulse.CloudFiles.CfSharp/MirrorPulseRemoteConflictActions.cs)
show which outcomes are queued rather than completed.

## File synchronization

| Capability | Status | Current behavior / evidence |
| --- | --- | --- |
| Directory pages and demand ranges | Implemented | Installed signed Workers and protocol fixtures exercise reads. Full Explorer image/thumbnail/retention acceptance remains open. |
| Multiple instances | Implemented | Signed aggregate tests start independent Local Workers with separate instance IDs. |
| Multiple roots per instance | Partial | Worker requests carry RootKey, with explicit demand and upload routing. The background poller still admits only one active root per instance. |
| Offline edits and upload replay | Partial | ARM64 signed Local CLI regression verifies queue 0 to 2 to 0. File and directory-create recovery retain accepted intent across restart; sync status exposes blocked reasons. File-chain aggregate execution, recursive move/delete and bounded fair paging still need completion. Local metadata-only changes are outside the content-sync policy. |
| Move / delete | Partial | Current official Workers implement file/directory mutations. Complete journal, cross-root, recursive and conflict safety is not proven. |
| Remote polling | Partial | Single-root polling retains retry snapshots and replays durable pending batches before observing newer changes. Pending root rename fences observation and replay; multi-root polling and native rename recovery still need completion. |
| Pin / free space | Unsupported | The availability helper exists, but no complete Control/CLI command is connected. |
| Explorer identity / icon | Partial | MSIX identity is tested separately from interactive Shell/custom-name acceptance; the registrar still uses a system icon. |

Worker paths are routed by the
[demand provider](../src/MirrorPulse.CloudFiles.CfSharp/MirrorPulseDemandProvider.cs)
and [process supervisor](../src/MirrorPulse.Core/Host/AdapterInstanceProcessSupervisor.cs).
CfSharp's official journal is consumed by the
[journal pump](../src/MirrorPulse.CloudFiles.CfSharp/MirrorPulseJournalUploadPump.cs).
Remote changes use the [poller](../src/MirrorPulse.CloudFiles.CfSharp/MirrorPulseActiveRemotePoller.cs)
and [batch coordinator](../src/MirrorPulse.CloudFiles.CfSharp/MirrorPulseRemoteBatchCoordinator.cs).
The [availability bridge](../src/MirrorPulse.CloudFiles.CfSharp/MirrorPulseAvailabilityPinBridge.cs)
is currently a helper, not a completed product command.

## Ownership and compatibility

Control schema 1 and CLI JSON schema 1 are separate contracts. Worker negotiation
is separate again. Changes must specify their compatibility window.

The CLI uses MirrorPulse.Control.Client. Existing WinUI pages are transitional:
some still read the product catalog or use the legacy status pipe. Migration
must remove those paths and use the same client, without duplicating sync or
credential logic.

CfSharp owns native Cloud Files state and its official SQLite store. The
MirrorPulse anti-corruption layer applies product policy; it does not replace
the library's journal or private tables. Current-user pipe access and one
process per instance do not constitute a filesystem sandbox.

## Evidence boundaries

Managed entry deletion is rejected by the provider, but this does not yet protect
the complete directory tree. An external recursive caller can remove ordinary
unconverted child files before the protected entry receives its delete callback.
Rejecting every provider delete callback does not intercept these ordinary files.
Whole-tree protection remains an open product requirement.

Unsent file-chain coalescing decisions preserve the official journal operation IDs
and are immutable across catalog restart. Operations already started cannot be
merged; decision members cannot be individually dispatched. Planning is not
yet connected to the upload pump: complete public journal pagination, remote
verification, local projection and acknowledgement of every original ID remain
required before the product can claim coalescing execution.

The catalog records whether mutation execution has ever started before dispatch.
That evidence survives a return to Prepared and catalog restart. Migrated records
without historical evidence remain Unknown; their current state cannot establish
that they were never sent. An explicit coalescing decision can supersede only
proven never-started Prepared journal intent with a matching complete observation
window, immutable original intent, common baseline and historical content binding.
The plan, original-operation ownership and Superseded state commit together.
Original intent remains readable, with its plan ID, and generic mutation recovery
excludes it. Superseding neither executes a Worker operation nor acknowledges the
official journal; aggregate execution and recovery still need implementation.

An inherited NTFS delete-denial probe preserves ordinary children and their latest
in-place edits, but it also blocks individual deletion and replacement-based saves.
That probe is a compatibility diagnostic; the product has not adopted its ACL.

Root rename intent fences local dispatch, rescans and remote application. The
catalog retains immutable original object evidence and successive rename history.
Native rename recovery still requires a public directory object binding and safe
subtree reconciliation from CfSharp; these persistence tests do not prove the
native operation is implemented.

Ordinary managed, explicit native, signed-package and installed-MSIX tests are
different gates. A successful ordinary run does not prove native or Explorer
tests executed. Missing environment prerequisites must produce skipped results.

The ARM64 signed Local CLI gate covers real Host/Worker/CfSharp transfer,
offline replay, conflicts and cursor/catalog persistence. FTP/SFTP process
tests and WebDAV/SMB fixtures establish their documented boundaries, not all
servers or complete offline Explorer workflows.

See [official Adapter capabilities](official-adapter-capabilities.md),
[Cloud Files lifecycle](cloud-files-lifecycle.md) and [CLI reference](cli.md).
