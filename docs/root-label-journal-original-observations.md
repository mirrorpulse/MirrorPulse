# Original SDK journal observations after protected root renames

The existing native protected root Label execution fixture now records a bounded
public SDK journal window after each of its fourteen enabled/disabled and
interruption scenarios has completed the original three-owner, subtree,
case-only Label, historical replay, byte and deletion-protection assertions.
The original assertions, seven fault cuts, source-read checks, cleanup and
thirty-second per-scenario deadline remain unchanged.

The observation creates a public local change feed and uses its finite scan and
paged reads. It reads at most eight pages of sixty-four entries and retains at
most sixteen original first-level directory-move notifications. It records the
original operation ID, sequence, optional ItemId, original names and observation
time, together with the known original root SDK ItemId, current native ItemId,
scan bounds, observed counts, completeness and actual rescan flag. Incomplete
windows and truncated retained observations are explicit. Other journal changes
contribute only to the scanned count; their paths or content are not logged.

The diagnostic performs no ACK, SDK mutation, rescan reset, Worker operation,
content read or new ownership acceptance. A failed observation reports only its
exception type, HRESULT and native error when available; it does not replace the
original native fixture outcome. An unavailable observation is not evidence for
a working journal boundary. Successful existing native tests therefore still do
not imply that root notifications have been accepted or acknowledged.

These original SDK facts must be inspected before connecting runtime policy.
Assigned and unassigned notifications, repeated names, SDK loss signals, and
actual original IDs cannot be inferred from the synthetic catalog model. New
unassigned identity remains unassigned. The fourteen observations are a new
diagnostic boundary; actual availability and contents remain pending clean
Windows native CI. No local native execution is required to compile this change.
