# Installed Shell probe entry observations

`MirrorPulseShellProbe=true` selects a diagnostic managed entry point for the
disposable installed verification build. Before delegating to the XAML compiler's
`XamlGeneratedProgram.XamlGeneratedMain`, it writes fixed startup progress. The
generated method still initializes WinRT, starts WinUI, installs the dispatcher
synchronization context and constructs the real App. The probe's actual Shell
registration, readback, result parser and two-minute deadline are unchanged.

The framework delegation has a separate non-inlined method so its JIT boundary
does not precede the first authored entry observation. Runtime or module
initialization can still occur before Main. Missing progress cannot establish
which of those earlier stages ran or whether an output write was visible.
`GeneratedStartupReturned` records an actual return, including a normal WinUI
shutdown; it does not mean that Shell registration succeeded.

Ordinary builds keep the XAML compiler's original `Program.Main`. The installed
verifier inspects the installed assembly's PE metadata before launching it and
requires the requested probe entry point. It does not load or execute the App to
inspect metadata. A build or entry-point mismatch fails verification.

Failure evidence includes bounded actual result and progress read lengths when
those reads occurred. A null length means no bounded read was established, rather
than an empty file. Arbitrary probe text, paths and inherited environment values
are excluded. Existing failure states and success criteria remain independent of
these additional observations. Installed execution requires a clean Windows test
user and remains a CI gate.

The source513c682 CI failure established a running App and invalid result/progress,
with no retained raw lengths. It did not establish an empty output, a specific
WinUI call failure or a CfSharp defect. This diagnostic change makes no such claim.
