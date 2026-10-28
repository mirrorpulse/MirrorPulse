# Signed Local root Label lifecycle fixture

The existing CLI regression additionally executes three protected root Label
commands through the production CLI, current-user Control pipe and default Host:
an active rename, a disabled case-only rename, and a disabled restoration of the
fixture's original name. The enabled instance must have its real signed Local
Worker connected. Disabled phases require an offline instance and disconnected
Worker. Every command retains its original operation ID and complete response.

Each completed Label is followed by a complete Host process exit and independent
Host startup. Original and new process IDs, stable root/member/public item and
anchor identities, every descendant's current path and parent, and two complete
resident/source file hashes are checked. Replaying the first completed command
after the later case-only Label must return the first immutable endpoints with
the current name and fresh audit. It cannot revert a later Label. The fixture
restores its original name and active instance before the existing local/remote
conflict and restart regression continues.

Typed CLI envelopes and per-phase byte observations are retained beneath
`cli-regression.originals/root-labels` before teardown. A separate evidence policy
checks those original records against the reported summary; the existing final
CLI report, baseline assertions and cleanup remain required. A partially written
record or completed command alone cannot stand in for final fixture success.

This fixture does not interrupt first native execution or carry an unfinished
preparation into a new Host role. It does not inspect the original journal ACK
receipt and cannot infer acknowledgement from an empty queue. Those fault,
role-replacement, concurrency and receipt boundaries still need their own proof.
Local verification is limited to syntax, evidence-policy tests and static gates;
actual product Host, Cloud Files and signed Worker execution belongs to clean CI.

The regression separately runs the [independent Host cancellation fixture](root-label-signed-cancellation-fixture.md)
after this lifecycle. Its original preparations, cancellations, later Labels and
restorations have their own evidence directory and policy. They do not change
this fixture's three-command/three-restart boundary or add native interruption
and journal receipt claims to its result.
