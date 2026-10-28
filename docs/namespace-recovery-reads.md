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
Each transaction history cache family retains at most 16 successfully validated
immutable entries. Within that one SQLite snapshot, a hit marks an entry as
recently read and a full cache replaces its least recently read entry. A miss
still loads and validates its original fact before retention. No current head,
name, absence or native observation is cached. Directory position and protection readers
use their returned validated graph directly, including graphs beyond cache
capacity. A new transaction starts without prior cached facts.

This does not bound total SQL work, all process memory or complete P083. Each
cached graph still follows its existing payload/member limits. Born-member
recovery scans action sequences in pages of 16, validating every original action,
predecessor, initialization, reference, completion, head and pending reservation.
Internal callers retain the latest and optional selected action while validating
the current row. The existing 4096-action history limit stays unchanged. Public
history queries still return their complete requested original actions. Cache misses can repeat validation; per-turn work and
fairness require separate limits.
Managed-root rename discovery, move-root IDs, unregistered birth discovery,
root sorting, total catalog work per turn and complete fairness still require
their own bounds. The default Host must continue using its shared admission,
namespace actor and current native verification before dispatch.

An individual managed-root rename recovery now selects its original operation
through the indexed catalog key in the Host, scheduler service and public-proof
coordinator. It validates the same indexed owner, original proof and observation
without loading another root's history. A missing operation returns no record;
corruption in the selected record still fails. The complete history API retains
all requested original records and continues to report corruption. Startup still
requests the complete discovery inventory; only each selected recovery uses the
point lookup. This does not establish bounded pending discovery or complete P083.
