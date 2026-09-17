# Choose checks for a change

[Documentation](../../README.md) · [Development workflow](../development-process.md)

## Testing budget and evidence reuse

Follow the testing pyramid: many fast Go unit tests, fewer boundary integration
tests and a small set of targeted native acceptance harnesses verified against a
real headless RimWorld instance. Keep focused unit tests in the edit loop.
Before a slow check, identify the changed behavior or unresolved failure it
verifies and why cheaper checks cannot establish it.

Run native acceptance at relevant feature milestones, not after every edit.
After an acceptance failure, add a fast regression test where feasible and
rerun the affected harness; expand coverage only when the changed behavior or
failure justifies it. Do not duplicate tests or reviews already supported by
applicable evidence.

## Stage the precondition, do not play into it

A native acceptance harness should open on a colony that is already in the
state the assertion needs and then advance only the ticks the assertion itself
consumes. Do not start from a baseline/foothold save and let the colony grow,
research, build or starve its way into the precondition: a run that spends
20-30 minutes of game time getting ready is a fixture problem, and it makes
the harness too slow to rerun after a fix. Reach for, in order of preference:

- a prepared `.rws` save committed with the harness's fixture set, opened
  directly by the harness;
- a `test/*_prepare` op in the test fixture mod (see the existing
  `cleanliness_prepare`, `power_prepare`, `refrigeration_prepare`,
  `storage_haul_prepare`, `guarded_construction_prepare` families) that spawns
  the buildings, pawns, items and conditions the test needs in one call;
- `variantsavegen` / `ScenarioStartFixture` for a programmatic scenario start
  when the stressor is map- or start-level (seed, biome, season, scarcity).

Budget a targeted harness at minutes. If the precondition is the slow part,
build the fixture before writing the assertion, and review the generated save
once so later runs can trust it.

## Performance guidelines for a new harness

Every harness under `go/internal/nativeaccept/cmd/*` is written against
this checklist; the refactors behind #91 and #92 exist because earlier ones
were not. A reviewer holds a new harness to it.

1. **Open on the precondition.** A committed `.rws` or a single
   `test/*_prepare` call (previous section). The run's first assertion-bearing
   tick should come within a minute of the game being ready.
2. **Small map, tiny planet.** Take the default start (200x200, 5%
   planet); pass a larger `na.DebugStart` only when the assertion reasons
   about terrain beyond that, and never hardcode a map size in a fixture.
3. **Core-only unless the test is about DLC.** Do nothing and the profile is
   Core-only; a save-loading harness calls `cfg.UseSaveExpansions(save)` so a
   save's own `<modIds>` decide. Only a DLC-content test sets
   `Config.Expansions` (or `RIMGOVERNOR_ACCEPT_EXPANSIONS`).
4. **Quiet by default.** Start the debug colony with `na.StartDebugGame`
   and pick the mode deliberately: `QuietRequired` when the harness needs a
   fixture build anyway, `QuietIfAvailable` when it must also run on a
   production build, `Loud` only when the assertion is about an interruption.
   `test/configure_start` is quiet unless told otherwise.
5. **Every wait is stall-bounded.** Poll through `na.WaitProgress` with a
   signature over the thing that must move and `Terminal: service.Exited`
   when a serve subprocess is involved; use the shared `WaitReview`,
   `WaitPlan`, `WaitGoalMethod`, `WaitPlanTerminal` and `WaitRoutineReview`
   where they fit. No bare
   `for { ...; time.Sleep }` loops bounded only by the run timeout, and no
   per-phase ceilings measured in tens of minutes: a ceiling is the safety
   net, the stall budget (`-stall`, default `na.StallBudget()`) is what ends a
   broken run.
6. **Budget in minutes and say so.** `-timeout` defaults reflect a healthy
   run plus margin, not the worst run seen. If the honest default exceeds
   ~15 minutes, the precondition is not staged well enough (item 1) or the
   assertion covers too much; split it.
7. **Advance by ticks, at speed.** Run at `Fast` (or the clock's
   `tickDeadline`) and bound game-time waits by ticks where the assertion
   allows, so wall-clock is spent on the assertion, not on watching the game.
