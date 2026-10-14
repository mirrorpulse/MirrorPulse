# Cloud Files Lifecycle

MirrorPulse has one current-user Cloud Files sync root. CfSharp owns its native
namespace and coordination database. MirrorPulse owns Adapter installation,
instance routing, user policy, and a separate product catalog.

## Startup and shutdown today

The owned sync-root protection coordinator locates original permission evidence
by the complete native binding. It completes a retained permission attempt
before rotating to the current Host namespace role, independently reads back
the root descriptor, and retains the root and ancestor handles until disposal.
It changes no ancestor ACL. A missing original binding with existing anchor
history requires recovery instead of a new capture. This boundary is available
to startup composition; default whole-tree initialization and readiness are
still pending and cannot be inferred from anchor verification alone.

Initial original-member registration composes the existing native registrar
under shared instance scheduling and namespace ownership. It verifies the
immutable capture seal and complete live inventory, registers the protected
anchor before its descendants, then checks the inventory again. Partial or
restart replay preserves the first member records while independently checking
current permission epochs. This path does not recapture relocated or born
members and does not declare the evolving product namespace ready.

`namespace protection initialize` invokes that original-inventory composition
through the actual Runtime and Host Control contract. Its initialization
admission fences upload, rescan and remote dispatch before capture or permission
writes. Existing capture, permission and member records retain their own state
machines; initialization stores correlation and an audit fingerprint. Final
native inventory checks precede one transaction that verifies the unchanged
catalog snapshots, stores historical completion and releases only the owned
initialization fences. Failure or cancellation retains admission for retry.
The Runtime retains the live anchor guard and drains initialization on disposal.

Pending initialization also exposes a separate catalog recovery lineage for
registered members at their committed current positions. It retains moved and
born provenance and unfinished original permission intents when parent and child
roles differ after interruption. This read requires the pending root owner and
does not relax ordinary current-role authorization or fabricate native checks.
The default startup recovery executor and final current-tree admission remain
required before this catalog boundary can enable source dispatch.

The Runtime can recover an individual registered original under a pending
initialization owner. It checks the actual current object and direct parent,
replays unfinished permission work and persists a new role rotation at the
completed current placement before applying it through CfSharp's protected
scope. First capture and membership stay unchanged. Accepted current identities
are read from the existing confirmed correlation rather than overwritten with
the first local identity. Individual recovery leaves initialization fences in
place; it is not the default startup executor or current-tree readiness.

Registered born members also have a read-back recovery boundary. After their
actual parent has recovered to the live role, it checks inherited protection and
records the existing V3 located epoch, retaining first birth, observation,
membership and committed movement. This boundary reads native metadata only;
ordinary files requiring conversion or a new ACL need the separate before-write
recovery path. Its verified epoch does not release initialization fences.

Registered-born writes have their own immutable before-write actions. They
retain the pending initializer, current member, placement, identity, parent
epoch and descriptor target without replacing first birth observations. Their
pending ownership also blocks ordinary controlled mutations in that root;
the admitted initializer can recover dependent parents. A drained completion
projects the existing V3 protection and releases only its action reservation in
one product transaction. Initialization remains fenced until full-tree audit.
Product catalog schema 51 creates empty action tables for older catalogs and
does not invent native writes, verification or readiness during migration.

The Runtime executes these actions under shared scheduling, namespace role and
permission application admission. It converts a matching ordinary born file
through CfSharp's public same-object scope, retains its current identity and
latest bytes, restores only owned descriptors, then drains before native
read-back and atomic product projection. A busy scope retains its action for
retry. Completed replay returns historical facts without touching later edits.
Default whole-tree startup and Host/CLI recovery orchestration remain required.
Default automatic startup, dynamic moved/born graph recovery and newly added
roots below an already protected anchor still require their full integration.

Current-tree auditing reads the complete live membership, completed retirements,
current placements, identities, protection and dispatch fences in one product
transaction. Missing names, disconnected members and damaged retirement receipts
cannot disappear from that inventory. The first capture remains historical;
completed moves and born members are audited at their current locations.

