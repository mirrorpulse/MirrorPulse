# Signed Local cancellation across independent Host processes

The CLI regression runs two additional protected-root cases after the original
three-command Label lifecycle. One keeps the signed Local instance active; the
other disables it and requires its Worker to remain disconnected and offline.
Both use the production CLI, current-user Control pipe and default Host.

Each case prepares a root Label without executing it, retains the original
operation and endpoints, and observes the pending preparation with namespace
admission closed. The preparing Host must fully exit. A different Host process
starts through the ordinary public command and must retain that same pending
preparation. The public sync state is `NamespaceRecovery` while it is pending;
the root's configured `state` separately retains `Active` or `Disabled`.
The public `root cancel-rename` command must then return the
original cancellation ID, endpoints and timestamp. Current admission comes from
a separate fresh audit: an active root becomes ready, and a disabled root stays
closed with its Worker offline.

After cancellation, another public Label is prepared and executed. Replaying
the old cancellation must preserve its immutable receipt and today's later
Label. The fixture then executes a restoration. All nine post-command/replay/
restoration observations retain the complete initial member graph, stable root,
member, native item and anchor identities, and both resident/source file hashes.
The original fixture name and active state are restored before the existing
local/remote conflict regression continues. Each stop/start and state wait uses
the existing 30-second public boundary; the regression step budget is unchanged.

Typed originals, process identities, per-phase byte observations and the complete
result are retained under `cli-regression.originals/root-cancellations` before
cleanup. `cli-root-label-cancellation-policy.ps1` independently checks the
original records against the summary. The final CLI report also includes
`rootCancellations`; it is written only after the existing guarded cleanup.
Missing originals or a partially completed case cannot establish success.

These cases exercise clean process exit with an unstarted preparation. They do
not interrupt native execution, inspect journal ACK receipts, or establish the
full crash, cancellation-refusal and concurrency matrix. The original Label
lifecycle keeps its separate three-command/three-restart evidence and claims.
Local evidence-policy tests exercise damaged input rejection; actual signed
Host, Worker and Cloud Files execution is required on clean Windows CI before
the new process-boundary cases can be accepted.
