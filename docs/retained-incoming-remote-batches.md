# Retained incoming remote batches

The routed Runtime retains an incoming public CloudRemoteChangeBatch in the Host
product catalog before SDK access. Catalog schema 68 stores one immutable pending
envelope per instance, with an integrity digest and bounded payload. The envelope
contains the public batch fields, every metadata value, per-entry cursors, SDK
fingerprint, public apply policy and original stable root mappings. It contains
neither SDK-private checkpoint bytes nor an inferred native-success receipt.

An exact incoming replay must keep its original fingerprint and apply policy.
Before a later incoming or root-polling batch reaches apply, the Runtime resumes
the retained batch first. Only that batch's SDK/member recovery path may perform
work. A changed Label, source root key, identity scope, instance or disabled root
cannot retarget retained paths. Root-scoped polling keeps its existing independent
durable intent rather than consuming the incoming instance slot.

Host startup discovers retained owners in pages of 64 after namespace bootstrap
and attempts recovery through the instance scheduler and current library owner.
Recovery takes at most 64 turns and continues only while the actual public SDK
entry prefix advances. A retry with unchanged progress stops immediately. An
offline instance is retained without dispatch; failures report a stable recovery
status and leave other owners available. Further incoming/polling work retries
the retained owner before applying its own batch. These calls do not request a
new source enumeration or reconstruct a batch from current Adapter data.

Completion requires the existing apply coordinator to finish product member
settlement and publish the cursor. The retained coordinator independently reads
the original public SDK batch fingerprint, complete prefix, terminal change and
final cursor, together with the published instance cursor. Only then does it
remove the exact original product envelope. An exception or cancellation after
SDK or cursor commit therefore keeps enough input for replay after reopening.
Managed-root rename preparation also waits for this original incoming intent.

The Host library owner serializes admission and replay. Per-instance ordering
does not authorize source work, bypass closed namespace roots, replace old-role
intent or treat durable SDK conflicts as successful native changes. Recovery
still uses the existing role, object and parent proofs. Public callbacks cannot
be serialized as durable conflict resolvers and are rejected on this route.

Tests use separate real product/official SQLite owners to exercise every retained
field, original policy, commit interruptions, complete-progress/cursor readback,
mapping changes, bounded owner discovery, schema migration and multi-entry startup
replay. The existing mandatory native cursor fixture also checks a real SDK commit
followed by a retained-envelope interruption across product owners; that extension
is compiled and awaits disposable native CI. This does not complete managed remote
birth/replacement/retirement, current-role handoff, default Host composition or S06.
