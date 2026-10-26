# Signed fixture cleanup after a completed root Label

The stopped-Host test probe retains the original fixed GUID fixture marker. A
different current root name requires the same instance, stable root and Adapter
key, plus the original registered root binding and anchor. It follows the current
committed placement head through at most three complete version-3 Label moves to
the exact original marker name. Every step must retain its original public proof,
same member and binding, atomic whole-subtree completion and predecessor. Missing,
pending, cancelled, legacy, foreign, cyclic or overlong histories refuse cleanup.

This authentication precedes SDK construction and all permission writes. The
existing fixed six-entry root inventory, complete local enumeration, retained
leases, current owner, alias refusal and child-first permission restoration remain
required. The renamed native root must still have the original authenticated
binding before any restoration and immediately before its own permission write.
The transfer fixture continues to use its separate authenticated inventory.

The change addresses the secondary RouteValidation cleanup failure retained from
CI 40339651595. The primary disabled case-only preparation failure remains open;
successful cleanup never turns the failed execution into a passing regression.
Actual native cleanup and the full signed lifecycle require clean Windows CI.
