# Official Adapter capability evidence

This matrix distinguishes implemented Worker operations from complete product
acceptance. Source support alone does not prove journal recovery or Explorer
behavior. The Host must not infer capabilities from a protocol's feature set.

## Package and input boundaries

Host applies the same manifest configuration schema when creating or patching an
instance. Ordinary settings cannot inject secret fields or managed credential
references. Credential rotation publishes a new opaque reference only after save;
unused credentials are retired through a durable cleanup record. Workers receive
system and declared cache environment variables rather than inherited tokens or
runtime injection variables. Package admission validates canonical Windows paths,
minimum product/protocol versions, and both x64/ARM64 executable payloads.

The independently maintained WebDAV Worker confines raw relative segments and
returned hrefs to its configured origin and directory. Redirects are not followed.
Its bounded range reader requires HTTP 206, correct Content-Range offsets/length,
identity encoding and consistent HEAD/GET revision metadata before exposing bytes.
PROPFIND is capped at 4 MiB and 8,192 responses and disallows DTDs. Directory
pagination beyond that budget and a revision pinned across separate Host requests
remain contract work. These changes are in the Adapter source and signed candidate
artifacts; consumers of older published packages retain that package's behavior.

## Independent release verification

The [Adapter template](https://github.com/mirrorpulse/adapter-template) supplies
release scripts with data-only environment inputs, full Action commit SHAs and
separate build, sign and publish jobs. Numeric versions are validated before path
creation. Build receives no signing secrets. Manual release dispatch defaults to
verified signed artifacts and does not create a public Release.

Official repositories additionally test each newly signed candidate using a pinned
MirrorPulse verifier and real Host/Worker protocol fixtures on a disposable Windows
runner. Evidence records Adapter and MirrorPulse source commits and package hash.
This gate is separate from testing whichever older package is currently latest.
Environment reviewers, branch/tag protection and signing-secret scope must be
configured by the repository owner; environment names in YAML do not establish
those policies. Candidate integrity checks do not replace the product's publisher
trust check.

## Current protocol coverage

| Adapter | Read | Write | Move | Delete | Offline upload | Conflict detection |
| --- | --- | --- | --- | --- | --- | --- |
| Local directory | Signed Worker + CfSharp demand | Signed Worker + Host upload | Worker file/directory implementation | Worker file/directory implementation | ARM64 Local CLI replay verified; complete journal coverage partial | Revision check |
| WebDAV | Signed Worker + CfSharp demand | Signed Worker + Host upload | Worker implementation; product acceptance partial | Worker implementation; product acceptance partial | Not verified end to end | Conditional ETag check |
| SMB | Signed Worker + CfSharp demand (live UNC share) | Signed Worker + Host upload (live UNC share) | Worker file/directory implementation | Worker file/directory implementation | Not verified end to end | Revision check |
| FTP / FTPS | Signed Worker + CfSharp demand (plain FTP fixture) | Signed Worker + Host upload (plain FTP fixture) | Worker file/directory implementation | Worker file/directory implementation | Transfer retry only | Optimistic revision check |
| SFTP | Signed Worker + CfSharp demand | Signed Worker + Host upload | Worker file/directory implementation | Worker file/directory implementation | Transfer retry only | Optimistic revision check |

The WebDAV loopback fixture exercises the installed signed Worker, Host pipe,
CfSharp-compatible demand reads, and stale ETag upload rejection without a
remote overwrite. It does not establish compatibility with every WebDAV server.
The signed Local ARM64 CLI regression verifies real offline file edits and replay
through Host and CfSharp, including cursor/catalog persistence. It does not prove
all directory, metadata, multi-root or Explorer recovery cases. “Transfer retry
only” does not establish offline journal replay. The SMB CI fixture creates a Windows share and verifies
the signed Worker against its UNC path. This establishes the Host and CfSharp
demand path, not Explorer's complete offline synchronization behavior.

The current FTP and SFTP Workers expose `Stat`, `ReadRange`, `Upload`, `Move`, and `Delete`.
Their tests launch separate EXEs, pass credentials through the current-user
pipe, perform real protocol transfers, and check stale revisions. FTP tests
cover plain FTP and both FTPS TLS modes. SFTP tests cover host-key approval and
pinning, server disconnect, Worker restart, and retry. Both revision checks
combine length with remote modification time; neither protocol path has an
atomic compare-and-swap, so a concurrent writer may still win between the
last check and rename. These Workers must not advertise strong conditional
write or conflict-free offline synchronization.

Evidence:

- `MirrorPulseLocalDirectoryReaderTests`,
  `MirrorPulseLocalDirectoryWriterTests`, and
  `MirrorPulseLocalDirectoryMutatorTests` use temporary local files.
- `MirrorPulseWebDavRangeReadTests`,
  `MirrorPulseWebDavUploadTests`, and
  `MirrorPulseWebDavEtagGuardTests` use HTTP handlers.
- `MirrorPulseSmbDirectoryPollerTests` uses an injected entry source.
- `SignedSmbHostProcessTests` starts the installed signed release against a
  live Windows share. It checks directory enumeration, CfSharp demand range
  reads, conditional upload, and stale revision rejection without overwriting
  the share's newer content.
- `FtpWorkerProcessTests` and `SftpWorkerTransferTests` run real protocol
  fixtures through independent Workers.
- `FtpSignedPackageProcessTests` and `SftpSignedPackageProcessTests` verify
  signed packages and start their installed Workers.
- `FtpWorkerProcessTests.SignedFtpReleaseReadsAndConditionallyUploadsThroughHostAndCfSharp`
  and `SignedSftpHostProcessTests` install current signed releases, route range
  hydration through the Host and CfSharp demand provider, upload through the
  Host, and reject stale revisions against real loopback protocol servers.
- `OfficialAdapterAggregateProcessTests` installs all five signed releases
  using only each `.mpadapter` file and the built-in public trust anchor;
  the Local case starts two independent installed Workers and routes
  CfSharp-compatible demand enumeration and range reads through their separate roots.
- `SignedLocalVersionSwitchProcessTests` installs signed Local v0.1.3 alongside
  the latest release, observes the selected Worker executable in each process
  session, verifies reads, and confirms that a disabled instance starts no Worker
  until it is reenabled after a catalog restart.
- `SignedWebDavWorkerProcessTests` starts the signed WebDAV release against a
  loopback HTTP fixture and verifies directory ETag preservation, demand reads,
  stale upload rejection, and a successful conditional upload.

Independent production sources are [Local](https://github.com/MirrorPulse/adapter-local),
[WebDAV](https://github.com/MirrorPulse/adapter-webdav),
[SMB](https://github.com/MirrorPulse/adapter-smb),
[FTP](https://github.com/MirrorPulse/adapter-ftp), and
[SFTP](https://github.com/MirrorPulse/adapter-sftp). Installed-version behavior
must be checked independently of current source support.

Revise this matrix when complete offline, mutation, and conflict acceptance is
established. See the [product capability baseline](cli-capabilities.md) for Host
and Control limitations. The package and UI must not advertise an unverified
product guarantee.