8. **One fixture call, not a script.** Spawn, forbid, damage, assign and
   settle in one `test/*_prepare` op rather than a sequence of production ops
   each paying a bridge round trip; production ops are for the behavior under
   test, not for setup.
9. **Fail fast on terminal signals.** A `ScenarioInterrupted` hold, a plan in
   `Unsuccessful`/`Cancelled`, a serve exit or a missing fixture op ends the
   run at once with the evidence in the report; do not wait out the ceiling
   hoping it recovers.
10. **Prefer reuse over boot.** When several cases share a save, run them
   through `sustainedmatrixaccept -reuse-game` ([below](#reusing-one-game-across-acceptance-cases)) rather
   than booting RimWorld per case.

## Keep the game quiet and small

Acceptance profiles are Core-only: `nativeaccept.PrepareNativeModConfig`
drops every `ludeon.rimworld.*` expansion from the headless/rendered
`ModsConfig.xml` it generates, because each active expansion adds def loading
and per-tick systems no harness needs unless it tests that DLC. A harness that
does sets `Config.Expansions` (or the run sets
`RIMGOVERNOR_ACCEPT_EXPANSIONS=royalty,biotech`). A save refuses to load
(`save.missing_mods`) under a profile missing an expansion it was recorded
with, so a harness that loads a save calls `cfg.UseSaveExpansions(save)`
before `PrepareConfig`, which activates exactly the expansions in that
save's `<modIds>` header; regenerate saves Core-only (`variantsavegen` now
does) rather than carrying DLC forward.

Fixture games are also quiet by default: `test/configure_start` applies
`test/quiet_storyteller` once the colony exists (pass `quiet=false` to keep
the ordinary storyteller), and harnesses start their debug colony through
`na.StartDebugGame(ctx, h, names, mode)`, which applies the same op per
mode: `QuietRequired` for fixture-dependent harnesses (a missing op is a
stale-mod error; every fixture build carries it), `QuietIfAvailable` for
harnesses that also run against a production build, and `Loud` for
interruption harnesses. Quiet means a Custom difficulty at
zero threat scale with no big/intro threats, violent quests or humanlike
hunting, no queued incidents, no storyteller ticks, and every non-colony pawn
removed from the map; because the Custom difficulty is what the save
persists, a quiet save stays quiet after reload while a fixture build is
installed. Interruption harnesses (`combataccept`, `defenselayoutaccept`,
`movementaccept`, `disconnectaccept`, `test/world_incident` users) stay
`Loud`.

Starts are small by default. `test/configure_debug_start` (in every fixture
build) arms the next quick start with a map size and planet coverage, and
`na.StartDebugGame` uses it whenever it is discoverable: 200x200 on a 5%
planet (`na.DefaultDebugStart`; `RIMGOVERNOR_ACCEPT_MAP_SIZE` and
`RIMGOVERNOR_ACCEPT_PLANET_COVERAGE` override a run), which took the quick
start from 11.5s to 4s. `test/configure_start` takes the same `mapSize` and
`planetCoverage` parameters, `variantsavegen` exposes them as `-map-size` /
`-planet-coverage` and manifest fields (a variant that picks a biome or a
temperature band defaults to a 30% planet, since a 5% planet has no
guaranteed tundra or extreme-desert tile). A harness that reasons about
surrounding terrain calls `na.StartDebugGameSized` with what it needs (150
is the floor, 400 the ceiling); fixtures read `map.Size` rather than
assuming 250.

Bound waits by stall, not only by ceiling. A broken run stops changing long
before its wall-clock budget runs out, so a poll loop goes through
`na.WaitProgress(ctx, na.Wait{Ceiling, Stall, Terminal}, probe)`: the probe
returns a progress signature (`na.Signature(...)` over whatever must move:
plan stages, a goal binding, a method count) and the wait fails once it has
not changed for the stall budget. Leave the game tick out of the signature
unless the wait tolerates a plan that is not moving while the game runs.
`Terminal` fails fast on a signal that nothing can recover from, typically
the serve subprocess having exited (`service.Exited`). The shared
`WaitReview` (a latch or binding on the routine review), `WaitPlan` (a
plan's stages, with `PlanSignature`), `WaitGoalMethod`, `WaitPlanTerminal`
and `WaitRoutineReview` already do this
with `na.StallBudget()` (10 minutes, `RIMGOVERNOR_ACCEPT_STALL` overrides);
harnesses with their own loops take a `-stall` flag defaulting to the same.
Issues #91 and #92 track the remaining speed and quiet work (pre-generated worlds,
process reuse, frozen needs, letter acknowledgement).

Passing evidence follows relevant code, dependencies, inputs and environment,
not the main HEAD hash. Unrelated main commits, clean cherry-picks and rebases
do not invalidate it. Inspect the relevant diff and reuse applicable results
across agents; conflict resolution or dependency changes require only the
checks they affect. "The full affected suite" means the applicable automated
suite, not every gameplay scenario. Finish when agreed completion criteria and
relevant checks pass; put unrelated discoveries in the backlog.

## Reusing one game across acceptance cases

Native acceptance binaries are fresh-process by default: one `games_start`,
the cases, one `games_stop`. That launch is the expensive part (tens of
seconds against a few seconds to reload a paused save), so a binary that
runs several cases in a loop may opt into `nativeaccept.GameReuse`
([reuse.go](../../../go/internal/nativeaccept/reuse.go)): RimWorld is
launched once and each case begins with a reload of its save into the same
process. `sustainedmatrixaccept -reuse-game -saves a,b,c` is the first
consumer; [reuseaccept](../../../go/internal/nativeaccept/cmd/reuseaccept/main.go)
is the acceptance for the lifecycle itself.

Reuse is only valid because every reload is checked against a reset
contract before the case starts (`CheckReset`): a load token never issued
before, the game paused at the baseline tick, no active authority, no owned
draft claim, and the sampled resource census equal to the first load's. The
colony id is not compared -- a fixture save the mod never wrote has no
persisted id, so native mints one per load; for the same reason reloads go
through `rimworld/load_game_ready`, not `lifecycle_load`. A case
that fails, or that ends with authority or a draft still held, retires the
game (`games_stop`) rather than handing it on. Each case gets its own output
directory and, when it launches `rimgovernor serve`, its own SQLite state.

Reuse does **not** reset mod static state: process-scoped statics such as
`OrderedWorkHistory`, `PlayerFrame`, the `Supervisor` journal and
`PawnConfigTool`'s letter maps survive a reload (see
[native-static-state.md](../../../contracts/native-static-state.md)). Any
case whose assertion depends on one of those, and any case run as static-
state or fresh-Go-session evidence, stays in fresh-process mode. Manifest
variants (`-manifest`) are still generated in their own fresh process.

