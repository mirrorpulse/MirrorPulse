# Original SDK journal observations after protected root renames

The existing native protected root Label execution fixture records bounded
public SDK journal windows during each of its fourteen enabled/disabled and
interruption scenarios. The execution and recovery owners each start their
watcher before their first move, recovery or replay. Each captures a finite
window after its existing assertions and emits one report after closing its
watcher. The initial population owner retains its existing separate feed.
The original assertions, seven fault cuts, source-read checks, cleanup and
thirty-second per-scenario deadline remain unchanged.

Each observation uses the already running public feed's finite scan and paged
reads. It reads at most eight pages of sixty-four entries and retains at
most sixteen original first-level directory-move notifications. It records the
original operation ID, sequence, optional ItemId, original names and observation
time, together with the owner number, known original root SDK ItemId, current
native ItemId, scan bounds, observed counts, completeness and actual rescan flag. Incomplete
windows and truncated retained observations are explicit. Other journal changes
contribute only to the scanned count; their paths or content are not logged.

`completeWindow` refers only to the captured durable sequence range. The public
SDK explicitly does not wait for native notifications still being normalized.
Every report therefore records `nativeQueueDrainProven=false`: an empty window
cannot prove that the native move produced no notification. A schema-1 observer
introduced in commits7e38ae1/7619b78 started its watcher only after all moves;
those original CI reports remain preserved but cannot establish observation of
the moves. Schema2 corrects that fixture lifetime without claiming a drain or
causal association that the public SDK does not provide.

The diagnostic performs no ACK, SDK mutation, rescan reset, Worker operation,
content read or new ownership acceptance. A failed observation reports only its
exception type, HRESULT and native error when available; it does not replace the
original native fixture outcome. An unavailable observation is not evidence for
a working journal boundary. Successful existing native tests therefore still do
not imply that root notifications have been accepted or acknowledged.

These original SDK facts must be inspected before connecting runtime policy.
Assigned and unassigned notifications, repeated names, SDK loss signals, and
actual original IDs cannot be inferred from the synthetic catalog model. New
unassigned identity remains unassigned. The twenty-eight owner observations are
a diagnostic boundary; actual availability and contents remain pending clean
Windows native CI. No local native execution is required to compile this change.
