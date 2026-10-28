# Root Label cancellation failure boundaries

The Host records `RootLabelCancellationFailed` in its local root-label log when
the public cancellation command fails. The record contains the original operation
ID, a closed cancellation stage, the existing fixed failure classification and
the original exception's HRESULT. The public response and exception mapping stay
unchanged. Paths, labels, credentials, exception messages and raw proof data are
not included.

Stages distinguish root drain, instance/library admission, original permission
recovery, original cancellation, metadata inventory, anchor metadata and native
inspection, complete member inspection, public reconciliation, reinspection,
final native observations, atomic cancellation and current-tree recovery. The
deepest known failure is retained through outer wrappers. Logging failure cannot
replace the product exception, and cancellation retains its original token.
Successful requests emit no failure observation.

Member inspection distinguishes retained provenance, metadata inspection, native
object verification, public identity, sealed admission, complete local inventory
and the original public root proof. An already rejected native object reports the
first failed fixed check: snapshot availability, native or retained binding,
owner, root, path, kind, link count, retained descriptor or descriptor policy.
Only the closed check name is recorded. Diagnostic observation retains the
original exception type, message and HRESULT. The two bounded identity and
descriptor policy corrections are described below. These details never record
a member path, identity bytes, SID or descriptor, and add no successful-path read.

These observations grant no cancellation, native or readiness authority. They add
no native call, catalog query, retry or timeout, and cannot turn a failed request
into success. In particular, public `mp.control.invalidRequest` also covers
invalid stored observations; it does not prove that the user's command syntax
was wrong. The retained source `dab0923` signed reproduction rejected cancellation
after an independent Host restart at `MemberInspection`, with `InvalidData` and
original exception HRESULT `80131501`, before public reconciliation. The precise
member check remains unproven until a same-source reproduction supplies a more
specific record. This HRESULT alone does not establish a CfSharp native defect.

CfSharp preview.8 documents snapshot `ItemId` as durable-state metadata that may
be unknown. An unstarted cancellation accepts an absent durable projection only
after the retained registered identity and complete actual native placeholder
identity match the original root proof. A known conflicting projected item ID,
missing native identity or any changed opaque identity byte still fails. The
same original membership, owner, native binding, descriptor, complete inventory
and successful public `NotMoved` checks continue to apply. A missing projection
does not establish another root's identity or grant namespace authority.

Unstarted cancellation compares a fresh native descriptor to both its retained
verification and its requested policy using the existing bounded Windows
read-back rule. The only accepted difference is Windows adding the automatic
inheritance completion flag. Removing that flag, changing an ACE, rights, owner
or execution role, removing protection or adding an inheritance request still
fails. The atomic catalog boundary applies the same directional rule to the
retained protection epoch. Original protection records are never rewritten;
the cancellation receipt retains the actual read-back, and replay must match
that immutable receipt exactly.

The retained `9478450` signed failure stopped at `ObjectRetainedDescriptor`.
That identifies unequal actual and retained descriptor strings; it does not
establish the exact object or prove this bounded Windows normalization caused
the signed failure. The correction is independently covered by unchanged
sealed/unsealed, enabled/disabled, original/born, anchor/root/leaf catalog tests
and native-policy boundary tests. A new signed reproduction remains required.

An object-verification rejection can additionally record four closed values:
anchor/root/descendant scope, original/born provenance, the descriptor difference
category, and whether its existing ACEs reference the current execution role.
These observations inspect only the already captured descriptors. They issue no
native read, permission write or catalog query, and grant no authority. A role
reference does not mean that the role is allowed access or that recovery owns it.
Raw descriptors, principals, paths and identity bytes remain omitted. Failed
classification cannot replace the original product exception. The same original
object and permission guards still reject the request.
