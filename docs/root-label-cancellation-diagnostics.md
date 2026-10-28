# Root Label cancellation failure boundaries

The Host records `RootLabelCancellationFailed` in its local root-label log when
the public cancellation command fails. The record contains the original operation
ID, a closed cancellation stage, the existing fixed failure classification and
the original exception's HRESULT. The public response and exception mapping stay
unchanged. Paths, labels, credentials, exception messages and raw proof data are
not included.

Stages distinguish root drain, instance/library admission, original permission
recovery, original cancellation, metadata inventory, anchor metadata and native
inspection, complete member inspection, public reconciliation, reinspection,
final native observations, atomic cancellation and current-tree recovery. The
deepest known failure is retained through outer wrappers. Logging failure cannot
replace the product exception, and cancellation retains its original token.
Successful requests emit no failure observation.

These observations grant no cancellation, native or readiness authority. They add
no native call, catalog query, retry or timeout, and cannot turn a failed request
into success. In particular, public `mp.control.invalidRequest` also covers
invalid stored observations; it does not prove that the user's command syntax
was wrong. The retained sourceb7cc86d signed reproduction returned exit2 from
cancellation after an independent Host restart. Its exact inner rejection remains
unproven until a same-source diagnostic reproduction supplies an actual record.
