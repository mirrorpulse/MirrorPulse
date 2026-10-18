# Original remote retirement admission

Product catalog schema 70 retains an SDK-owned delete as a version 2 retirement.
The immutable reference contains the original instance, batch ID, fingerprint,
entry count, ordinal and change ID; the logical identity and revisions; coherent
registered child and direct-parent snapshots; and the supported SDK source and
public preservation/echo policy. Admission and the batch-entry index commit
together. Move, birth and retirement indexes reject conflicting entry ownership
on insertion and read, including completed or rejected history.

The original native child binding is already known before deletion. A retained
start must use that exact logical identity. Ordinary controlled deletion cannot
pre-execute an SDK-owned retirement or publish its completion. The runtime must
apply the unchanged public SDK batch and check its actual durable prefix before
submitting settlement facts.

Successful settlement requires native absence, the exact original public SDK
tombstone and revision, and a fresh unchanged protected parent. A rejected
settlement requires the actual durable SDK conflict and an unchanged original
child, parent and official revision. A single product transaction stores the
immutable outcome and terminal retirement. Only successful deletion releases the
member name; rejection retains the member and name. Neither outcome resolves a
remote conflict, acknowledges a local journal operation or publishes an Adapter
cursor. The original batch alone can pass its own pending-retirement root fence.

Existing controlled retirements keep their version 1 meaning. Migration rebuilds
the legacy phase constraint transactionally, preserving rowids, original payloads,
fingerprints and member references. It creates no remote owner or outcome for
older records.

Tests use real product SQLite owners, transaction-abort triggers, reopening,
success and rejection evidence, cross-kind ownership, corruption and an actual
schema 69 table. They provide catalog-boundary evidence.

The managed remote batch driver now routes explicit-identity leaf deletes through
the original SDK batch. It retains the current role, shared actor, original child
and protected parent, then persists admission and start before SDK dispatch.
On replay, an existing child must still match the original native binding and
revision. An absent child requires the original start and exact public item
projection before the SDK can finish its own durable tombstone recovery. Applied
SDK prefixes settle their original product entry before any later SDK entry.
Decoded durable conflicts are matched to the complete original change; their
records remain available for an explicit conflict decision.

The native fixture covers file and empty-directory deletion interrupted after
the SDK result or at the actual SDK batch-progress commit, two product/SDK owners
under the same role, original membership history, tombstone recovery, exact replay,
and name settlement before Adapter cursor publication. It is compiled locally;
its clean native execution is still pending. Recursive directory retirement,
new-role recovery and complete default Host integration remain required. A
nonempty native directory is rejected before the leaf admission can create a
pending owner.

Schema 71 adds an original SDK subtree admission. Its complete registered graph,
SDK entry owner, current child/parent protection and per-member identity/revision
snapshots commit atomically. Separate bounded metadata rows retain the full tree
fingerprint and can be read in pages of at most 64. The existing retirement graph
bounds remain 65,536 members and 4 MiB for its structural header. Reopening or
reading pages cannot replace missing members, positions, identities or metadata
with current paths. Ordinary controlled retirement cannot execute or complete
this SDK-owned admission.

Catalog recovery can enumerate at most 64 pending subtree owners for the exact
instance and original SDK batch. Only the same immutable admission can pass its
own retirement-batch fence; all other operation fences remain effective. This
permission is an ownership check and provides no native-start, deletion, SDK
result or cursor evidence. The runtime recursive route is still pending.

This subtree catalog boundary is preparatory. The managed driver still routes
only registered leaves. Original SDK recursive dispatch through retained starts,
partial deletion/tombstone recovery, descendant settlement, conflicts and cursor
publication still require their dedicated execution path and clean native proof.

Schema 72 adds a durable original SDK invocation start. The first start validates
the original parent, every retained member snapshot in pages of 64, immutable SDK
and tree fingerprints, and all foreign operation fences in one transaction.
The start and its owning header reference commit together. Losing either side
is corruption, which cannot be treated as a fresh never-started admission.
The original structural graph keeps its 4 MiB bound; its fixed start reference
has a separate 128-byte header allowance, so starting an admitted tree does not
consume more of the structural budget or rewrite the original graph.
Reopening preserves the first timestamp. An upgraded schema71 admission receives
no invented start; a failed start write grants no permission to invoke the SDK.
This record means invocation may have begun. The Host still must inspect actual
native objects and public SDK projections before invoking the unchanged batch,
then separately settle its actual SDK outcome before publishing an Adapter cursor.
