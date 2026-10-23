# CLI transfer verification

Run `eng/verify-cli-transfers.ps1` only in the disposable native Windows test
environment with `MIRRORPULSE_NATIVE_TEST=1`. It publishes the actual CLI and Host,
installs signed packages through the public control contract, and uses a fresh
GUID fixture. It does not authorize running native tests in a personal sync root.

The default invocation takes `-PackagePath` for the signed Local package and
`-EvidencePath` for its report. It retains schema 2 and two distinct Local
instances, each using the manifest's declared `local` root. It exercises both
directions; it does not establish heterogeneous backend support.

Add `-WebDavPackagePath` for a second signed package to exercise Local to WebDAV
and WebDAV to Local. Schema 3 retains two installations, two independent actual
Worker processes, and the declared `local` and `webdav` roots in distinct
instances. Host installation verifies signatures. The verifier additionally
records each package digest and binds each observed private-runtime Worker to
its installation. It never fabricates extra roots in a single-root manifest.

The test-only WebDAV process binds a loopback HTTP endpoint and serves exactly
`reverse.bin` and `forward-moved.bin` from the ordinary private `source/right`
directory. It supports bounded listing, revision-pinned whole/range reads,
conditional creation/replacement, and conditional deletion. It rejects other
names and reparse-point files. There are no credentials. The process survives
the Host restart and stops explicitly before the stopped-Host audit and fixture
removal. Its actual read, write and delete counts are retained independently.
Those HTTP counts supplement the complete byte and original-receipt checks.

Both modes retain the same required observations:

- Complete original source bytes equal both target copies, with distinct
  publication identities and removal of both original source copies.
- Original copy, deletion, native retirement, name settlement and journal ACK
  receipts remain ordered; an empty journal claim cannot claim a real ACK.
- A later local target write reaches its backend, while a newly recreated
  original backend source becomes a separate registered member.
- Historical resume preserves both later objects and its original status.
  A real Host restart and current root admission precede the final replay.
- An independent stopped-Host oracle takes the exclusive catalog lease, checks
  the original plans and receipts, and verifies complete retained private bytes.
- Authentication checks the exact marker, topology, declared keys and backend
  configuration before permission cleanup, root unregistration and removal of
  the bounded synthetic directory.

CI retains the original Local matrix. Manual native verification adds the
cross-package matrix using the same frozen official release candidate. Each
matrix retains its execution or cleanup failure independently and fails the
job after other verification reports have been preserved. The additional
artifact is `cli-cross-package-arm64-originals`; its completed report is
`cli-cross-package.json` and its original observations are under
`cli-cross-package.originals/`. A failed or absent report is not acceptance.

Managed topology, HTTP and evidence-policy tests do not prove Cloud Files or
full Host transfers. A direct signed Worker probe proves only package/protocol
compatibility with this HTTP origin. Complete acceptance requires the original
source-bound native matrix, both transfer directions, later generations, restart,
private-byte oracle and authenticated cleanup to pass together. Same-instance
multi-root transfers remain a separate post-S06 requirement.

`eng/verify-cli-cross-package.ps1` owns CI package preparation before invoking
the same native verifier. Its CI step supplies the workflow's read-only token
to the frozen release aggregator. A package download or inventory failure is
recorded in `cli-cross-package.originals/verification-failure.json` even when
the native fixture never starts. That record contains only the source, run,
runtime, closed stage and HRESULT; it preserves the original exception and
cannot count as successful verification. A final report must match the exact
source/run/runtime and satisfy the full schema 3 byte, receipt and cleanup
policy. An Actions step conclusion affected by `continue-on-error` is never a
substitute for that original report.

Remote polling diagnostics expose only a closed failure boundary and named poll
stage, preserving the original exception and retry state. They export no remote
IDs, paths, exception text or stacks. `Unknown` remains an unclassified failure;
it must not be interpreted as a successful poll or evidence of a library defect.
