# Native build and validation

Use the host's native filesystem and tools for this Avalonia/libmpv application.
Keep the primary checkout unchanged when using concurrent writers: create a
`feature/` branch under `.wt/<task>` as described in `AGENTS.md`. Preserve existing
worktrees, prototypes, ignored data and warm caches.

| Host | Native work | Acceptance boundary |
| --- | --- | --- |
| WINDOWS11-VM | Windows x64 source, validation, Windows packaging | Check SDK, libmpv and installer tools on that host; a VM run does not establish physical device or GPU behavior. |
| PC-MARC | Windows x64 source, validation, Windows packaging | Use native Windows UI/media/device tools; no WSL dependency. |
| MacBook-Pro ARM64 | macOS ARM64 source, validation, `.app`/ZIP/DMG packaging | Use native macOS tooling and libmpv dependencies; verify signatures and actual playback on the Mac. |

These are intended host roles, not a claim that prerequisites or native
acceptance have passed on each machine. There is no required container lane.
Linux validation remains in GitHub CI; existing Linux x64 packaging requires a
Linux host with its documented native prerequisites. Containers cannot establish
Windows/macOS UI, media, hardware or installer acceptance.

## Validate

`global.json` requires SDK **11.0.100**, with `latestFeature` roll-forward within
.NET 11. PowerShell 7 runs the same entry point on every native host:

```powershell
pwsh ./validate.ps1 -Suite Unit -Class '*PlaybackCoordinatorTests' -Plan
pwsh ./tests/Harness.Tests.ps1
pwsh ./validate.ps1 -Suite Unit -Class '*PlaybackCoordinatorTests' -MaxParallelism 1
pwsh ./validate.ps1 -Coverage
```

The plan and harness contract checks need no SDK, downloads, native media or
hardware. They validate command construction and failure propagation, not the
application. If SDK 11 is missing, report that exact prerequisite; do not change
the target, pin or global SDK configuration to make older tooling appear valid.

The full default gate restores, builds with warnings as errors, and runs both
unit and headless UI suites. Live-device tests stay excluded unless explicitly
requested with `-IncludeLiveDevice`; their credential name is
`HANDY_CONNECTION_KEY`. A focused run does not replace the full release gate or
native emergency-stop, playback, device and installer acceptance.

Validation restores projects sequentially, bounds MSBuild workers and concurrent
MTP test modules to two, and accepts `-MaxParallelism 1..64` for a deliberate
per-invocation budget. This does not limit xUnit threads inside each module.
Avoid concurrent validation/publish processes in the same checkout, where
`bin`, `obj`, test reports and UI screenshots are shared writable state. Coordinate
other projects separately; these controls do not implement a host scheduler.

Use `-NoRestore` only after a matching successful restore and `-NoBuild` only
with current build output. Source `harness-env.ps1` or, from the repository root,
`harness-env.sh` before direct `dotnet` commands. Only NuGet packages are shared,
under the primary checkout's `.harness-cache/<os>-<arch>/packages`; CLI home,
HTTP/plugin caches and temporary files stay under each checkout's `.harness/`.
Do not share generated output or writable caches between hosts or architectures.
MTP's Unix socket paths have a 103-byte UTF-8 limit. For long checkout paths,
the helpers set `TESTINGPLATFORM_PIPE_DIRECTORY` to a short socket-only directory
under the primary project's `.harness/i/`. Its fingerprint includes checkout,
OS and architecture; a full owner marker rejects namespace collisions. Other
temporary files and mutable caches remain checkout-local. If even the primary
project path is too long, use a shorter native project root rather than bypass
the test gate.
For independent remote clones, the shared executor supplies the outer project as
`BUILD_RELEASE_PROJECT_ROOT`. Helpers verify its exact Git root, ownership marker
and matching canonical origin before using its shorter socket namespace. An
explicit pipe directory is preserved when checkout-local (or the matching owned
fallback) and within the byte limit; invalid paths fail rather than being replaced.

## Package locally

After the full gate and required native acceptance, use only the affected host's
existing entry point. No script uploads a production release.

| Platform | Command | Native requirements and outputs |
| --- | --- | --- |
| Windows x64 | `pwsh ./publish-win.ps1 -RequireMsi` | Existing 7-Zip; locked libmpv restore; WiX 6.0.2 for required MSI. ZIP, MSI, optional Inno Setup EXE, native dependency receipt and SHA-256 manifest. |
| macOS ARM64 | `bash ./publish-mac.sh` | macOS tools, libmpv and dependencies; existing ad-hoc signing and strict verification. `.app`, ZIP and DMG. |
| Linux x64 | `bash ./publish-linux.sh` | Linux/native package tools; tar.gz, DEB and optional AppImage. |

Each invocation creates `publish/releases/<rid>/<run>/`; select only the exact
artifact paths printed by that invocation. Preserve previous and failed runs.
Publishing keeps incremental compilation and rebuilds packaging stages. Windows
`-NoRestore` and Bash `HANDY_NO_RESTORE=1` require a matching successful
self-contained restore for that RID/configuration in the same checkout.
Publish builds default to two MSBuild workers and sequential project restore;
Windows accepts `-MaxParallelism`, and Bash accepts `HANDY_MAX_PARALLELISM=1..64`.
The Windows publisher defaults to `-PackageProfile Full`, preserving automatic
Inno EXE creation when its compiler exists. `-PackageProfile Candidate` omits
only that optional EXE and always requires ZIP plus MSI. The shared Windows
request selects Candidate by default; choose Full with
`--parameter packageProfile=Full`. Compression levels and native/MSI gates are
unchanged. Actual stage times are logged and copied into the existing artifact
manifest; skipped Inno time is zero, not an estimated saving.

