# Installed Shell failure process boundary

This verifier records additional failure observations; they do not establish a cause or satisfy installed Shell acceptance.

The b96b477 CI 40198804387 failure has verified managed probe entry metadata and
two actually read zero-byte files while the observed process remains running.
This does not establish that Main was never entered or identify a CfSharp defect.

Add failure-only read-only observations of the observed process: package identity
presence, same-session status and three fixed module-presence booleans. No names,
paths, memory, tokens, command lines or window text are retained. Query failures
remain unavailable, independently of other observations. These facts cannot make
registration or readback pass and do not replace any original two-minute deadline.

Microsoft documents a null-buffer GetPackageFullName query returning the required
length with ERROR_INSUFFICIENT_BUFFER when package identity is present, and
APPMODEL_ERROR_NO_PACKAGE when absent:
https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagefullname

Run only synthetic managed processes locally. Installed execution remains for a
clean CI test user. This candidate is diagnosis, not a causal fix or S06 closure.
