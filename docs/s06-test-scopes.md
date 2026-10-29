# S06 and retained P079 verification scopes

S06 covers transfers between distinct single-root Adapter instances, including
different supported packages. Same-instance multi-root transfer (P079) is deferred
by the user to the separately initiated R1 review after S06. Root isolation remains
part of S06.

The five existing native transfer methods select their original cross-instance
branch. Five `DeferredP079` methods select the original same-instance branch of the
same helper, including all five source-retirement cutpoints. Both selections retain
the original native actions, assertions and 45-second per-scope deadlines.

`verify-native.ps1` excludes `DeferredP079`. The required native and namespace name
sets are unchanged. `verify-deferred-p079.ps1` verifies the separate five-method
selection without skipped or missing results. CI runs it on x64 and ARM64 and
preserves its original results in separate artifacts. A negative compatibility
result remains visible and does not fail the S06 gate. It does not complete P079.

The ordinary-user controller uses `-Scope S06`, covering the 124 selected methods
and retaining every selected fixture deadline. Its envelope is 5,310,000 ms plus
the existing five-minute cleanup margin (94 minutes for the workflow step).
`-Scope AllRetained` preserves the historical 5,715,000 ms calculation and 101-minute
step envelope for reviewing the original combined scope. Neither scope changes
the 30-second public control request deadline.
