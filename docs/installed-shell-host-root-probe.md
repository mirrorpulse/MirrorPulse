# Installed Shell probe and the existing Host root

The packaged Shell probe verifies custom and unified labels on the single
current-user root already registered by the installed Host. The verifier first
requires the real CLI alias and Host auto-start to succeed, then stops that Host
before running the packaged App probe. It supplies the expected absolute root
from the Host launch environment (`MIRRORPULSE_SYNC_ROOT`) or the same default
`UserProfile/MirrorPulse`. Relative overrides fail rather than depending on two
processes' working directories.

The probe requires that directory to exist. Before changing a label, it reads the
actual fixed current-user registration and checks its path and product Provider
ID against those expected values. It never infers ownership from a discovered
registration. The production registrar's different-path and different-provider
guards remain unchanged.

The probe performs both real registrations and both real Shell label readbacks.
It then restores the original label, icon resource and provider version, and
reads back the restored label, path and provider. Success is written only after
restoration returns. Failed verification also attempts restoration; if both
operations fail, both original exceptions are retained. The probe neither
unregisters nor deletes the Host root.

On failure, the separate minimal Shell diagnostic uses its own random directory
outside the Host root and a unique registration ID. Only that diagnostic's
registration and directory are cleaned up. A diagnostic cleanup failure is
reported without replacing the original product failure. Progress observations,
the original two-minute deadline, real package identity, generated App startup,
App SDK initialization, and native apphost failure reporting remain required.

This corrects a MirrorPulse test fixture defect. CI run 40218294372 reached the
actual packaged App and failed with `InvalidOperationException` (80131509): the
probe had reused the product's fixed registration ID with a fresh random path.
It also supplied a different Provider ID. The existing Host registration was
correctly refused; the separate minimal registration succeeded. These facts do
not establish a CfSharp defect. Installed acceptance still requires a new clean
Windows CI run for the corrected source.

Local tests exercise expected-root rejection and non-ownership with ordinary
private directories, original failure preservation and awaited restoration, and
the verifier's exact child-only root environment. They do not register Cloud
Files, install MSIX, execute the product App, or substitute for installed Shell
readbacks.
