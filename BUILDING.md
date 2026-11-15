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
`harness-env.sh` before direct `dotnet` commands. NuGet packages are shared under
the primary checkout's `.harness-cache/<os>-<arch>/packages`; `NUGET_SCRATCH`
coordination locks use the sibling `scratch/` for all consumers of those packages.
CLI home, HTTP/plugin caches and other temporary files stay under each checkout's
`.harness/`.
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

On native Windows, restore the pinned WiX tool explicitly before packaging:

```powershell
pwsh ./eng/restore-tools.ps1 -Plan
pwsh ./eng/restore-tools.ps1
```

This reuses WiX 6.0.2 under `.harness/tools/wix-6.0.2` or installs that exact
version there. Unknown or mismatching existing targets fail without replacement.
The publisher prefers the local tool and uses checkout-local WiX extension
storage; process PATH changes do not persist. Existing global tools remain intact.

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
`--parameter packageProfile=Full`. Full retains high CAB compression; Candidate
uses medium CAB compression after a same-payload comparison. ZIP compression and
native/MSI gates are unchanged. Actual stage times are logged and copied into the existing artifact
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
The current reviewed client SHA-256 is
`45ab3344243903fe5bc057d51d1d61c9356c023ee19a773471f13befb4e7d1d6`.
Its owned-state check refuses unmarked requester clones; use a fresh owned
requester/output instead of adopting unknown legacy state. Historical candidate
receipts retain their original client identity and are not rewritten.

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
Receipts also bind the native host/architecture, resolved SDK version and actual
libmpv identity (including the reviewed Windows lockfile). Package/upload reuse
fails after an environment change or when an older receipt lacks that identity.
Changing the worker budget alone does not invalidate passing evidence.
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
After successful draft creation only, a missing exact-tag release gets at most
four lookups, with waits of 0.5, 1 and 2 seconds between attempts. API errors
and visible releases still reach the existing failure and identity checks
immediately; the uploader never recreates a draft or retries conflicting bytes.

## Qualified candidate checkpoint (2026-11-15)

These draft artifacts belong to the source revisions below. Later documentation
commits do not change their provenance or imply another build or test run.
Each native software gate passed 317 unit and 9 headless UI tests with zero
build warnings or errors.

