# HandyPlayer development

This is a native Avalonia desktop app, not a browser app. Read `TECHNICAL.md`
for project boundaries and `README.md` for native prerequisites and packaging.
Keep device, emergency-stop, native-media and signing checks intact.

Use `validate.ps1` as the canonical validation entry point. Start with the
smallest relevant suite/class, then run the full default gate before release:

```powershell
pwsh ./validate.ps1 -Suite Unit -Class '*PlaybackCoordinatorTests' -Plan
pwsh ./validate.ps1 -Suite Unit -Class '*PlaybackCoordinatorTests'
pwsh ./validate.ps1 -Suite UI
pwsh ./validate.ps1 -Coverage
```

`-NoRestore` requires a matching successful restore; `-NoBuild` additionally
requires current build output. They do not make stale artifacts valid. Class
filters use xUnit MTP syntax, not VSTest `--filter`. Empty selections fail via
MTP's minimum-test count gate. The unit project references the App, so its build
still includes Avalonia and all referenced libraries.
LiveDevice tests require explicit hardware intent and `-IncludeLiveDevice`.
No SDK available: use `-Plan` and PowerShell/Bash parser checks and report that
application compilation/tests were not run; do not install dependencies.

Create any agent worktree under this project's `.worktrees/` on a `feature/`
branch. Preserve unrelated work/index entries/local commits. Keep generated
files, reports, downloads and scratch work inside this project. Existing
entry points initialize `harness-env.ps1` / `harness-env.sh`: immutable NuGet
packages share the primary checkout's `.harness-cache/<os>-<arch>/packages`;
CLI home, HTTP/plugin caches and temporary files use each checkout's ignored
`.harness/`. `bin`, `obj`, `TestResults`, UI screenshots and `publish` remain
checkout-local. For direct dotnet commands, source the matching environment
helper first. Never share `bin`/`obj` between checkouts or operating systems.

Use native Windows for Windows UI and installer work. There is no required WSL
workflow. If Linux work uses WSL, keep its checkout and `.worktrees` on the
distro's Linux filesystem, inside a single project root; avoid `/mnt/d` for
build-heavy work, and do not reuse Windows native outputs or package caches.
Do not install/reconfigure WSL or change global agent settings as project work.

Publish only the affected platform with the existing `publish-*` entry point.
Incremental compilation is retained; packaging stages rebuild their outputs.
Use Windows `-NoRestore` or Bash `HANDY_NO_RESTORE=1` only after a restore for
that same RID/self-contained configuration. `PREBUILT_DIR` on macOS reuses
supplied output for packaging, not validation. Native prerequisites, installer
failures and signature verification still apply. Do not publish/distribute a
release solely because a filtered local run passed. No cleanup/deletion or
live-device operation is implied by harness maintenance.
