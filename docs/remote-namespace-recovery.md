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

Catalog schema 62 retains a separate immutable remote rejection. `RejectRemoteAsync`
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
coordinator APIs are used by the managed move-batch driver described below. The
remaining entry lifecycles and default Host integration are still pending.

## Integration still in progress

Managed remote move dispatch uses these admissions. Remote birth, replacement,
retirement, and interrupted operations under a new Host role
must be integrated before default strict Host startup is enabled. An applied
entry count includes durable conflicts, and `AlreadyApplied` on replay is not
native-success evidence. Recovery must use the original binding and receipts;
the object currently occupying a pathname cannot replace missing historical facts.

## Managed remote batch boundaries

The strict runtime retains its current SDK owner and root work admission for the complete
remote apply call. It admits one entry of the original public batch at a time, preserves its
batch ID, fingerprint and conservative options, and returns the actual SDK result. The
current driver supports moves; birth, replacement and retirement batches remain rejected
until their product lifecycles are connected. This does not enable the default Host.

Before SDK execution, the move coordinator retains the original native member, parents,
position, identity and before-native start. After SDK execution, the driver verifies either
the actual target and official projection or the durable conflict and unchanged source.
An Adapter checkpoint is published only after this product settlement. Recovery after an
SDK commit settles the retained entry before allowing a later entry or cursor publication.
A start without committed SDK progress requires explicit outcome recovery. A version 2
start retains the actual official source revision observed before dispatch, separately
from the remote request's expected revision. A stale request can therefore become a
durable rejection against the unchanged actual source, without retaining its own fence
forever or rewriting either opaque revision.

The driver may resume an unmoved version 2 start only after fresh checks of its original
native binding, identity revision, source spelling, parents, protection epochs and actual
official revision. Directory recovery also requires the original public move proof to
report `NotMoved` and validates every sealed descendant. The actor, current role and
parent guards remain held while the SDK applies the original entry. The first start is
immutable. A moved, replaced, missing, reprotected or otherwise uncertain source cannot
use this route. Legacy starts remain readable and acquire no fabricated revision evidence.
Native success before SDK batch-progress commit still requires a separate recovery path.

Polling may reconstruct its durable original batch before checking the ordinary polling
fence. Its explicit replay predicate admits only the batch owning the pending remote move;
ordinary polling stays fenced and does not observe a newer source snapshot. The shared live
work owner still requires fresh root readiness. This query does not reopen a root or transfer
an old Host role's authority. Default Host composition, new-role recovery, streamed durable
intent, multiple roots and the remaining entry lifecycles are still pending.

The driver bounds batches to 65,536 entries. Prefix settlement currently validates historical
move references, and conflict recovery uses CfSharp preview.5's decoded list API because it
does not expose a batch-scoped conflict page. These paths do not constitute the complete
fairness and query-budget acceptance required for S06.
