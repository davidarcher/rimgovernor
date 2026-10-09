# Agent runbook

[Agent rules](../../AGENTS.md) · [Workflow](development-process.md) ·
[Choose tests](testing/choose-tests.md) · [Acceptance guide](testing/acceptance-guide.md)

Several sessions share the maintainer's Windows machine. Each task uses its
own worktree, game copy and bridge root.

## Shared with peers: never touch these from a task

| Shared resource | Rule |
| --- | --- |
| Steam installation and shared `.rimgovernor/isolated-rimworld` | Never replace their DLLs. |
| Running games | Stop only your root's game or a PID verified against your private path. Never kill by image name. |
| Go build cache | Keep the default `GOCACHE`; never clear it or create a private cache. A missing cached `<hash>-d` file permits retrying the failed build. |
| Main checkout | Only the landing lane integrates there; the player launcher builds from it. |
| `.git/rimgovernor-land.lock` | Let the lane wait. Remove a stale lock only after confirming its recorded PID is gone. |

## Private to the worktree

From `go/`, prepare the private layout:

```powershell
go run ./internal/nativeaccept/cmd/acceptance setup
```

Setup discovers Steam and Harmony. Override with `-rimworld`, `-harmony`,
`RIMGOVERNOR_RIMWORLD_DIR` or `RIMGOVERNOR_HARMONY_DLL`. Keep the worktree path
short: setup refuses game-copy paths RimWorld cannot open.

| Location | Contents |
| --- | --- |
| `.rimgovernor/native-rimworld/` | Private game executable and mod; game data directories are junctions to Steam. |
| `.rimgovernor/bridge/` | Config, endpoint record, private profile and saves; overridden by `-root`. |
| `.rimgovernor/native-builds/` | Staged native builds. |
| `.rimgovernor/bin/` | Controller and acceptance executables. |

Build mods through `acceptance setup -rebuild [-fixture A,B]`, never by invoking
`scripts/build_native_mod.ps1` directly. Setup supplies paths safely and refuses
to install while a game uses that copy. Do not rebuild a binary or mod while
a harness runs from it.

`acceptance run` can heal a stale mod or missing fixture in this worktree's
private copy: it stops that copy's game, rebuilds and records the repair under
`healed` in `result.json`. It does not heal shared installations.
`-no-heal` disables repair; `suite` requires a matching build.

## Running a case

Use absolute paths for `-root`, `-output` and `-rimgovernor`.

```text
acceptance doctor -root <root> -rimgovernor <exe>
acceptance run <area>/<case> -root <root> -output <fresh directory>
acceptance why <output>/<area>/<case>
acceptance stop -root <root>
```

`doctor` checks paths, build freshness, expansions, endpoints, binaries and
orphaned processes. `run` and `suite` include preflight automatically.
Before removing a worktree, stop its root to avoid orphaning its game.

For long runs, build the acceptance executable and launch it with
`Start-Process -WindowStyle Hidden -PassThru`, redirecting stdout/stderr under
`.rimgovernor/`. Retain the PID and wait through the execution tool's completion
mechanism; do not use sleep/poll shell loops. Large suites must run detached.

Read `diagnosis.txt` or `result.json` first. `acceptance why` summarizes failed
stages, native refusals, Concern blockers and authority changes, with artifact
and flight-row references. Controller stderr is only startup/fatal output.

The runner keeps its game by default. Stop it before changing expansions or
using a fixture-less production build. `acceptance warm -root <root>` boots
to the menu; `-background` detaches that boot.

Outputs accumulate. `acceptance prune -output <absolute output directory>
-keep 5 -dry-run` previews removal of old result bundles; omit `-dry-run` to
apply. Checkpoint rings live under the root, separately from run outputs.
Resume, staging, breakpoints and fixture iteration are in the
[acceptance guide](testing/acceptance-guide.md).

## Remote agents

A Linux checkout without licensed game files runs Go checks but no acceptance.
Record `Unverified: no acceptance run (remote agent)` in the commit body.

Compile C# with `scripts/build_native_ref.sh`, using public reference assemblies.
Fixture properties pass through, for example `-p:UpkeepFixture=true`. Install
`dotnet-sdk-8.0` through the configured package manager; no game installation
is needed for this compilation.

Use GitHub MCP for GitHub operations in cloud sessions where `gh` and GraphQL
are blocked. Repository: `davidarcher/rimgovernor`. Read body/comments with
`issue_read`, find matches with `search_issues`/`list_issues`, and edit or close
with `issue_write`. If the landing lane cannot close an issue, close it there
and comment with the landing commit. Pull requests remain disabled.

Remote Windows gameplay uses [handoff](testing/remote-handoff.md),
[workflow](testing/remote-workflow.md) and [encrypted bundles](remote-bundles.md).
Those runners use job-local dependencies, not Steam discovery or peer profiles.

## Landing

Follow the [development workflow](development-process.md#landing).