| Candidate | Packaged source | Verified result |
| --- | --- | --- |
| Windows x64 Candidate | `2b40d193497fbed5d01b256599f6465b34d46134` | ZIP and required MSI; [draft](https://github.com/Belgian-Coder/HandyPlayer/releases/tag/untagged-d9af21a02aa4cf5fae2e). Complete request 233.95s, packaging 163.92s. |
| Windows x64 Full | `4fcccbb6701c6a131da9597b8a67c37d543b652b` | ZIP, required MSI and Inno EXE; [draft](https://github.com/Belgian-Coder/HandyPlayer/releases/tag/untagged-a26cc65bc91044c9f68c). Complete request 397.25s, packaging 285.84s. |
| macOS ARM64 | `83702bb1ddb742431df45f14e27b2d436483f881` | ZIP and DMG, strict ad-hoc signature checks; [draft](https://github.com/Belgian-Coder/HandyPlayer/releases/tag/untagged-342f0e28e63da3b840fb). Original validation/package request 72.08s; later upload-only resume 23.07s. |

All uploaded assets, including each draft manifest, were verified by exact size
and SHA-256. The Mac upload retained the original qualified checkout, packages
and validation/artifact receipts. Its first upload attempt created the draft but
stopped before assets when the immediate release-list lookup did not find it;
the guarded resume reused that exact draft and staged bytes. Failed-attempt
evidence remains retained. No Mac candidate was rebuilt for authentication or
documentation changes.

Windows Candidate's uploaded payload was 223,777,888 bytes versus Full's
310,000,496 bytes, a 27.8% reduction. These requests used different commits and
cache states; their timings are not a controlled measurement of Inno's cost.
Hosted Windows, Ubuntu and macOS CI passed for `2b40d19` in
[run 43753448161](https://github.com/Belgian-Coder/HandyPlayer/actions/runs/43753448161).
At that checkpoint PC-MARC packaging stopped at its absent WiX prerequisite.
The later qualification below explicitly restored the pinned tool locally;
WINDOWS11-VM remains the default remote builder.

Interactive playback, GPU, physical devices and installed Windows installer
behavior remain unverified. The Mac candidate is ad-hoc signed; Developer ID
signing and notarization remain unperformed. These are testing drafts, not
public production releases. Original outputs, failed-run evidence, warm caches,
primary checkouts and existing worktrees are retained.

## PC-MARC qualification and measured packaging changes

Measurements used native Windows build 26200, SDK 11.0.100, PowerShell 7.6.10
and two build/test/CAB workers. Warm caches were retained. The `d5cd1c5` software
baseline passed all 326 offline tests with Cobertura enabled: first full gate
18.34s, warm no-change 9.59s, and a one-comment incremental source edit 9.41s.
The comment was restored byte-for-byte afterward. These are elapsed runs, not a
claim that less coverage was executed. Restore/build/test stages were
1.2/8.5/8.3s initially and 0.4/0.8/8.0s on the warm repeat.

The first Candidate package took 111.71s: publish 38.14s, portable copy 8.97s,
ZIP 11.82s and MSI 51.49s. A subsequent same-source/configuration/RID
`-NoRestore` package took 61.59s, with publish 0.64s and high-compression MSI
47.09s. Its portable directory move took 0.005s; all 273 files and 303,973,469
bytes matched the original copied directory by SHA-256. The total difference
also includes warm compilation/restore reuse and is not solely a move speedup.
The real warm run exposed a scalar argument-splat bug in `-NoRestore`; the
publisher now keeps that option as one native CLI argument.

A separate WiX 6.0.2 comparison used the exact same portable input and two CAB
workers: high compression took 62.82s and produced 97,401,050 bytes; medium took
25.73s and produced 102,369,498 bytes. Both passed ICE validation and extracted
to the same 273 files/303,973,469 bytes with every payload hash equal. Candidate
therefore uses medium CAB compression, trading 4,968,448 bytes (5.1%) for the
measured build-time reduction; Full and direct WiX builds retain high. Hardware
load affects absolute times. The existing ICE60 file-language warning was
reported in both variants; no ICE was suppressed.

Repeat the package comparison without rebuilding the app or changing caches:

```powershell
./benchmarks/measure-windows-msi.ps1 -PortableDirectory <qualified-portable-folder>
```

It retains input hashes, build/validation logs, timing/size measurements and
read-only MSI payload verification under `.harness/msi-profile/<run>/`.
For an existing package, run `eng/verify-windows-package.ps1` with absolute
`-Msi`, `-PortableDirectory` and a fresh ignored `-OutputDirectory`. It reads MSI
tables and embedded CABs without installing or registering the application.

Run every offline contract and the concurrent local-feed restore fixture:

```powershell
foreach ($name in 'Harness','Pipe','Native','Publish','Release','Draft','Tools','PackageVerification','NuGetScratch') {
    pwsh -NoProfile -File "./tests/$name.Tests.ps1"
    if ($LASTEXITCODE) { throw "$name contracts failed" }
}
pwsh ./validate.ps1 -Coverage -MaxParallelism 2
```

The NuGet fixture restores one initially missing, unique package concurrently
from a local feed into the shared package cache. Both processes use shared
coordination locks and separate CLI/HTTP/TEMP directories, then verify archive
and extracted bytes. Existing cache entries are never cleared. NuGet documents
the requirement for shared scratch locks when sharing package caches in its
[cache guidance](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders).

Native GUI smoke uses a fresh `HANDYPLAYER_DATA_DIR`, no connection key, and
locally generated script-free media. Launch, decode/render, play/pause/seek and
close can be checked without a physical device or existing library. Installer
execution remains a separate operational check in a disposable environment;
do not install over a working app. Developer ID signing/notarization and the ten
LiveDevice tests require their separately authorized platform/hardware gates.
