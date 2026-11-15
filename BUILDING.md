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

## Package locally

After the full gate and required native acceptance, use only the affected host's
existing entry point. No script uploads a production release.

| Platform | Command | Native requirements and outputs |
| --- | --- | --- |
| Windows x64 | `pwsh ./publish-win.ps1 -RequireMsi` | libmpv; WiX 6.0.2 for required MSI. ZIP, MSI, optional Inno Setup EXE, SHA-256 manifest. |
| macOS ARM64 | `bash ./publish-mac.sh` | macOS tools, libmpv and dependencies; existing ad-hoc signing and strict verification. `.app`, ZIP and DMG. |
| Linux x64 | `bash ./publish-linux.sh` | Linux/native package tools; tar.gz, DEB and optional AppImage. |

Each invocation creates `publish/releases/<rid>/<run>/`; select only the exact
artifact paths printed by that invocation. Preserve previous and failed runs.
Publishing keeps incremental compilation and rebuilds packaging stages. Windows
`-NoRestore` and Bash `HANDY_NO_RESTORE=1` require a matching successful
self-contained restore for that RID/configuration in the same checkout.

macOS `PREBUILT_DIR` supplies input for packaging; it is not native validation.
The Mac script supports `osx-arm64` and ad-hoc signatures. It does not implement
Developer ID signing, notarization, or an Intel Mac release lane.

The repository defines no release upload destination or upload authentication.
GitHub CI uploads test evidence only, using `actions/upload-artifact`; its
workflow has `contents: read`. Delivery automation must identify the destination
and credential mechanism separately and retain all existing acceptance gates.