## Available checks

| What changed / what you need to establish | Available support | Requirements and limits |
| --- | --- | --- |
| Go controller logic, contracts, persistence | From `go/`: `go test ./...`, `go vet ./...`, `go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor` (also run together, with staticcheck and the test-time budget, by `task go:build && task go:test` from the [root Taskfile](../../../Taskfile.yml)) | Pin Go via [go/.go-version](../../../go/.go-version); `CGO_ENABLED=0`. Linux race tests need CGO/GCC. Native control and fresh Go-session recovery have separate behavioral checks below. See [go/README.md](../../../go/README.md). |
| Dashboard behavior and build | `task dashboard:build` runs `pnpm run typecheck`, `pnpm run lint` and `pnpm run build`; `task dashboard:test` runs `pnpm test` (Vitest) | Local pnpm and dashboard dependencies; native UI acceptance is separate. |
| Shared Protobuf contracts | Official C#/Go generation `--check` for both languages | [Generation commands](../../../contracts/schema-generation.md); native adapters additionally need gameplay acceptance. |
| Completed pawn work, recovery or another live-game invariant | A targeted `go/internal/nativeaccept/cmd/*accept` harness (`bedassignaccept`, `billsaccept`, `caravancontrolaccept`, `caravandepartureaccept`, `combataccept`, `constructionaccept`, `developmentaccept`, `draftaccept`, `excavationaccept`, `facilityaccept`, `husbandryaccept`, `movementaccept`, `questacceptaccept`, `questfulfillaccept`, `recoveryareaaccept`, `recoveryserviceaccept`, `researchaccept`, `roomsaccept`, `settlementgiftaccept`, `sustainedfoodaccept`, `tradeaccept`, `upkeepaccept`, `wallremovalaccept`, `wallupgradeaccept`, `zoneaccept` and others — see the current set under `go/internal/nativeaccept/cmd/`) | Disposable prepared colony, matching native DLLs, GABS and a real headless RimWorld instance. Never replace installed DLLs while any RimWorld instance is running, including another worktree's tests. Isolated tests must restore temporarily swapped DLLs. Never kill `RimWorldWin64.exe`/`gabs.exe` by image name — that ends every concurrent worktree's game (seen there as GABS's catalog emptying, `availableTotal: 0`); stop your own via `games_stop` or kill only pids whose command line contains your `-root`. A receipt alone does not prove pawn work completed — verify the observed postcondition. |
| Native colony/routine read parity against a retained capture | `go test ./internal/observation -run <TestName> -v` with the matching `RIMGOVERNOR_NATIVE_*_CAPTURE`/`RIMGOVERNOR_NATIVE_*_REFERENCE` environment variable (colony, food forecast, production, naming, power methods, temperature methods, mood — see [go/README.md](../../../go/README.md) for the exact test name and variable per family) | Retained native capture files; verifies decoding/replay parity, not a live game session. |
| Where bridge call time goes (gate wait, GABS round trip, native main-thread queue wait, native tool execution, receipt decode, ProtoJSON decode) and wall TPS over a session | Run `serve` with `--flight-recorder <abs path>`, then `rimgovernor phases [--json] <abs path>` from `go/` (`go run ./cmd/rimgovernor phases ...`). Rows carry per-call `timing` phases and `native_decode` rows; the sampler aggregates per native tool, with describe (`games_tool_detail`) round trips listed separately (paid once per method per GABS session, not per call); the `cached` column counts reads the scheduler's per-step read cache served without a round trip (`native_cache_hit` rows). Each `ClockScheduler.Step` also publishes a `clock_step` row tallying the round trips it still issued by tool, which the report shows as reads/step (mean, max, per tool); `RIMGOVERNOR_CLOCK_DEBUG=1` prints the same tally with the cache's hit/miss counts per step on stderr (`[clock-scheduler] step reads: ...`) | Read-only over the recorder's retained segments; no game or authority access. The companion reports its own split beside the payload (`{"payload": ..., "timing": {"queueMs", "executeMs"}}`) for every `rimgovernor/*` tool whose single main-thread hop goes through `ProtoBoundary.OnMainThread`; the `queue ms`/`exec ms` columns are means over the calls that carried it and read `-` (absent, not zero) for an older companion or a multi-hop media capture. Sub-millisecond phases can read 0 on Windows' coarse monotonic clock. Wall TPS comes from reply ticks and includes paused time; tick decreases (load, rewind) are excluded as resets. |
| Docker packaging of the Go controller | `docker build -f containers/Dockerfile --target go-controller`, `docker run --rm rimgovernor-go:local version`/`help` | Confirms the image builds and the binary starts without a licensed game; not gameplay evidence. See [go/README.md](../../../go/README.md#go-launch-and-packaging-the-production-default-g0111-g0112). |

For agents: inspect the affected tests and choose the smallest relevant check, then
run the full affected suite once before handoff. Go-only changes need the full Go
suite; dashboard-only changes need typecheck, Vitest and build. Changes to shared
Protobuf contracts need both plus generation checks. Reuse a successful run when
relevant code, dependencies, inputs and environment are unchanged, even if main has
advanced; do not repeat full suites after documentation-only follow-ups. Report
commands, exit status, skips, artifact locations and what remains unverified. Keep
failed trials. A documentation-only edit normally needs command/flag and link
verification, not a new game session. Do not mark acceptance work complete from
unit tests, compilation or native receipts alone — verify the observed outcome.

Use an isolated task worktree when peers may be active. A worktree does not inherit
the main checkout's build artifacts; run `go test`/`go build` and `pnpm install`
there for local checks.

Native acceptance tooling is Go-only; see
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38) for coverage
gaps, and `go/internal/nativeaccept/cmd/` for the actual current set of
harnesses (issue #38's own description can lag newly landed families — trust the
directory listing over the issue body when they disagree).
