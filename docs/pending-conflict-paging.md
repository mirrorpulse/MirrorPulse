# Pending conflict pages

`conflict list [--limit 1..1000] [--cursor CURSOR]` reads one bounded page from
the running Host. The default page size is 100. JSON returns `items` and an
optional `nextCursor`; human output also reports the next cursor. Pass that
exact opaque cursor to the next request. Invalid limits, duplicate options,
empty cursors and unsupported cursor versions fail explicitly.

Both pending upload conflicts and the Host's pending CfSharp remote-conflict
projections are included. Results are newest first by exact UTC detection time,
then remote-before-upload source and canonical conflict ID. Indexed range
queries materialize at most limit+1 rows per family within one catalog snapshot.
The cursor's own family can use an equal-time ID seek followed by an older-time
seek; both share that single row budget. The equal-time seek orders only by ID
because its time is fixed. The Host merges only those candidates. The CLI uses the versioned Control
contract and never reads either database. A remote projection is display
metadata; it does not authorize an SDK operation or replace actual SDK results.

A cursor records a strict position, not a snapshot, total count, root admission
or mutation permission. Later requests observe the current pending set. Removing
earlier records cannot shift an offset and skip remaining older records; newly
inserted newer conflicts appear on a new first-page request. A conflict newly
inserted below the cursor can appear on a later page. Ties use source and ID so
same-time records do not repeat or disappear at a page boundary.

`conflict show --conflict-id ID` follows pages to find the requested pending
conflict, honors cancellation and rejects repeated/cyclic cursors. It no longer
treats absence from the first hundred entries as absence from the pending set.
No conflict is resolved, retried, acknowledged or otherwise changed by listing
or showing it.

The previous Host returned a numeric next-page token but ignored incoming tokens.
Those tokens did not identify an applied position. They are not accepted by the
new reader; start a fresh first page after changing Host versions. Treat all
returned cursors as opaque and version specific.

The private product catalog gains optional query indexes only. Existing record
formats, schema version, operation IDs, conflict statuses, SDK authority and
public/native time budgets are retained. The indexes use exact fractional ticks
and UTC seconds without SQLite's fractional-date rounding. Tests must establish
their actual range-query plans and offset/tick boundary behavior.

This query boundary is one part of S06 backlog visibility. It does not accept
the complete conflict-choice/fault/restart matrix, signed Worker integration,
recursive directory operations or the final protected-main gate.
