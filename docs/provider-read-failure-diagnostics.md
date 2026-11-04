# Provider read failure boundaries

CloudProviderRequestFailed retains the public CfSharp request kind and numeric
error. An optional providerReadStage identifies a failure caught by MirrorPulse
while routing, entering root work, validating the current identity, creating the
stream, requesting a Worker range or consuming its body. The optional
providerReadFailureKind uses a closed set of exception categories.

The provider attaches these facts to the nearest original public CfSharp
FetchData activity, searching at most eight current ancestors and preserving
the first observed failure. Requests without that activity retain their original
failure behavior. Logging uses the existing bounded, nonblocking diagnostics
queue and the product's field whitelist. It records no paths, remote identities,
credential values, exception messages, stack traces or arbitrary activity tags.
The original exception, root admission and range result remain authoritative.

A native file read error and the SDK activity error can identify different
boundaries. The signed ARM64 fixture at MirrorPulse 5ce752e retained
0x80070185 from its whole-file hash after a completed active Label restore, and
0x80131509 from FetchData. Those facts alone identify neither the failing
product guard nor a CfSharp defect. A missing optional stage is unobserved
boundary information, not evidence that a particular stage succeeded.

Focused tests exercise actual provider routing, closed-root admission, rejected
range transport and truncated content, retain the original exceptions and verify
that only the closed boundary fields reach the log. Clean signed Host and native
acceptance remain separate.
