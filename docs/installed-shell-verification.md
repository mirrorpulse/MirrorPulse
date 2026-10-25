# Installed Shell probe output

The disposable installed-MSIX verifier must distinguish launching a process,
observing fixed progress, and receiving a successful registration/readback result.
None of those observations alone replaces the others. The actual installed test
runs only in the clean Windows CI user and retains source-bound original reports.

The verifier precreates empty result and progress files in its own GUID scratch
directory before launching the packaged app. MSIX can redirect newly created
AppData files for virtualized packaged desktop applications. Opening an existing
real file avoids that new-file visibility ambiguity. See Microsoft's
[AppData behavior](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes#appdata-operations-on-windows-10-version-1903-and-later).
This is an observation-boundary correction, not evidence that virtualization
caused a particular earlier missing-result failure.

The verifier launches the observed executable directly with shell execution
disabled and explicitly supplies the private result environment variable in its
`ProcessStartInfo`. This makes the observed child and its probe selection
environment explicit. See the [.NET environment contract](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.environment?view=net-10.0).
The probe must still validate its real installed package identity and actual
registration/readback; direct launch alone proves neither.

The same original two-minute result deadline applies. Empty, partial, oversized,
linked or unrecognized files cannot pass verification; existence alone never
terminates the wait successfully. Only the bounded existing `success` or failure
protocol completes the result wait. Fixed progress remains independently parsed,
and failure evidence records whether the real output files were prepared. Existing
files are never overwritten during preparation, and preparation failure does not
claim registration success. Product registration/readback and cleanup remain
unchanged. The Shell probe does not invoke CfSharp.

Managed tests exercise the real helper, temporary files and a separate child
process. They do not emulate MSIX virtualization or establish installed Shell
acceptance. The complete source must pass the actual clean installed CI before
the Shell requirement can close. Earlier source-bound failures remain retained.
