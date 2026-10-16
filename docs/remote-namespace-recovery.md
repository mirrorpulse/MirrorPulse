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

## Integration still in progress

Production remote batch dispatch does not yet use these admissions. Remote birth,
move settlement, retirement, and interrupted operations under a new Host role
must be integrated before default strict Host startup is enabled. An applied
entry count includes durable conflicts, and `AlreadyApplied` on replay is not
native-success evidence. Recovery must use the original binding and receipts;
the object currently occupying a pathname cannot replace missing historical facts.
