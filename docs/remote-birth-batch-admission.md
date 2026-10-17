# Original remote birth batch admission

Product catalog schema 69 can retain a version 3 remote-population birth owned by
one original public SDK batch entry. Its name reservation and entry index commit
in the same SQLite transaction. The immutable reference contains the instance,
batch ID and fingerprint, entry count and ordinal, change ID, original logical
identity and revision, registered direct parent and placement, supported SDK
package/source identity and public preservation/echo policy. It contains no child
native binding, SDK-private preparation or successful outcome claim.

The logical ItemId is reserved in that first transaction, before a separate birth
plan exists. Other remote births, ordinary birth plans and protected original
identities cannot adopt it. A resumed plan must keep the original identity and
admission time. Move and birth entry indexes reject conflicting ownership in
both insertion and read paths. Birth, protection and parent-lineage reads verify
the remote index, including its reserved identity. Corruption keeps dispatch
closed instead of replacing the original owner from a current path.

Schema 68 and earlier birth records retain their existing version 1/2 semantics.
Migration creates no remote owner for historical source population. A remote
batch birth cannot be executed through the proactive source-population creator:
that would create a placeholder before the SDK receives its unchanged batch.
Actual native work must remain with the SDK public apply/recovery path.

Tests exercise real product SQLite owners, atomic rollback, identity and entry
collisions, original and born direct parents, reservation transfer at registration,
corrupt lineage and migration. They are catalog-boundary tests and supply no
native success evidence.

The managed remote batch driver now routes new explicit-identity file/directory
upserts through an SDK-owned birth coordinator. Under the shared namespace actor
and original protected-parent guard, it commits the original admission, plan and
start, applies the unchanged SDK batch one entry at a time, checks the public SDK
prefix and item projection, and records the first native object before protection
and membership. The Adapter cursor remains with the outer batch coordinator and
is published only after registration. No preliminary placeholder is created.

A committed first observation can be settled after reopening the product/SDK
owners: the current object must retain that exact native binding. A registered
directory can then act as the actual direct parent of the next file entry. The
original batch alone can pass its own pending-birth fence; another unresolved
birth or namespace operation keeps that root closed. Disabled roots are rejected.
Pure public-input checks run before native admission.

An existing start without a first native observation remains unresolved, even
when the SDK prefix advanced. The implementation does not infer its original
native object from a current matching path/identity. SDK conflicts, occupied
replacement destinations, metadata and retirement entries also retain their
separate lifecycle requirements. Durable birth-conflict closure, every unknown
start cutpoint, new-role handoff and full default Host startup recovery are still
required before the complete remote birth lifecycle is delivered.

The mandatory native fixture covers SDK creation, an interruption after the first
directory observation, two product/SDK owners under the same role, nested file
creation, original binding preservation and registration before cursor publication.
On source `792a533bdaeaf7fd8430e08ede83e73efae9f93e`, that fixture passed the
original x64 native, ARM64 namespace and fresh ordinary-user ARM64 namespace runs
in [CI 39020999016](https://github.com/mirrorpulse/MirrorPulse/actions/runs/39020999016).
The original reports were checked against that source's frozen required-case
catalog. This evidence covers the observed-directory recovery above; unknown
starts, conflict closure, new roles and complete default Host integration remain
outside that fixture's scope.
