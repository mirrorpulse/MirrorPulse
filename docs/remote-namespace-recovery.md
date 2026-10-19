# Remote namespace recovery

The current dependency is CfSharp preview.7. Its default preservation policy retains
even clean remote Delete targets as durable conflicts; MirrorPulse preserves that
policy. Explicit product KeepRemote settlement uses its separate command lifecycle. Earlier automatic
Delete scenarios below describe the preview.6 baseline, not preview.7 acceptance.
See [preview compatibility](cfsharp-preview-compatibility.md) for the schema 8
migration, original-evidence compatibility and separate SDK recovery regressions.

Remote conflict commands require an observed public SDK outcome before recording
successful completion. A missing SDK conflict does not prove that the current
product command completed, and cannot be converted into `AlreadyApplied`.
Unavailable outcomes retain the original product conflict and any verified copy.
An explicit reconciliation path remains necessary when an SDK result was lost
without an owned terminal receipt; replay cannot manufacture that evidence.

After an actual SDK apply or dismissal, the Host persists the command's terminal
state before removing its in-memory conflict. A failed catalog write retains that
conflict and its original product projection. If recording failure also fails,
the original action error and the separate persistence error are both retained.

Schema 77 retains the exact command, conflict epoch, selected side and SDK publication
before KeepLocal, KeepRemote or KeepBoth execution. An observed terminal SDK return
and the command's resolved state commit together, even if the caller cancels after
that return. Replaying that same decision after
restart returns its original status without repeating the SDK decision. KeepBoth also
reopens and verifies the original private copy; damage cannot become a successful
replay or cause a replacement source to be read. A completed receipt cannot change
its intent or be reset through the generic command store. Upgrading an older catalog
does not invent receipts for historical commands. A result lost before this receipt
commits still requires separate reconciliation; the receipt proves neither current
native state nor completion of a protected member's deletion lifecycle.

The schema 78 explicit-delete storage boundary retains a separate KeepRemote
decision, its exact SDK input fingerprint and complete protected subtree before
invocation. It preserves the original poll outcome and package policy. Its own
batch fence and original member receipts cannot be adopted by controlled deletion
APIs, another command or a generic resolved-state write. Member settlement requires
the owned SDK outcome, actual native absence, exact public tombstones and fresh
protected-parent facts. At most 64 children-first observations commit together with
their names, retirement receipts and progress. The fence remains until every sealed
member and the final parent verification are complete. Host decisions now pass the
original command to the shared namespace actor and current execution role. They seal
the complete actual tree before calling the public SDK resolution API, then verify
native absence, exact public tombstones, conflict removal and the protected parent.
An owned start can reconcile a lost return only after those actual facts agree.
Missing conflict identity alone still supplies no successful outcome.
The runtime rejects trees larger than preview.7's 4096-member native Delete budget
before creating a product deletion owner. Other protected namespace operation kinds
require their own product lifecycle and cannot use this Delete executor's role.

The Host reuses an existing Delete command when the user continues that conflict.
Startup examines a bounded original command before a root's current-tree audit;
incomplete pages keep the root closed. A new role may reconcile an already deleted
tree through read-only inspection of its original protected parent and exact SDK
tombstones. It cannot use an old role to execute another native deletion. Surviving
members with an unresolved SDK conflict still require their original role or further
explicit role recovery. These execution paths require fresh disposable native and
default Host acceptance.
If all deletion members and the final parent verification already committed, a lost
outer command receipt is reconstructed from that exact durable disposition. Later
legitimate parent-role rotation neither authorizes another SDK invocation nor
invalidates this completed historical operation.

The six preview.6 automatic-delete gap fixtures are replaced by explicit-decision
scenarios under preview.7: default polling must first preserve the object and durable
conflict; only an explicit KeepRemote may remove it. They retain physical-absence,
official tombstone, immutable poll-history, failed-commit and bounded-page assertions.
Following namespace work and its Adapter cursor stay fenced until the decision's
members settle. Four additional mandatory fixtures exercise lost-return reconciliation
under a new role, a subsequent fresh current-tree audit, and recovery of a lost outer
receipt after legitimate parent-role rotation. Their original test results
must establish acceptance; successful compilation is insufficient.

The complete current-tree audit borrows one coherent inventory only for its awaited
native-inspection callback. The permission coordinator validates the entire current
catalog fingerprint before and after that callback; the borrowed admission expires
before initialization completion. Every object still receives fresh public identity,
native binding, owner, descriptor and direct-parent checks, followed by complete
local enumeration and a final anchor check. Initialization retains its separate
transactional inventory validation before releasing its owned fences. Standalone
member registration and directory publication keep their individual catalog checks.

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
Files with an original [SDK protocol admission](remote-file-move-protocol-admission.md)
use the preview.6 original-batch recovery path. The driver verifies the unchanged member,
current role, both protected parents and the original native object at either admitted
endpoint. It accepts only the original official source baseline or incoming target
projection, then delegates replay to CfSharp. The SDK authenticates its own opaque
preparation, local work and native state before deciding whether another rename,
projection or only outcome publication is required. Product admission alone cannot
establish success. The driver returns the actual SDK result and settles the product
only after the original entry's real progress advances.

The commit-gap fixtures decorate the public state-store transaction and fail its
original progress commit only after the SDK item has the target path and incoming
revision. They require the persisted progress to remain behind the actual native
binding and official item across SDK owners, then require production-driver recovery
of that original entry. A metadata-only target handle denies another native rename,
including a case-only source alias. The assertions require the same protocol/start,
actual successful SDK outcome, no spurious conflict, stable binding, cold content,
and product settlement before the Adapter cursor. These revised fixtures require
fresh native acceptance; earlier preview.5 defect-characterization results do not
prove preview.6 recovery.

A start without protocol admission cannot adopt an already-moved target. The legacy
guard fixtures use the older public product-start path under preview.6 and require
the original progress, start and pending fence to remain intact without inventing
protocol evidence. They do not claim a preview.5-binary database migration test.

Polling may reconstruct its durable original batch before checking the ordinary polling
fence. Its explicit replay predicate admits only the batch owning the pending remote move;
ordinary polling stays fenced and does not observe a newer source snapshot. The shared live
work owner still requires fresh root readiness. This query does not reopen a root or transfer
an old Host role's authority. Default Host composition, new-role recovery, streamed durable
intent, multiple roots and the remaining entry lifecycles are still pending.

The driver bounds batches to 65,536 entries. Prefix settlement currently validates historical
move references, and conflict recovery uses the decoded list API introduced in preview.5 because it
does not expose a batch-scoped conflict page. These paths do not constitute the complete
fairness and query-budget acceptance required for S06.
