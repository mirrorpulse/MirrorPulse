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

## Independent protocol v2 verification

The independently maintained Workers have the following exact-package evidence.
Preview and dry-run candidates remain separate from default stable distribution.

| Adapter | Candidate | Native x64 and ARM64 evidence | Mutation boundary |
| --- | --- | --- | --- |
| Local | Stable 1.0.0 | [12 Worker cases and one production Host case](https://github.com/mirrorpulse/adapter-local/actions/runs/37328416807) per architecture | Protected native file operations; explicit recovery copies after ambiguous replacement |
| WebDAV | Stable 1.0.0 | [14 Worker cases and one production Host case](https://github.com/mirrorpulse/adapter-webdav/actions/runs/37398809874) per architecture | Conditional HTTP requests and supported directory locks; lost acknowledgements are not blindly replayed |
| SMB | Stable 1.0.0 | [18 actual shared-file cases and one production Host case](https://github.com/mirrorpulse/adapter-smb/actions/runs/37398735064) per architecture | Windows identities and protected native handles; unsupported cross-root or directory-tree moves are refused |
| FTP / FTPS | Stable 1.0.0 | [23 Worker cases and one production Host case](https://github.com/mirrorpulse/adapter-ftp/actions/runs/37432810833) per architecture | Optimistic writes with staged bytes, retained originals and remote operation receipts; each root can instead refuse mutations |
| SFTP | Stable 1.0.0 | [24 Worker cases and one production Host case](https://github.com/mirrorpulse/adapter-sftp/actions/runs/37432860025) per architecture | Optimistic writes with staged bytes, retained originals and remote operation receipts; host keys must be approved or pinned |

These gates verify the exact signed package, its private runtime, independent root
credentials, disabled roots, and the production Host boundary. All five formal
1.0.0 releases verify the built-in product trust anchor. The published FTP and
SFTP package bytes match the candidates tested on both architectures. This
evidence does not complete offline journal recovery or Explorer acceptance for
every Adapter.

### Network v2 optimistic write boundary

The stable FTP/FTPS and SFTP 1.0.0 Workers provide verified staged uploads,
same-root file moves, retained file deletion, directory creation and empty-only
directory deletion. Each root selects `mutationPolicy=Optimistic` (the default)
or `ReadOnly`. Existing move destinations, cross-root moves and directory-tree
moves are refused. Historical read-only dry-run candidates do not represent
these stable releases.

Remote receipts bind the stable operation ID, root, paths, preconditions and
content digest. Staging, previous content and receipts use reserved sibling names
that are omitted from normal directory pages. They consume remote space and must
remain available while a result is unknown. Retries read back the result;
they do not blindly repeat an uncertain rename. A cancellation acknowledgement
does not establish that a remote publication was rolled back.

Metadata and full-content checks improve conflict detection, but external writers
can race the final check and rename. Previous copies may miss the last concurrent
edit. This policy supplies neither atomic version CAS nor exactly-once semantics.
The signed network v2 Host profile verifies actual writes and replay, retained
bytes, moves/deletes, directories, hidden evidence and a separate read-only root.
Both formal releases passed this profile on native x64 and ARM64 using the same
organization-signed package. Product journal and Explorer gates remain separate.

## Published stable protocol coverage

| Adapter | Read | Write | Move | Delete | Offline upload | Conflict detection |
| --- | --- | --- | --- | --- | --- | --- |
| Local directory | Signed Worker + CfSharp demand | Signed Worker + Host upload | Worker file/directory implementation | Worker file/directory implementation | ARM64 Local CLI replay verified; complete journal coverage partial | Revision check |
| WebDAV | Signed Worker + CfSharp demand | Signed Worker + Host upload | Conditional file moves and supported directory locks; product acceptance partial | Conditional files and empty directories; product acceptance partial | Not verified end to end | Conditional ETag and target checks |
| SMB | Signed Worker + CfSharp demand (live UNC share) | Signed Worker + Host upload (live UNC share) | Protected file moves; moves of directory trees or between roots refused | Files and empty directories | Not verified end to end | Revision and protected native handle checks |
| FTP / FTPS | Signed Worker + CfSharp demand; v2 Host profile uses FTPS | Signed Worker + Host upload, staged digest checks and retained bytes | Same-root files; existing targets and directory-tree moves refused | Retained files and empty directories | Operation receipt recovery verified; complete offline journal replay unverified | Optimistic metadata and content checks; external writers can race publication |
| SFTP | Signed Worker + CfSharp demand with pinned host keys | Signed Worker + Host upload, staged digest checks and retained bytes | Same-root files; existing targets and directory-tree moves refused | Retained files and empty directories | Operation receipt recovery verified; complete offline journal replay unverified | Optimistic metadata and content checks; external writers can race publication |

The WebDAV loopback fixture exercises the installed signed Worker, Host pipe,
CfSharp-compatible demand reads, and stale ETag upload rejection without a
remote overwrite. It does not establish compatibility with every WebDAV server.
The signed Local ARM64 CLI regression verifies real offline file edits and replay
through Host and CfSharp, including cursor/catalog persistence. It does not prove
all directory, metadata, multi-root or Explorer recovery cases. Remote operation
receipt recovery does not establish complete offline journal replay. The SMB CI
fixture creates a Windows share and verifies the signed Worker against its UNC
path. This establishes the Host and CfSharp
demand path, not Explorer's complete offline synchronization behavior.

The stable network v2 Workers expose root-bound `Stat`, `ReadRange`, `Upload`,
`Move`, `Delete` and directory creation. Their tests launch separate EXEs, pass
credentials through the current-user pipe, perform real protocol transfers, and
check stale revisions. FTP tests cover plain FTP and both FTPS TLS modes. SFTP
tests cover host-key approval and pinning, server disconnect, Worker restart,
and retry. Lost acknowledgements and interrupted mutations are recovered using
stable operation bindings, retained content and remote receipts. Neither protocol
path has an atomic compare-and-swap. These Workers must not advertise strong
conditional writes, exactly-once execution or conflict-free offline synchronization.

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
- `SignedNetworkV2WorkerProcessTests` installs the exact signed FTP or SFTP v2
  package using the product trust policy and verifies root credentials, actual
  writes and replay, retained bytes, namespace operations, hidden receipts,
  stale revision rejection and a separate read-only root through the Host.
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
