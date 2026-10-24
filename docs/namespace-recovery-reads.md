# Namespace recovery reads

The product catalog validates original namespace owners before returning a
complete recovery snapshot. Reading that snapshot grants no native authority,
changes no original receipt, and does not acknowledge a journal notification.

Internal discovery streams transfers, remote updates, member retirements,
retirement batches and born-member recovery owners. ID queries retain at most
16 candidates per page in the caller's original catalog transaction. Union
branches limit their own candidates before deduplication. The initial page
includes an empty corrupt key so pagination cannot silently hide it.

Every selected owner still passes its existing payload fingerprint, ownership,
reservation, original graph and completion checks. A corrupt later page rejects
the complete snapshot; callers do not publish partial readiness. The existing
4096 remote-update inventory limit remains. No new inventory limit discards
retirements, batches or born-member histories. Existing catalog bytes, schema
version, SDK policy and public result ordering remain unchanged.

This removes lists of all complete owner graphs from those internal iterators.
It does not bound total SQL work or complete P083. Born-member validation still
loads one bounded original history, public history APIs retain their requested
results, and shared transaction graph caches retain validated references.
Managed-root rename discovery, move-root IDs, unregistered birth discovery,
root sorting, total catalog work per turn and complete fairness still require
their own bounds. The default Host must continue using its shared admission,
namespace actor and current native verification before dispatch.