Windows publishing restores `eng/native-dependencies.json` through the same
helper as `pwsh ./eng/restore-native.ps1`. The feature candidate explicitly pins
SourceForge's non-v3 `20261108-git-55d3feb45c` archive, published archive SHA-256
and extracted DLL SHA-256/size/PE x64 header (`mpv v0.42.0-5-g55d3feb45`). This is
a reviewed native baseline
change from the VM's earlier unreceipted `v0.41.0-252-gc401ef9c3` cache; primary
cached bytes are preserved. It does not establish interactive playback acceptance.
Restore verifies archive members, refuses links/traversal and mismatched existing
targets, retains failed evidence and reuses only verified warm archives. A supplied
`HANDY_NATIVE_ARCHIVE` (or standalone `-ArchivePath`) is read-only input verified
against the same pin. `native-dependency.json` ships alongside candidate artifacts
and participates in their checksum manifest; no extraction or installer tool is
installed globally by the restore.
The shared Windows request restores the locked DLL before its full software gate,
then publishing verifies the same bytes again before packaging. This keeps the
test/build outputs aligned with the selected native candidate.

macOS `PREBUILT_DIR` supplies input for packaging; it is not native validation.
The Mac script supports `osx-arm64` and ad-hoc signatures. It does not implement
Developer ID signing, notarization, or an Intel Mac release lane.

## Exact-source candidates and GitHub drafts

`eng/release.ps1` adapts those same canonical scripts for a clean checkout at an
exact committed SHA. `eng/release-targets.json` declares the default Windows VM
and native ARM64 Mac builder capabilities for the shared SSH request client.
The existing Linux x64 entry point remains directly usable; no Linux remote
builder is declared without a compatible configured host.

The standalone `eng/request-release.py` client uses native Python 3.10+ and the
standard library. Invoke the existing interpreter by its absolute path. For
example, on PC-MARC:

```powershell
$python = 'C:\Users\marcp\AppData\Roaming\uv\python\cpython-3.12.11-windows-x86_64-none\python.exe'
& $python ./eng/request-release.py --target win-x64 --mode plan --execution-context console
& $python ./eng/request-release.py --target osx-arm64 --mode plan --execution-context console
```

Plans read the committed capability manifest without network or state changes.
For an explicitly requested real `validate`, `package` or `upload`, retain
`--execution-context console` so the builder's existing console user's signing
and GitHub authentication stay local. Use `--ssh-config <existing-config>` when
the host uses a dedicated existing SSH configuration; do not copy keys or tokens.
The default execution context is `current`, suitable for unauthenticated static
fixtures. Requests pin committed source and use the declared native host; they
do not transfer uncommitted edits, dependency caches or credentials. Shared client
changes must come from Infrastructure rather than project-specific forks.

```powershell
$sha = git rev-parse HEAD
$output = Join-Path $PWD ('.harness/remote-release/' + [Guid]::NewGuid().ToString('N'))
pwsh ./eng/release.ps1 -Target win-x64 -Mode plan -ExpectedSha $sha -OutputDirectory $output
pwsh ./eng/release.ps1 -Target win-x64 -Mode validate -ExpectedSha $sha -OutputDirectory $output
pwsh ./eng/release.ps1 -Target win-x64 -Mode package -ExpectedSha $sha -OutputDirectory $output
```

Plan mode performs no network or filesystem mutations. Other modes reject dirty
source, SHA mismatch, a nonmatching native OS/architecture, output outside this
checkout, unignored output and redirected output ancestors. A checkout lock
protects adapter invocations. The same request directory holds validation logs,
a SHA/target/checkout-bound `validation.json` receipt, fresh `artifacts/` and an
`artifact-manifest.json` containing file sizes and SHA-256 hashes. Package mode
reuses a matching same-request validation receipt, or runs the full gate when no
receipt exists. Prior package output and failed-run logs are never overwritten.
The guarded `HANDY_RELEASE_OUTPUT` override is adapter plumbing; ordinary
publish scripts continue to create unique `publish/releases/<rid>/<run>/` roots.
The selected packaging profile is bound to validation, package and upload
evidence. Wrong-profile or older-SHA receipts fail; use a fresh request output
when changing profiles. Windows draft tags also include the profile so Full and
Candidate artifacts cannot be silently mixed for the same commit.

Explicit `-Mode upload` requires the same request's passing validation receipt
and candidate manifest and verifies the files again. It uses machine-local
`gh` authentication to create a **draft only** in the existing
`Belgian-Coder/HandyPlayer` repository, tagged
`ci-test-<target>-<profile>-<full-commit-sha>` on Windows (the profile is omitted
for other platforms). Source commits must already exist in that
GitHub repository. Existing exact assets are reusable; mismatched assets,
changed tags or a non-draft release fail. No asset is overwritten and no public
release is published. The portable `draft-manifest.json` records candidate hashes
and `draft-upload.json` records the verified draft URL. Missing `gh` or local
authentication is a prerequisite failure, never a reason to copy credentials.

Software validation and macOS ad-hoc signature checks do not establish native
playback/device/installer acceptance, Developer ID signing or notarization.
Candidate manifests keep those limits explicit. GitHub CI still uploads only
test evidence, using `actions/upload-artifact` with `contents: read`; the draft
uploader is an explicit local/remote adapter action, not a new CI workflow.
The uploader checks draft state before each asset and afterward. GitHub does not
provide an atomic draft-only asset upload, so another actor must not publish the
same draft while an upload is running.
