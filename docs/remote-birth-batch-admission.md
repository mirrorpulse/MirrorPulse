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
native success evidence. The managed remote batch driver still supports moves
only. Connecting birth execution, durable conflict settlement, original-owner
fence recovery, role handoff and default Host composition remains necessary
before this admission becomes a delivered remote birth lifecycle.
