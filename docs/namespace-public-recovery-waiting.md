# Public recovery requests behind an existing root owner

CI 40363669794 at `f6766a7` retained a signed CLI preparation failure classified
as `RootWorkDrain / PendingTransition / 80131509`. That stage wraps only root
recovery admission; its matching guard refuses an existing recovery owner. The
failure occurs before instance/library serialization, current-tree audit or
CfSharp proof preparation. This identifies the immediate MirrorPulse admission
collision for that run; it does not establish a cause for older unclassified
failures or a CfSharp defect.

Public prepare, execute and cancel commands now use a cancellable recovery
request queue before acquiring root work, instance/library serialization or the
namespace actor. At most 64 requests wait. Earlier overlapping requests retain
priority, while unrelated roots can continue ordinary work or recovery. A
request closes all selected roots together, then drains previously accepted
work only after it owns every selected recovery slot. Root readiness cannot
briefly reopen between queued owners, and only a fresh audit can reopen it.

Existing internal `BeginRecoveryAsync` still refuses an existing or queued
overlapping owner; it does not become reentrant. Waiting requests must originate
outside an already held ordinary/recovery, instance/library or actor scope.
Cancellation removes only that request, preserves the actual current owner and
does not grant readiness. Shutdown wakes queued requests and drains their owner
lifecycle without permitting a new claim.

The focused managed tests use real asynchronous scopes to cover the observed
owner collision, overlapping order, atomic selected-root drain with independent
progress, cancellation/successor behavior, the queue bound and shutdown. Actual
signed Host success still requires the original complete lifecycle regression;
waiting does not waive a later protection, pending-batch, identity or SDK failure.
