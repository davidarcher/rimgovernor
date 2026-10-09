# Native acceptance

[Choose tests](choose-tests.md) · [Machine setup](../agent-runbook.md)

The runner is `go/internal/nativeaccept/cmd/acceptance`; cases live in
`go/internal/nativeaccept/cases/<area>`. Run commands from `go/`, or build
the runner and use `acceptance` below. `acceptance <subcommand> -h` lists flags.

## Run and diagnose

```text
acceptance list
acceptance run <area>/<case> -root <absolute root> -output <fresh directory>
acceptance why <output>/<area>/<case>
acceptance stop -root <absolute root>
```

The runner owns package checks, profile preparation, game connection, discovery,
start, pause, frozen needs, reporting and cleanup. A case receives its fixture
reply through `s.Prepared()`.

Each case writes `result.json`, native-call/service evidence and its game-log
slice. Failures add `diagnosis.txt`; `acceptance why` reads the same digest.
The report's `world` records seed, save hash and fixture hash. Large replies
are capped by default; `-evidence full` retains them. Acknowledging an
interruption permits progress but does not establish that the fault is harmless.

Use `-repeat N` to reproduce a stochastic failure under one build and
`-seed <s>` for a recorded debug/scenario seed. A save start uses its saved world
and rejects a seed override.

## Adding a case

Register a `cases.Case` with `cases.Register`. A new area must also be imported
by `cmd/acceptance/main.go`.

| Field | Contract |
| --- | --- |
| `Name` | Stable `<area>/<case>` identifier. |
| `Scope` | Exact claim, including why a Go snapshot cannot prove it. |
| `Budget` | Minute-scale runtime; exceeding `cases.MaxBudget` needs a `Reason`. |
| `Start` | Prepared save, fixture, lab, scenario, or `Owned` lifecycle. |
| `RequiredOps` | Fixture operations called by the body or service hooks. |
| `Run` | Exercise the behavior and watch progress. |
| `Postmortem` | Read and assert native outcomes after services stop. |

For serve-driven cases, declare `Serve: &cases.ServeSpec{...}`, then
`s.Serve(ctx, s.Spec())`. Use `s.Reattach(ctx)` for harness reads afterward.
Store assertion inputs with `na.SetCheckpointState`, `Session.Resumed` or
`Session.Prior` so postmortem can run independently.

### Stage the precondition, do not play into it

Start with the state the assertion needs. Native op/read cases normally use
`cases.LabStart()` or `cases.Lab{Colonists: n}`: a pinned blank 100×100 map.
Spawn at known offsets from its center. Terrain-dependent cases need an explicit
exception to `TestFixtureCasesStartPinned`.

Use one `test/*_prepare` operation to spawn, assign, damage and settle the
fixture. Use production operations for the behavior under test. A prepared save
or scenario is appropriate when history, biome or season is the precondition;
do not grow a colony for twenty minutes to arrange a small assertion.

Try a fixture without writing a case:

```text
acceptance fixture <op> [key=value ...] -root <root>
acceptance fixture <op> [key=value ...] -root <root> -loaded
```

The first call loads the baseline; `-loaded` reuses the paused world.
JSON-looking values decode as JSON; other values are strings.

### Keep the game quiet and small

- Prefer Core-only unless the claim requires DLC. The runner's default activates
  installed expansions; a save start activates its saved `modIds`. Narrow
  explicitly with `Config.Expansions` or `RIMGOVERNOR_ACCEPT_EXPANSIONS`.
- Use a quiet storyteller. Needs are frozen except the case's `Keep` list;
  preserve the needs being tested. Use `QuietWorld` only when wild-map
  simulation is irrelevant, not for farm, hunting or husbandry claims.
- Use `na.WaitProgress` with a progress signature and `Terminal: service.Exited`
  for serve-driven waits. Existing `WaitReview`, `WaitPlan` and `WaitRounds`
  helpers cover common cases. No sleep loops.
- Advance gameplay by ticks with `na.RunUntil`/`na.Wait{Ticks: ...}`.
  Wall time is a hang guard. Fail immediately on terminal refusals or service exit.
- Declare budgets in minutes. The default timeout is 20 minutes and cannot be
  less than budget plus five; it is separate from the case budget.
- `cases.Case.Lint` checks the case before execution and in runner tests.
  Departures requiring `Reason` must explain the tested constraint.

The baseline is generated from `na.BaselineStart` on first use and stamped
beside its save; a changed spec regenerates it. A retained seed/save/fixture hash
identifies the input actually exercised.

## Reusing one game across acceptance cases

The runner keeps a fixture-enabled game by default. Declare `NoKeep` for a case
that terminates/replaces the process; an `Owned` start must do so. A kept game
retains its loaded mod configuration: stop it before changing expansions or
installing a new mod build. A fixture-less production build requires fresh boot.

`acceptance warm -root <root>` prepares the menu before a run.
Use the suite runner for worker isolation and parallel cases rather than
sharing one profile between independent runners.

## Checkpointing a slow precondition

A failed run normally resumes from its latest checkpoint on the next run in the
same root. `-fresh` starts over; `-rewind N` chooses an earlier checkpoint.
Keep the report's `resumed_from` with the result.

A resumable body must recognize `s.Resumed()`, restore recorded fixture state
and avoid repeating writes already represented by the bundle. Use
`s.RequestID(base)` for request IDs; resumed attempts get a distinct suffix.
Declare `NoCheckpoint` if the body cannot safely resume, or
`na.CapCheckpoints(reason)` after its last resumable point.

### Postmortem-only and dev iteration

`acceptance run <case> -postmortem-only [-from <label|directory>]` loads a retained
bundle and runs only `Postmortem`. It requires a fresh output directory and
cannot combine with fresh/repeat/seed options or a suite.

`acceptance dev <case> -root <root> [-from <label|directory>] [-watch]` rebuilds
the controller, reloads the bundle and runs `Run` plus `Postmortem`.
It waits for Enter, or a Go-file change with `-watch`, between iterations.
Outputs are separate; the checkpoint ring and metrics series are unchanged.

### Breakpoints

`acceptance run <case> -break stage=<name>|tick=<n>|minute=<m>
-headless=false` stops at the chosen boundary, bundles state and leaves the game
visible and paused. It prints `BREAK` and exits 3, not a pass.

Continue with `acceptance resume -root <root>`; discard with `acceptance stop`.
Breakpoints require checkpoint support and cannot combine with repeat,
postmortem-only or dev mode.

### Staging a slow Run body

Declare ordered `Stages` and wrap setup blocks in `s.Stage(ctx, name, fn)`.
Each block must stop its services before returning. The runner stores a bundle
and reuses the newest matching native package, start, expansions and stage key.

The stage key covers the area's Go files and stage names, not shared helpers or
the controller binary. Use `-restage` when those affect the precondition.
`-fresh` retains stages; `-restage` discards them. A pending resume takes
precedence. `RIMGOVERNOR_ACCEPT_STAGES=0` disables caching.

`acceptance suite -stages` schedules missing stages and their dependent tails
for iteration. The ordinary suite restages from scratch. Preserve
`staged_from` when reporting cached evidence; see
[result provenance](choose-tests.md#full-cached-and-resumed-runs).
