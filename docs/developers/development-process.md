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

From `go/`:

```powershell
go run ./cmd/test > ../test.out 2>&1
```

Wait for the final `test: PASS` or `test: FAIL`. The command formats changed Go
files, runs analysis and short tests, and identifies relevant native checks.
Affected-input selection determines which checks run. Do not follow it with another full
Go suite. Use [choose-tests](testing/choose-tests.md) for skipped tests, C#/wire
changes, concurrency and native evidence.

Keep checks and their limitations in the milestone commit. A passing result
survives a clean merge of main; do not repeat it merely because main moved.
Native acceptance is a nightly signal, not a pre-landing gate.

## Landing

Commit each coherent milestone, with the final subject on the branch tip.
From `go/` in the branch worktree:

```powershell
go run ./cmd/land
git fetch origin main
git push origin main
```

The lane locks the repository, fast-forwards local main to origin/main, merges
main into the branch, squash-lands in the main checkout and resets the branch.
It does not push. It closes the issue named by an `issue-<n>-...` branch or
`-issue <n>`; `-no-close` suppresses that close.

- Run the lane once per ready milestone. `land -test` is an alternative to
  `cmd/test`, not an additional check.
- On a rejected push, fetch, re-land and push without retesting.
- Resolve a reported merge conflict in the task branch. Run `go build ./...`;
  also vet touched packages if Go files conflicted. For generated protobuf
  conflicts, resolve the schema and regenerate both languages.
- Do not edit the main checkout, manually squash there, open a PR, force-push,
  push a task branch, or push a branch SHA directly to main.
- The launcher builds from main. Restart it after landing when testing the
  player installation.

Record out-of-scope work in an existing matching issue or a new focused issue.
Do not turn unverified coverage into a new issue; state it in the commit.

## Project-wide build

C# and shared protobuf changes require `task build` and `task test` at the
repository root. The [Taskfile](../../Taskfile.yml) pins the Go toolchain and
composes each project's build and test gates. Outputs live under
`.rimgovernor/task/`; unchanged native and protobuf inputs are checksum-skipped.

Missing machine-local inputs report `unavailable`. The native build accepts
`RIMWORLD_MANAGED_DIR` and `HARMONY_ASSEMBLY`; game-free remote compilation is
described in the [runbook](agent-runbook.md#remote-agents).

## Documentation ownership

Keep procedures here, machine setup in the runbook, check selection in
choose-tests, and behavior in the owning contract. Update the
[documentation map](../README.md) when adding a page. Use current behavior,
constraints and rationale; put implementation history in Git and unfinished
work in issues.
