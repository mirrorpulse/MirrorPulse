# Upload conflict boundary observations

The public `StaleRemoteRevision` classification and a `BothMissing` revision
relation do not identify the failure boundary. In particular, rejecting a
retained tombstone happens before remote Stat and reports no revision values.
It does not prove that both remote revisions were absent.

The existing bounded `JournalDispatchBoundary` diagnostic now covers caught
coalesced conflicts as well as ordinary content conflicts. Its typed
`uploadConflictBoundary` identifies `AcknowledgedItemTombstone`,
`RemoteRevisionMismatch` or `WorkerRejected`. `CoalescedExecution` identifies
the coalesced path, including its preparation; it does not claim that a Worker
mutation was executed. Original IDs correlate the event without recording
paths, credentials, remote IDs, revision values or arbitrary exception text.

These observations change no revision guard, remote precondition, tombstone
policy, admission, retry, ACK or receipt. They do not establish a cause or a
CfSharp defect for an earlier failure. Actual current-source failure evidence
is required before making a causal claim or changing execution policy.