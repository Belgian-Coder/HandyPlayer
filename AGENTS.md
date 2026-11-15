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
Validation defaults to two MSBuild workers and two concurrent test modules;
`-MaxParallelism 1` reduces either limit on a busy host. Restore processes
projects sequentially. This does not limit xUnit test-case threads or coordinate
other invocations: run one writer/build per checkout and budget host-wide work
before starting another project. See `BUILDING.md` for native host boundaries.
No SDK available: use `-Plan` and PowerShell/Bash parser checks and report that
application compilation/tests were not run; do not install dependencies.

Create any agent worktree under this project's `.wt/` on a `feature/`
branch. Preserve unrelated work/index entries/local commits. Keep generated
files, reports, downloads and scratch work inside this project. Existing
entry points initialize `harness-env.ps1` / `harness-env.sh`: immutable NuGet
packages share the primary checkout's `.harness-cache/<os>-<arch>/packages`;
CLI home, HTTP/plugin caches and temporary files use each checkout's ignored
`.harness/`. `bin`, `obj`, `TestResults`, UI screenshots and `publish` remain
checkout-local. For direct dotnet commands, source the matching environment
helper first. Never share `bin`/`obj` between checkouts or operating systems.

Use native Windows for Windows UI and installer work. There is no required WSL
workflow. If Linux work uses WSL, keep its checkout and `.wt` on the
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

## Development workflow

Use `.wt/<task>` on a `feature/` branch for concurrent writers. Name each
writer's owned paths and choose one integrator; read-only agents may share a
checkout. Preserve unrelated index entries, working changes and local commits.
Stage explicit owned paths; never use broad add, reset, stash or clean.

Choose focused, valuable checks at meaningful checkpoints. Reuse successful
results while relevant inputs, dependencies, configuration and environment match.
Only the integrator runs combined validation once after integration; broaden
checks for unresolved risk or release requirements. Add regression tests for
valuable behavior, not trivial changes or tests that mirror implementation.
Build incrementally once; do not retry with clean builds or repeat restore,
packaging or full CI without a concrete reason. Documentation-only changes need
text/reference review and `git diff --check`, not app launches, screenshots,
builds or routine independent reviews.

Do not install automatic development validation, formatting, startup, polling,
cost-reporting or Stop hooks. Format only explicitly requested owned files while
no other writer can race the formatter. There are no fixed story quotas, model
lanes, review counts, time/cost reports or mandatory cleanup JSON receipts.
Full CI is not an automatic per-commit or per-push local requirement.
Operational approval, data compatibility, signing, safety and release gates
remain required. On PC-MARC, machine-managed elevation automatically runs shell
commands as Administrator in both Codex and Claude Code, independent of project
location. Do not add project elevation hooks or change sandbox/permission settings
as a workaround. Automatic elevation does not sandbox Administrator commands.

Preserve warm dependencies and OS/cache isolation. Remove only proven task-owned
disposable files and clean, pushed worktrees after reviewing ignored contents
and checking resolved paths stay in the project; retain unknown data, caches,
evidence and recovery branches. Never force worktree removal or sweep/prune.
