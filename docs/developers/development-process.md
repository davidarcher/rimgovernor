# Development workflow

[Developer guide](README.md) · [Agent rules](../../AGENTS.md)

## Start a change

Use a task branch in a separate worktree. Fetch `origin main` and merge
`origin/main` at session start; every agent lands to the same remote main.

Find the owning component in the [source map](source-map.md), then read its
contract. Follow the existing path:

`native facts → policy → shared plan → Hands → native order → observed outcome`

Add observations, admission, execution and outcome checks together when a feature
needs them. A receipt can complete an order-placement action; it does not prove
the pawn finished the work or the owning Concern is satisfied.

## Verify

From the worktree root:

```powershell
.\RimGovernor.cmd test > test.out 2>&1
```

Wait for the final `test: PASS` or `test: FAIL`. When anything the gate builds changed, the command
runs it ([project-wide build](#project-wide-build)). Do not follow it with another full build or test. [choose-tests](testing/choose-tests.md) covers skipped tests, wire
changes, concurrency and native evidence.

Keep checks and their limitations in the milestone commit. A passing result
survives a clean merge of main; do not repeat it merely because main moved.
Native acceptance is a nightly signal, not a pre-landing gate.

## Landing

Commit each coherent milestone, with the final subject on the branch tip.
From the branch worktree's root:

```powershell
.\RimGovernor.cmd land
```

The lane locks the repository, fast-forwards local main to origin/main, merges
main into the branch, squashes, pushes the squash to origin's main, then moves local
main and the main checkout to it and resets the branch. When origin's main moved
since the fetch, the push is rejected and the lane lands again on top (three tries,
no retest); a push origin refuses for any other reason lands nothing. It closes the issue named by an `issue-<n>-...` branch or
`-issue <n>`; `-no-close` suppresses that close.

- Run the lane once per ready milestone. `land -test` is an alternative to
  `test`, not an additional check.
- Resolve a reported merge conflict in the task branch, then build the touched
  projects (`dotnet build` on them).
- Do not edit the main checkout, manually squash there, open a PR, force-push,
  push a task branch, or push to main yourself.
- The launcher builds from main. Restart it after landing when testing the
  player installation.

Record out-of-scope work in an existing matching issue or a new focused issue.
Do not turn unverified coverage into a new issue; state it in the commit.

## Project-wide build

`RimGovernor.cmd test` runs the gate (`tools/repo/Commands/Gate.cs`). It builds, in
order, [RimGovernor.slnx](../../RimGovernor.slnx) (every C# project; locked restore,
warnings as errors), the native contract probes, the real-Harmony hook set probe and
the native mod, then runs `dotnet test` on the solution, the pure probes, the
proto-boundary probe and the hook set probe at once, printing each when it finishes.
Outputs live under `.rimgovernor/gate/`; the native mod is rebuilt (`native-build
-if-current`) only when its manifest no longer records the worktree's sources and
the game's assemblies.

Missing machine-local inputs report `unavailable`. The gate's native build accepts
`RIMWORLD_DIR` (the directory holding `Assembly-CSharp.dll`) and `HARMONY_ASSEMBLY`; game-free remote compilation is
described in the [runbook](agent-runbook.md#remote-agents).

## Documentation ownership

Keep procedures here, machine setup in the runbook, check selection in
choose-tests, and behavior in the owning contract. Update the
[documentation map](../README.md) when adding a page. Use current behavior,
constraints and rationale; put implementation history in Git and unfinished
work in issues.
