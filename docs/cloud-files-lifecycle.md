# Cloud Files Lifecycle

MirrorPulse has one current-user Cloud Files sync root. CfSharp owns its native
namespace and coordination database. MirrorPulse owns Adapter installation,
instance routing, user policy, and a separate product catalog.

## Startup and shutdown today

Startup rebuilds missing remote conflict projections from CfSharp's decoded
conflict queries. A mutation with a confirmed remote result and an absent
official journal operation repairs its product projection before marking the
ledger acknowledged. Catalog failures keep that repair pending; recovery never
repeats the remote mutation. Rescan intents are excluded from journal absence
checks because they have a different acknowledgement boundary.

1. The Host checks the Windows version, loads product configuration and the
   installed Adapter topology, then derives separate sync-root and data-root paths.
2. `MirrorPulseCloudHostSession` obtains the current-user owner lock and ensures
   the Shell and CfSharp registrations agree with its stable root identity.
3. The session starts one `CloudFileSystem` with the official
   `CfSharp.Storage.Sqlite` factory. Its demand provider routes first-level
   directories to installed, enabled Adapter instances. Without configured
   Adapters, it exposes an empty root.
4. The Host starts one isolated Worker process and current-user Named Pipe for
   each enabled instance. Disabled instances retain visible offline directories.
5. Cancellation stops the Workers, disposes the CfSharp session, and releases
   the owner lock.
   Registration and the SQLite database remain for the next run. Explicit
   account removal has a separate unregister path.

The WinUI install flow verifies and registers a signed `.mpadapter` package
before creating an instance. Current packages carry their signed inventory at
`META-INF/mirrorpulse/signature.json`, so a local package can be installed
without a neighboring signature file. Older releases with a detached
`.signature.json` remain supported. The instance form supplies non-secret Worker
settings and first-level folder names through the current-user Host Pipe;
optional secrets are stored in Windows Credential Manager and only their
references enter the product catalog. New instances and version or startup
selection changes take effect after restarting MirrorPulse. Multiple instances
of the same Adapter require distinct first-level folder names.

## Integrated data paths

The journal pump handles `RequiresFullRescan` through CfSharp's public local
enumeration and inspection APIs. It completes discovery before missing-file
decisions, uploads fully available dirty files through the mutation ledger, and
uses CfSharp placeholder conversion or updates to commit the confirmed identity.
Only complete reconciliation of enabled roots acknowledges the official rescan
marker. Offline roots retain separate durable reconciliation obligations and
never receive uploads. Re-enabling a root consumes its obligation before normal
journal reads; it does not block unrelated online roots. Incomplete local
content and unsupported directory mutations remain durable blocked decisions;
they are not discarded or treated as successful remote changes.

The product catalog stores a scan generation and phase, not another Cloud Files
journal. Restart repeats public discovery and uses stable mutation intents to
avoid repeated remote writes. Permissions, cancellation and incomplete discovery
leave the scan pending without authorizing deletion. A projection failure after
official acknowledgement also retains the generation until recovery succeeds.
Rescan shares the per-instance scheduler with remote polling and streamed apply.
Native conversion and update use CfSharp coordination. The current preview.2
path requires a positive operation USN before final content verification and
conditional in-sync confirmation. A missing or rejected precondition leaves
`RemoteAccepted` and the scan generation pending. This boundary currently blocks
complete native full-rescan acceptance.

A disposable experiment validated exclusive protected references, complete
content/identity verification and same-handle confirmation. The planned managed
CfSharp operation will own that native boundary and official state recovery;
MirrorPulse retains its anti-corruption layer and remote acceptance ledger.
Production integration awaits that public API. See
[protected content confirmation](protected-content-confirmation.md) for evidence,
ordinary-file identity requirements, and remaining cancellation/recovery gates.

Remote mutations persist intent before dispatch. On restart, an uncertain upload
uses bounded remote reads and verifies its exact length and SHA-256 between two
revision checks. Matching bytes can converge even when the revision differs.
Different remote content becomes a retained conflict. A missing delete target
proves the delete postcondition. Moves without a confirmed revision remain
unresolved unless their postcondition can be verified; uncertainty does not
authorize another destructive request. Weak revision sources require additional
consistency policy and remain a documented limitation.

- First-level Adapter labels are routed within the same root. One instance may
  own multiple labels, and multiple instances of one Adapter have distinct
  identities. Duplicate active labels are rejected before population.
