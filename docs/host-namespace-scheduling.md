# Host namespace scheduling

Controlled operations retain root work before waiting for their instance. They
acquire the instance before the current SDK owner, and create SDK coordinators
only inside that owner's lifetime. A queued operation therefore leaves the SDK
available to earlier work in the same instance. Cancellation while queued releases its root
admission without creating a native intent.

Recovery first validates its original route under the current SDK owner and
closes the selected roots. It releases the SDK owner while earlier root work
drains. After draining, it acquires the configured instances in stable order,
reacquires the SDK owner, and checks provenance again before retaining an
initializer or reopening the library. Only a fresh native audit reopens root
work; historical completion and canceled recovery leave current admission
unchanged or closed as appropriate.

The explicit instance lease provides serialized lanes to recovery coordinators.
They use those lanes instead of reacquiring physical instance gates. The lease
rejects foreign instances and drains accepted work before releasing its gates.
It does not grant namespace execution authority, native identity, or root
readiness. Permanent services bind their SDK coordinator after acquiring the
current owner; they do not retain a temporary recovery lease.

Replacement preparation materializes the original and stages private input as
the normal user before waiting for the instance. Its transient preparation
contains the original product snapshot and content digest. A fresh coordinator
checks that snapshot, input, target, parent, identity and protection again before
admission. SDK item references do not survive library rotation.

Runtime shutdown cancels queued admissions and waits for recovery requests before
disposing the current library. Accepted native callbacks, projection work and
retained bodies still drain through their owners. A shared instance scheduler
remains owned by its caller. Scheduler shutdown retains its original drain even
when a cancellation callback throws or another caller requests disposal.

These boundaries support strict namespace integration. Default strict Host
composition still requires all remote member lifecycles and pending operations
under a new Host role, alongside the complete native and installed product gates.
