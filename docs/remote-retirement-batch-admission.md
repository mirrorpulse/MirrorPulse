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
schema 69 table. They provide catalog-boundary evidence. The managed remote
batch driver does not yet route deletes through this protocol; SDK execution,
native cutpoints, recursive remote retirement, new-role recovery and complete
default Host integration still require implementation and clean native tests.
