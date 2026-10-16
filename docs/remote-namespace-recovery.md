# Remote namespace recovery

CfSharp owns remote batch execution, decoded remote conflicts, native file
operations, and its official SQLite projection. MirrorPulse retains product
membership, stable native bindings, current protected locations, and Adapter
instance ownership outside that store.

## Move admission

A version 2 member move can retain its original remote entry: Adapter instance,
batch ID and SHA256 fingerprint, total entry count, ordinal, change ID, incoming
and previous revisions, and the actual original native identity revision. The
existing admission retains the child binding, both parent bindings and committed
positions, public item identity, original protection, and directory move evidence.
The batch reference does not prove that a native operation succeeded.

Catalog schema 59 adds a separate unique index for remote entry ownership.

Catalog schema 60 also retains the public identity of an existing directory placeholder
before its first permission write. Files keep version 1 identity records; directories
use version 2. This is metadata inspection within the public protected scope, without
conversion, hydration, revision acceptance or journal acknowledgement. Ordinary
directories keep no identity record. Historical directories with no original identity
remain unresolved; migration never adopts a later identity from their current path.

The sync-root anchor belongs to the Cloud Files registration, rather than a remote
member. Its permission work validates the original root binding, owner and access
descriptor without requiring or retaining a remote placeholder identity. Windows
placeholder classification alone does not give an anchor a remote member identity.
This distinction does not relax identity checks for directories below the anchor.

The native move matrix keeps clean movement and conservative watcher conflicts
separate. The clean file cases start their live feed after initial protection;
the conflict cases retain a feed throughout protection and observe a file metadata
notification. CfSharp may classify that notification as pending content. Its
default apply then preserves the original object and journal. MP must refuse false
settlement and another native dispatch. These component cases do not establish
that production startup can discard those notifications or that pending conflict
admissions are recovered; that dispatcher work remains required.
Admission and index insertion share one transaction. Reusing a batch ordinal or
change ID for another move, changing the batch fingerprint, and replaying an
ordinary move ID as a remote move are rejected. Reading a remote admission
requires its original index; reading an ordinary admission rejects an unexpected
remote owner. Upgrading an older catalog creates an empty index and preserves
ordinary move provenance.

`PrepareRemoteAsync` performs the same fresh native child and parent checks as
controlled movement and retains the batch reference before any native move. Both
endpoints must stay in the original active Adapter root. Directory preparation
uses public CfSharp move evidence and seals the existing product subtree.
Exact admission replay returns historical evidence; it does not refresh native
authorization or start an operation.

The ordinary `ExecuteAsync` entry point refuses remote admissions. The original
remote batch must own execution and settlement. This prevents a second controlled
move from changing the source path before CfSharp evaluates the original entry.
Native observations retain the original placeholder identity revision, while the
official projection must match the incoming batch revision. These observations
do not authorize acknowledging a local content upload.

## Remote start and settlement

`BeginRemoteAsync` checks the exact public batch boundary, both protected parents,
the original child identity and projection, and the sealed directory subtree
before recording a start. An existing start requires outcome recovery; it does
not authorize dispatching again. Offline or rerouted roots retain their original
intent without entering native work.

After the original CfSharp batch has processed this entry, `SettleRemoteAsync`
checks its public fingerprint, entry count, ordinal and last change ID. It then
inspects the original binding at the target and commits product membership using
the existing move receipts. It never issues a second native move. Case-only file
renames also require the actual target spelling from public local enumeration.
Directory settlement uses the original public move proof and CfSharp's subtree
path reconciliation. A missing original start cannot be recreated after movement.

Public progress counts durable conflicts too. It is only a sequencing check;
the fresh native binding, original identity, parent protection and incoming
official revision must also agree. These APIs do not resolve a remote conflict,
publish an Adapter cursor, or acknowledge local content. A completed replay
returns history after validating its original batch reference.

## Preserved, unmoved conflicts

Catalog schema 61 retains a separate immutable remote rejection. `RejectRemoteAsync`
requires the original batch boundary and start, a matching unresolved decoded SDK
conflict, and fresh original child and parent bindings, protection and source
projection. Case-only movement requires the actual old spelling. A directory also
requires its original public move proof to report `NotMoved` and a complete audit
of the sealed source subtree. An absent source, occupied target, changed revision
or unknown native outcome retains the pending admission.

Recording rejection releases only that operation's pending row, reserved target
and exclusive predecessor claim in one product transaction. It retains the intent,
start, remote entry index and rejection history. Successful moves keep their
predecessor claims; a rejected attempt does not consume or enter the completed
relocation chain. Migration preserves existing claims without inventing rejection
or native evidence, and the current schema does not repair a missing claim.

A rejected replay returns history and cannot release a newer operation's fence.
The SDK conflict and original local journal remain unresolved. This does not move
or hydrate content, acknowledge an upload, or publish an Adapter cursor. These
coordinator APIs still require production batch-driver and Host integration.

## Integration still in progress

Production remote batch dispatch does not yet use these admissions. Remote birth,
move settlement, retirement, and interrupted operations under a new Host role
must be integrated before default strict Host startup is enabled. An applied
entry count includes durable conflicts, and `AlreadyApplied` on replay is not
native-success evidence. Recovery must use the original binding and receipts;
the object currently occupying a pathname cannot replace missing historical facts.
