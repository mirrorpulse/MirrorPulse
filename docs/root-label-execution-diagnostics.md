# Fixed stages for an original root Label execution failure

Signed CI40372827374 at `6be4546` retains an actual successful disabled case-only
preparation followed by failure of `root execute-rename ORIGINAL_ID`. Its public
error is `mp.control.internalFailure`; the exact execution cause and native
error are not established. The earlier preparation-stage collision must not be
assigned to this different failing command.

The Host records the original operation ID, closed product failure boundary,
deepest closed execution stage, failure category and original HRESULT. Stages
identify root-work drain, instance/library scope, original subtree preparation,
original protected execution or current-tree audit. A protected-execution stage
does not itself prove that a native SDK call occurred.

Observation preserves the same exception, cancellation token and successful
return. A logging failure cannot replace the product failure. Exception text,
labels, paths, credentials and arbitrary stage values are not recorded. Existing
bounded rolling logs and original CI failure retention remain in use.

These diagnostics change no move, permission, identity, admission, journal or
readiness check and extend no timeout. Actual original CI evidence must establish
the failing boundary before a product correction or CfSharp issue is justified.

CI40434803541 at `a8f4689` locates the same original disabled case-only failure at
`SubtreePreparation` with the original exception HRESULT `80131509`. The exact
underlying cause remains unproven. Additional closed stages distinguish original
ownership, protection snapshots, complete member inventory, anchor inspection,
public unmoved reconciliation, member inspection, official projection checks,
member/anchor reinspection and the atomic catalog seal. They preserve all original
calls and guards. A reconciliation stage alone proves no native move or library
defect; actual original results must determine the next correction.
