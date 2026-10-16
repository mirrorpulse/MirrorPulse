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

## Integration still in progress

Production remote batch dispatch does not yet use these admissions. Remote birth,
move settlement, retirement, and interrupted operations under a new Host role
must be integrated before default strict Host startup is enabled. An applied
entry count includes durable conflicts, and `AlreadyApplied` on replay is not
native-success evidence. Recovery must use the original binding and receipts;
the object currently occupying a pathname cannot replace missing historical facts.