The native audit shares the Host's actor, execution role and permission admission,
retains the actual anchor guard, and uses the existing member inspector for each
object and its current direct parent. It then enumerates all actual local roots
and descendants, rejecting missing or unexpected entries, binding changes,
aliases, descriptor changes and public identity mismatches. It reads no source
content and changes no permissions or membership. A final catalog comparison
rejects concurrent changes to the retained graph or its fences. This receipt is
a fresh observation; Host admission and initialization fence completion remain
separate responsibilities, and later ordinary edits are not frozen by an audit.

Current-inventory initialization completion can run immediately after that final
audit inside the same actor, role and permission admission. Its product
transaction checks the complete supplied inventory again, retains every captured
original as either live or durably retired, rejects unfinished controlled work,
and commits the completion receipt together with release of only the admitted
initializer's root fences. Schema 52 distinguishes this receipt from the original
inventory receipt; migration preserves old receipt bytes and hashes. Replaying
a completed request supplies historical facts and leaves a newer initializer's
fences and later file edits untouched. Automatic startup still requires the
recovery orchestrator and fresh admission for the current Host lifecycle.

The Runtime also exposes explicit recovery of the registered current graph. It
persists the initializer before native work, recovers the anchor, and processes
live original and born members in parent order using their existing coordinators.
A compatible pending born action is resumed; a changed parent role requires an
immutable successor. Retired members remain historical and are excluded from
native traversal. Full current audit and completion run last, and the Runtime
retains the resulting anchor guard until disposal. A busy scope leaves the same
initializer queryable and retryable. Partially executed namespace operations,
new root bootstrap, Host/CLI orchestration and automatic startup still require
their respective integration boundaries.

Startup rebuilds missing remote conflict projections from CfSharp's decoded
conflict queries. A mutation with a confirmed remote result and an absent
official journal operation repairs its product projection before marking the
ledger acknowledged. Catalog failures keep that repair pending; recovery never
repeats the remote mutation. Rescan intents are excluded from journal absence
checks because they have a different acknowledgement boundary.

1. The Host checks the Windows version, loads product configuration and the
   installed Adapter topology, then derives separate sync-root and data-root paths.
   It captures the normal user in a Host-owned execution session. Worker RPC,
   polling, range reads and uploads use that normal identity even when called
   from local namespace work. Each bounded source range is read and disposed
   before returning a detached buffer. Creating the execution session does not
   apply ACLs or enable strict protection.
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

The execution identities are released after the Cloud Files session and Worker
owner have stopped; active source operations drain before token release. A
namespace identity cannot be created by relaxing permissions on failure.

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
Each deferred root request has its own durable generation. Entering discovery
captures those requests in a fingerprinted product snapshot, including an
explicit empty snapshot. Projection completes only the captured generation of
an active, dispatchable, unfenced root. Requests made during discovery or
acknowledgement survive the older scan; newly added and offline roots retain
their obligations. Restart repeats discovery and captures current requests
before acknowledgement. Migrated legacy markers become new administrative
requests, without evidence that an earlier scan covered them.
Rescan shares the per-instance scheduler with remote polling and streamed apply.
Native conversion, content confirmation and official state projection use
CfSharp `0.1.0-preview.5` public APIs behind the anti-corruption layer. The actual
registered policy must be `None`, as specified by the
[content-only policy](content-sync-policy.md). A rejected native precondition or
unfinished projection leaves `RemoteAccepted` and the scan generation pending.
The historical independent experiment validates mechanisms; final product
acceptance still requires the original native and installed reports. See
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
- A canceled hydration consumer leaves an already-dispatched bounded range
  correlated with its Worker session. The single response reader validates and
  drains that frame before the next range can use the channel. Abandoned bytes
  are disposed, and session shutdown faults the waiting drain. Cancellation
  during an incomplete control write still closes that range channel.
  Worker session failures have separate local sanitized diagnostics, so sync
  progress updates cannot erase their original recognized failure code.
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
