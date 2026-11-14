# HandyPlayer development

`AGENTS.md` is the sole project instruction file for Codex desktop and Claude
Code. Claude Code 2.1.277 or newer loads it natively when no `CLAUDE.md` or
`CLAUDE.local.md` exists in the project or its ancestor directories. A user-level
`~/.claude/CLAUDE.md` does not block this fallback. Do not create project Claude
instruction files or hooks that print these instructions. Open either agent from
the repository root; preserve selected model/reasoning settings and global
configuration.

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
release solely because a filtered local run passed. Releases use a new
`publish/releases/<rid>/<run>/` directory; retain previous output and failed-run
evidence. Installer/native prerequisites and signing gates remain required.

After every commit and every release, perform a cleanup checkpoint before
continuing or reporting completion. Remove only proven task-owned disposable
scratch/fixtures and finished, clean, pushed worktrees after resolving paths
and checking they remain inside this project. Before push retain the active
worktree; repeat the checkpoint after push from the primary checkout. Preserve
unknown/untracked data, user WIP, authentication material, release/audit/rollback
evidence, warm package/HTTP/plugin/CLI caches and useful incremental `bin`/`obj`.
Inspect ignored contents before removing a worktree; if any must be preserved,
retain it and report the exact limit. Never use force removal, `git clean`,
`git worktree prune`, recursive sweeps or blanket output/cache pruning. Keep the
recovery branch. This bounded cleanup authorization does not permit unrelated
deletion or live-device operations.

## Validation proportional to risk

For Codex desktop and Claude Code, choose checks by asking what meaningful
failure they can catch and whether that benefit justifies their cost. For
instruction/documentation-only edits, review the changed text, references and
`git diff --check`; skip unrelated builds, tests, app launches, screenshots and
independent reviews. Do not start WSL or install tools just to validate prose.
For implementation changes, build/check the changed module and run affected
tests first; broaden only for unresolved risk, integration effects or release
criteria. Full CI and release gates remain required where the change or release
warrants them, including safety, security, device and data-compatibility checks.

Reuse successful evidence while its relevant source, dependencies, configuration
and environment remain unchanged. Do not automatically repeat broad validation
after each edit or at agent Stop. Preserve useful warm caches and incremental
outputs under the existing cleanup rules. Report what ran, what was skipped and
any material gap. This policy takes precedence over routine unconditional local
validation/review instructions; approval, operational safety and release gates
remain intact.
