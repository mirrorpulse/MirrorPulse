# Product catalog open timing

`MirrorPulse.ProductCatalog.Open` is an optional .NET diagnostic source. With a
subscriber, each catalog open has a new anonymous trace ID and reports elapsed
milliseconds at a closed sequence of existing boundaries: owner lease, connection,
WAL mode, schema version, base schema, legacy columns, namespace schema, transfer
schema, recovery schema and completion. An interrupted open has no completion
event. These observations do not prove which statement was slow or why.

The payload is only a `(Guid, long)` tuple. Event names come from a closed internal
phase enum. It contains no database path, SQL, user or credential value, role,
native identity, exception or data row. Subscriber construction, filtering and
delivery failures cannot replace the original catalog result or exception. With
no subscriber, phase marks do not allocate payloads or start an elapsed timer.

Disposable native transfer and scoped-recovery verification subscribes to this
source and records only the known phase names, nonempty trace IDs and nonnegative
elapsed values. Its original operations, assertions and 45/60-second fixture
deadlines remain. This helps separate ordinary-user connection/startup delays
from permission and recovery work. It does not change SQL, transaction boundaries,
schema versions, durability, ownership, public deadlines or readiness.
