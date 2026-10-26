# Root Label failure observations

The public Host preparation command and the ordinary journal pump retain fixed
failure classifications alongside the original root or operation ID and HRESULT.
The classifications distinguish current fenced audits, live guards, public native
identity, original ownership, completed placement and complete tree checks.
Unknown exceptions remain `Unknown`; arbitrary exception messages, paths, Labels,
credentials and Worker responses do not become structured diagnostic values.

Preparation logs are written below the private data root's `logs/root-labels`
directory. Journal admission, recovery and readmission use their existing logs.
The signed CLI failure collector retains the latest 32 preparation log entries
separately for execution and cleanup failures, before deleting its temporary root.
The original exception, cancellation, operation and policy decision are preserved,
including when the diagnostic writer fails. A classification identifies the guard
that refused progress; it does not by itself establish the underlying cause or
authorize a retry, a revision rebase, fence release or journal acknowledgement.

The signed Local lifecycle failed at disabled case-only preparation in CI
40339651595 against source `6e0f09d3338bcee70756072f0051be4d36b93a4e` and CfSharp
`0.1.0-preview.8`. Its original `80131509` admission/readmission failures remain
unclassified historical negatives. This additive observation does not establish a
CfSharp defect or claim that the signed lifecycle now passes.