- CfSharp requests directory pages and file ranges through the MirrorPulse
  demand provider. It retains native continuation and hydration semantics.
  MirrorPulse translates callbacks to Adapter paths and protects instance
  boundaries, including volume-rooted paths supplied by Windows.
- CfSharp's local change feed is the authoritative pending upload journal.
  MirrorPulse maps entries to Worker commands and schedules retries; successful
  uploads are acknowledged with the accepted remote revision. Before an upload,
  MirrorPulse reads CfSharp's last mutually acknowledged item revision and
  compares it with a fresh Worker Stat. The acknowledged revision, never the
  fresh Stat value, is passed to the Worker's conditional upload. A mismatch
  persists an upload conflict in MP's separate catalog and stops automatic
  dispatch of that journal operation across restarts. The local content and
  remote file remain in place, and the Host exposes the pending conflict in
  status and notifications. Directory metadata notifications have no Worker
  transfer and are acknowledged separately so they do not leave a permanent
  pending-upload count. Conflict actions are available through the versioned
  Control client.
- Adapter remote batches map once to CfSharp batches. CfSharp persists applied
  progress and conflicts; MirrorPulse advances its named checkpoint only after
  a safe result. Replaying the same batch covers a crash between those writes.
- Active polling saves an immutable pending intent in the product catalog before
  applying it. A retry leaves the previous snapshot intact. On restart, the Host
  recreates and verifies the same batch before reading newer remote changes.
  After CfSharp reports the final safe cursor, the Host commits the candidate
  snapshot and clears the intent. A crash between these writes replays the batch;
  CfSharp remains the authority for already-applied entries and checkpoints.
- Background polling, manual refresh, streamed remote applies, and complete
  journal mutations share an instance scheduler. The journal gate includes
  remote acceptance, local confirmation, and acknowledgement, preventing a poll
  from applying the accepted upload in the middle of those boundaries.
  Different instances progress independently. A streamed
  batch cannot overtake a pending poll. Worker ingress uses a bounded ordered
  inbox so waiting for the scheduler never blocks the Pipe response reader.
- A failed journal command does not stop dispatch of later valid commands.
  Unacknowledged operations remain in the official feed. Acknowledgement failures
  have a distinct error code; source or catalog failures remain visible in the
  pump's in-memory health even when persistence is unavailable. Host status is
  degraded while that health reports a fault.
- Routing produces a decision for each journal observation. Root reconciliation,
  unknown paths, cross-root moves, and unsupported mutations are retained as
  blocked operations in the product catalog and exposed by sync status. They are
  never acknowledged as successful Worker mutations. Valid commands in the same
  feed batch continue. The later bounded journal paging work must also address
  batches filled entirely with unresolved blocked operations.
- A preserved conflict side has a flushed staging file, SHA-256, length and
  conflict-specific manifest in the private data root. The copy is usable only
  after its committed manifest and bytes verify. Failed saves retain an
  incomplete intent and never authorize the destructive conflict action.
  Replay verifies the existing manifest, or completes a flushed staging intent
  after a crash between the file move and manifest commit. A committed copy is
  reused even if its source changes. A wrong conflict identity or changed copy
  fails verification and is never overwritten.
- CfSharp suppresses local echoes from remote changes in the sync root.
  A local-directory Adapter separately suppresses its own source-tree watcher
  echoes because that is a different file tree.

## Recovery evidence and remaining gates

An opt-in Windows test uses an external consumer process to enumerate a
partial directory and read an online-only placeholder, then reopens the same
CfSharp database and reads again. Another test exits a separate process with
an open SQLite transaction and verifies committed journal, partial batch,
conflict, echo, and checkpoint data survive while the unfinished write does
not. It also performs a SQLite integrity check.

CfSharp `0.1.0-preview.2` provides the remote conflict and keep-local APIs
used here. The ARM64 CLI integration gate installs a signed Local Adapter,
reads a Cloud Files range, queues and drains an offline upload, applies a
remote batch, then verifies conflict and cursor state across Host restarts.
Interactive installed-MSIX Explorer validation and the planned WinUI migration
remain separate product gates.

Run `pwsh ./eng/verify-native.ps1` for the opt-in native checks. The ordinary CI
workflow runs x64 Release tests, x64/ARM64 CLI and Host publishes, and the
signed Local Adapter integration gate on ARM64. A manual CI dispatch also
checks native Cloud Files and installed MSIX identities.
