# Persistent namespace transfers

Cross-root and cross-instance moves require a retained copy, verification and
source-retirement lifecycle. The ordinary same-root move ledger does not change
an object's first root or birth provenance. A copied destination needs a separate
member and public identity. Worker V2 root routing alone does not implement this
product workflow.

The current delivery baseline uses one declared root per Adapter instance and
retains transfers between distinct instances, including separately selected
packages and backends when their capabilities permit the operation. These
instances share the single current-user Windows sync root. Same-instance
transfers between several declared roots are deferred for review after the
current journal milestone. Existing root addressing and component evidence stay
compatible; a new multi-root package is not required for this baseline.

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

## Controlled file move and recovery

`mp namespace transfer move OPERATION_ID SOURCE_MEMBER_ID TARGET_PARENT_ID NAME`
starts one retained file transfer. Choose a unique operation ID and preserve it
for `mp namespace transfer resume OPERATION_ID`. Status and list remain read-only.
The CLI sends typed requests over the versioned Control pipe; only the Host opens
the product catalog or the official CfSharp SQLite store. The Host drives copy,
whole-target verification, protected destination publication, conditional source
deletion, local source retirement and original journal acknowledgement. An
ambiguous copy or delete remains pending for explicit reconciliation; resume
does not authorize another remote effect from matching target bytes.

The read-only list for one root uses derived indexes of the original pending
source and destination routes, plus the retained root reservation. It validates
the selected transfer's complete original records. Missing state or reservation
rows cannot turn that original into an empty result. An unrelated root's damaged
transfer does not prevent reading this root's status; global recovery integrity
still rejects that damage before publishing its full root snapshot. Status does
not grant native authority, live root readiness or a journal acknowledgement.
A state with no original operation has no trustworthy root, so even a root-local
status lookup retains the global orphan-state check and rejects that damage.

Global transfer recovery keeps its complete integrity check and original catalog
transaction, then reads pending IDs in ordered pages of 16 and validates transfer
graphs individually. It retains no list of all complete transfer graphs. A late
invalid owner prevents publication of the full root result. The route indexes
can be rebuilt from existing originals without changing receipts or the catalog
version. These changes do not bound the total global integrity or orphan scan or the other
namespace-owner inventories; those broader backlog limits remain open.

The current remote-source completion requires negotiated V2 conditional source
deletion and the original non-null revision. Local-only sources, directory
manifests, legacy protocols and unfinished transfers under a different protection
role still require separate recovery. These commands do not complete those
broader transfer requirements.

The signed Local fixture exercises two independent single-root instances. Its
first successful move is component evidence: complete later-source recreation,
both transfer directions, restart, heterogeneous backends and the other recovery
cases must pass before the complete workflow is accepted. A completed historical
receipt grants no authority over a newer source or destination generation.

Source retirement retains its exact pre-deletion public SDK generation, native
binding and actual tombstone receipt before releasing the old product name.
Schema 85 also retains the transactional name epoch. A legacy retirement cannot
invent that evidence after deletion. An immutable pre-copy notification claim
is retained before upload; completion acknowledges exactly those IDs through
the same running public SDK feed, with a null **source** revision. An empty claim
settles only the explicit command and reports `journalAcknowledged: false`.

The first acknowledgement invocation is durable before calling the SDK. Only
the actual successful return permits the atomic product completion receipt and
removal of exactly both owned root fences. A crash after the SDK commits replays
the same invocation, even when the rows are already absent. Queue absence alone
cannot replace a receipt. Once accepted, retry does not reread old Worker target
content or overwrite later user edits. Private source bytes remain retained.

The Host keeps both roots closed until fresh current membership and native
inventory audits pass. A historical completed result does not reopen a root in a
new Host. If completion was persisted but its fresh audit was interrupted, use
the current root recovery command before expecting ordinary sync to resume.

Late notifications are independent of the immutable pre-copy claim. Before
ordinary upload admission, the Host proves the original retired generation,
current parent and source-name epoch, and any distinct registered replacement.
It retains each accepted late notification's first SDK invocation and return.
Pending accepted starts recover before waiting for a new feed head. Unassigned,
ambiguous or intervening generations remain pending for reconciliation; a path,
timestamp or nullable SDK file identifier cannot substitute for ownership.
The indexed source lookup and bounded rotating late-ACK recovery do not establish
a performance bound for all existing lifetime-history queries.

The additive clean Windows test covers same-instance and cross-instance original
ACK interruption, receipt rollback and late Delete recovery. Until that source's
native CI and signed Worker/CLI paths pass, managed tests and compilation alone
do not establish full transfer or S06 acceptance.

The manual ARM64 workflow also runs `eng/verify-cli-transfers.ps1` against the
same frozen signed Local V2 package. This separate disposable fixture uses two
real Worker processes and two single-root instances, using the manifest's declared
`local` key and distinct root labels. It exercises forward and reverse typed CLI
move/resume, whole source and destination bytes, a later distinct source
generation, later target edits, current root admission and Host restart. The
stopped-Host test oracle acquires the actual exclusive catalog lease before
reading original completion receipts and independently verifying retained private
bytes. An empty captured claim must report command completion without claiming
that original notifications were acknowledged.

Current reports use schema 2 and bind each direction to the original instance
and stable root IDs. The collector requires this single-root report. Schema 1
remains readable for historical evidence; same-instance multi-root acceptance is
deferred to the post-S06 refactor and is not a current S06 prerequisite.

`cli-transfers-arm64-originals` retains the source-bound report, completed
execution observations and independent execution/cleanup failures. The report
requires authenticated bounded permission cleanup and root unregistration before
the GUID fixture directory is removed. Its route/name allowlist is separate from
the existing single-root hydration regression fixture. Run this native verifier
only with `MIRRORPULSE_NATIVE_TEST=1` in the disposable Windows verification user.

## Reused source backend addresses

The default Host can select a durable successor public identity after the exact
original transfer-source retirement and journal acknowledgement complete. It
retains the old transfer addresses and receipts when the backend reuses its
remote ID. See [remote identity generations](remote-identity-generations.md) for
selection, polling and streamed replay boundaries. This component does not
replace the full signed transfer, later-generation and restart acceptance.

## Cross-package verification

The same disposable CLI verifier can additionally connect the signed Local and
WebDAV packages, each with its manifest's single declared root. See
[CLI transfer verification](cli-transfer-verification.md) for execution, original
evidence, conditional HTTP effects, and the distinction between component checks
and complete Host transfer acceptance.
