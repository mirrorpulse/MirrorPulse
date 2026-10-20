# Persistent namespace transfers

Cross-root and cross-instance moves require a retained copy, verification and
source-retirement lifecycle. The ordinary same-root move ledger does not change
an object's first root or birth provenance. A copied destination needs a separate
member and public identity. Worker V2 root routing alone does not implement this
product workflow.

Schema 80 adds the first file-copy admission and its original upload receipt.
`PrepareNamespaceTransferAsync` atomically retains the immutable protected source,
source parent, target parent, both backend scopes, private content ID, length,
whole-file SHA256 and distinct copy/delete effect IDs. It reserves both entire
roots. Both dispatch fencing and startup pending-operation discovery validate the
plan, both root indexes and both effect indexes. A missing index or orphan cannot
silently make a root ready. Historical reads preserve the original proof; they do
not refresh native authorization.

Route scope hashes bind the installation, non-secret instance configuration,
credential references, root key and identity scope. Configuration values and
credential values are not copied into the transfer record. A fresh start checks
the current catalog topology, both enabled instances and active roots, exact
member/public identities, protected parent lineages and unoccupied target name.
A legacy V1 instance must have exactly one configured root, including disabled
roots. Different roots may belong to the same instance or different instances.
The caller must also hold the namespace actor, ordered instance admissions and
fresh native guards; catalog facts alone never grant native access.

The first copy requires a V2 target with negotiated conditional-target support;
a V1 source remains separately routed. A legacy target's revision-null behavior
is not treated as proven no-overwrite support. The upload request also requires
the actual Worker client to reject a legacy channel before writing any frame,
so a claimed route version cannot substitute for negotiation. Supporting a
legacy target requires its own explicit capability and acceptance contract.

Reopening schema 80 requires all three original transfer tables. Missing tables
are corruption, including loss of all transfer tables; empty replacement history
would erase an original pending owner. Schema 79 migration creates the new tables
without inventing transfer admissions or altering existing member provenance.

`MirrorPulseTransferCopyExecutor` hashes the full retained private read-only stream
before atomically claiming the only first upload. It passes the original target
instance/root, root-relative path, copy operation ID, expected length/hash and
`DestinationMustBeAbsent=true` to the existing Worker upload contract. The Host
must hold an immutable private content handle throughout hashing and dispatch;
the content ID alone does not prove that staging exists or is durable. A successful
correlated upload revision is saved even if caller cancellation races the reply.

The [pinned Worker V2 specification](https://github.com/MirrorPulse/adapter-template/blob/0c6398635a7680691d4bcff418cbbd6b951ab0a4/spec/worker-v2.md)
does not promise unconditional exactly-once effects across provider restarts.
Copy-start without a saved original acceptance remains uncertain. Neither an
exception, cancellation, missing durable receipt, matching target hash nor a new
session authorizes another upload. A saved receipt is replayed as historical
state without dispatching again. Unsupported or rejected invocations also remain
conservative at this stage; an explicit recovery protocol must retain any proof
that a mutation could not have started.

Copy acceptance keeps both fences. It proves neither whole-target readback,
native destination birth, public SDK publication, content confirmation, source
retirement nor journal acknowledgement. There is deliberately no generic
`Resolved` transition or source-delete call in this first phase. P079/P080 remain
open until Host/Control/CLI integration, retained staging, target verification,
source retirement, directory manifests and every restart boundary are completed
and verified on clean Windows users.

## Original target verification

The transfer verifier requires the durable original upload acceptance before any
remote read. A matching file at the target cannot provide a lost upload receipt.
It first rechecks the admitted topology, scopes and protected member snapshots,
then checks the exact accepted target revision, enumerates the complete bounded
parent directory and captures its unique remote ID, type and length. Every
bounded content read carries the original accepted revision on the negotiated V2
channel. Legacy channels reject a pinned read before emitting a frame; ordinary
hydration requests keep their previous wire shape.

The complete SHA256 must match the retained source proof. A second metadata and
revision check must retain the same target ID and accepted revision. Short or
excess ranges, duplicate target entries, missing targets, cyclic or oversized
pages and changed identities cannot produce a saved verification. Empty files
require the exact zero length and SHA256 of empty content. The optional
verification fact is canonical and immutable, survives catalog owners, and keeps
both root fences. A failed verification save permits another readback, without
reuploading or deleting anything.

The target verification is historical. It does not promise that the target can
never change later, refresh source-native authorization, publish the destination
to CfSharp, delete the source or acknowledge original journal entries. Those
steps require the remaining original transfer lifecycle and current guards.

## Host preparation and copy boundary

The production Host supplies its normal-user Worker transports and the actual
connected channel versions. Preparation requires ordinary work admission for
both roots. Cold content is materialized before acquiring the ordered instance
gates, so hydration can use its own provider admission. Under the shared actor,
the Host rechecks the exact current role, members, public source projection,
native bindings and direct-parent permissions. The destination must be absent.
Both current catalog scopes and the actual target protocol are checked before
any cold source materialization; a positive startup dispatch snapshot cannot
authorize access to a subsequently disabled instance or inactive root.

Complete input is flushed and independently reopened under the private
`state/transfer-inputs` directory. Its content ID remains distinct from the
operation ID and from import/replacement storage. Reopening a catalog owner does
not discard these bytes. A damaged or missing input cannot start an upload;
neither copy acceptance nor target verification reclaims the private content.

The Host runtime exposes separate prepare, copy, target-verification and history
methods. Copy rechecks the current local source against the prepared whole
content before claiming its first upload. Pending execution admits both roots
atomically, including a mixed ready/closed inventory, and never reopens a root
without a fresh audit. Worker calls execute in the normal Host user context.
Historical accepted stages remain readable after a new role or offline instance;
they perform no Worker call and cannot refresh native authority.

These runtime phases are not yet a completed move or a Control/CLI transfer
contract. Both root fences remain. Local read sharing does not establish the
final writer/mapping protection needed for source deletion. Native target birth,
SDK publication/confirmation, source retirement, journal acknowledgement,
directory manifests and their fault/recovery matrices remain required.
