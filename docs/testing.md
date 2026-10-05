# Test boundaries

Prepare the isolated SFTP fixture with `pwsh ./eng/setup-test-environment.ps1`
before running the managed solution tests. Environment-dependent tests report
inconclusive results when prerequisites are absent. `eng/verify-test-results.ps1`
checks executed counts and requires every named test in a dedicated suite.
Ordinary CfSharp tests are not all native Cloud Files tests.

## Independent Adapter template gate

`SignedTemplateWorkerProcessTests` requires `MP_TEMPLATE_PACKAGE` and
`MP_TEMPLATE_PUBLIC_KEY` pointing to a disposable signed package and its public
key. It is declared environment-dependent in the general managed suite. The
independent adapter-template CI supplies those inputs on native x64 and ARM64,
then requires that exact method to execute and pass without skips. It generates
a fresh repository, builds its dual-RID package, verifies production publisher
trust/installation and runs the installed v2 Worker through the real Supervisor.
The test covers both roots, file operations, stable retry, mid-upload cancellation,
actual transfer-lease deletion and subsequent session reuse. It uses an isolated
product catalog and does not register Cloud Files or install an MSIX.

## Durability faults

`DurabilityFaultFixtureTests` invokes a child process with a unique temporary
root. It injects either an exception or abrupt `Environment.Exit`, immediately
before or after each of these real persistence boundaries:

| Boundary | Actual API | Reopened observation |
| --- | --- | --- |
| Remote write | Atomic Local-directory writer against a fixture source | Old/new file bytes, no leftover temporary file |
| Journal acknowledgement | CfSharp public operation repository transaction | Pending operation present before commit, absent after commit |
| Poll snapshot | Product snapshot store | Old/new revision, complete JSON file |
| Product catalog | Product catalog command write | Old/new durable command state |
| Pending remote batch | Host catalog opaque replay intent and candidate snapshot | No record before save, identical batch/snapshot after save |
| Conflict copy | Flushed staging/hash/manifest save | Original untouched; incomplete intent before commit, verified copy after manifest commit |
| Full rescan | Product scan generation/phase commit | Stable pending generation across process exit, with no copied Cloud Files journal |

All 28 combinations execute in the managed suite. The probe requires a fixture
marker. It does not register a sync root, open user files, or install packages.
The original crash-probe mode still exercises an uncommitted CfSharp SQLite WAL
transaction in the native suite.

These fixtures establish deterministic failure boundaries. The journal case
uses the official store transaction rather than native change-feed delivery;
the remote case uses a local fixture source rather than a network Worker. They
do not establish production pump recovery or cross-system exactly-once delivery.
Those behaviors need the corresponding Host/Worker integration tests.

```powershell
dotnet test tests/MirrorPulse.CloudFiles.CfSharp.Tests --configuration Release --filter TestCategory=DurabilityFaultFixture
```

Native Cloud Files and installed MSIX checks must run under a clean Windows
test user or a disposable runner. Do not run installation/uninstallation probes
against a user's configured application or storage sources.

The disposable GitHub-hosted native fixture enables a bounded NTFS USN journal
when the image has none. The preparation script rejects other environments.
The product registers the actual content-only `None` policy and uses CfSharp
preview.3's public managed confirmation API; it does not rely on a usable
conditional USN. The native journal fixture still needs NTFS change tracking.
The native rescan test includes real feed overflow, offline root deferral,
directory ACL denial without remote deletion, and a full runtime/store restart
after acknowledgement but before product projection. All of those assertions
remain required; an earlier confirmation success cannot replace the rest of the
test. Root migration from `TrackAll` must also preserve file content and identity
and report the actual registered `None` policy.

## CI evidence

Each CI job uploads `evidence-<job>` with schema 1 JSON containing the checked-out
commit, RID, executed/skipped counts by suite and category, and verified artifact
SHA-256 hashes. The collector re-hashes actual publish/package files and rejects
traversal paths, duplicate suites, mismatched counts, or missing required gates.
It exports only named checks and relative artifact paths, without TRX output,
stack traces, host user paths, credentials, or temporary certificate material.

A manual workflow run additionally requires all 23 named native/official-store
integration methods in `eng/test-suites.json`, and installed ARM64 MSIX checks
on the Windows 11 desktop runner. The x64 Server runner verifies that published
CLI and Host processes reject the unsupported SKU without creating state.
The broad native filter also selects managed helper tests; they remain a separate
category and cannot satisfy a required native method. Each required method must
execute exactly once, pass, and never skip. MSIX installation,
CLI alias, Host auto-start, associations, and uninstall checks are separate from
Shell registration. `shellRegistration=false` explicitly means it was not run.
Installed evidence includes the OS build and product type; a Server installation
cannot satisfy the [supported desktop matrix](windows-support.md).
The ARM64 report requires signed Local CLI regression evidence as well as the
two signed Worker tests; the official aggregate requires all seven named tests.

The manual `Conditional in-sync probe` workflow independently tests a file USN
queried with `FSCTL_READ_FILE_USN_DATA` against the public CfSharp conditional
in-sync operation on x64 Server and ARM64 Windows 11 runners. It requires current
and refreshed tokens to succeed, a stale token after a closed same-length write
to fail, and the edited bytes and out-of-sync state to survive that rejection.
The query is test-only; it does not introduce a product fallback or satisfy the
full-rescan recovery gate by itself. Its native execution is selected by the
dedicated opt-in workflow, outside the existing required native suite. Reports
contain numeric coordination observations and test outcomes, without fixture
paths or file contents.

The completed conditional experiment rejected current and refreshed tokens on
both architectures; its strict expected-success assertions failed. Do not treat
that workflow's failure as evidence that a USN reader alone repairs confirmation.

The separate manual `Protected content confirmation probe` workflow offers two
contracts. `prototype` selects all 15 cases in
`MirrorPulseProtectedConfirmationTests`. They exercise exclusive
references, same-handle content verification and commit, competing processes,
existing writable mappings, mismatches, pre-commit cancellation/failure, and an
oplock break between segments. Both architectures executed and passed all cases
in [run 37132837677](https://github.com/mirrorpulse/MirrorPulse/actions/runs/37132837677).
The report counts selected, executed and passed results separately and rejects
any skip or missing case. Ordinary managed runs declare these cases skipped;
the production suite requires its own named integrations.

The prototype is a mechanism experiment and does not prove production recovery.
`managed` selects 12 public API cases: actual `None`/`TrackAll` policy checks plus
the ten `MirrorPulseContentConfirmationRecoveryTests` methods. They cover binding
and identity refusals, partial data and reparse refusal, a 64 MiB segmented
cancel/dispose/writer race, writable handles/mappings, post-mark cancellation or
write, and native-applied official SQLite failure with full runtime/catalog
restart. The managed report requires all 12 cases to execute and pass on each
architecture. These tests are also mandatory in the full native suite, but a
successful probe alone cannot satisfy signed Local, full rescan or installed
acceptance. See [protected content confirmation](protected-content-confirmation.md).

Download the three `evidence-*` artifacts from one workflow run, then verify:

```powershell
pwsh ./eng/verify-ci-evidence.ps1 -EvidenceDirectory artifacts/downloaded-evidence -ExpectedSourceSha <commit-sha> -RequireNative -RequireInstalled
```

Omit the last two switches for a push/PR run, which does not select those gates.
The three reports must name the same expected commit and their job-specific RID.
