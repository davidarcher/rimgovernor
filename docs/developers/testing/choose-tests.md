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

## Keep the game quiet and small

Acceptance profiles are Core-only: `nativeaccept.PrepareNativeModConfig`
drops every `ludeon.rimworld.*` expansion from the headless/rendered
`ModsConfig.xml` it generates, because each active expansion adds def loading
and per-tick systems no harness needs unless it tests that DLC. A harness that
does sets `Config.Expansions` (or the run sets
`RIMGOVERNOR_ACCEPT_EXPANSIONS=royalty,biotech`); a committed `.rws` saved
with an expansion active needs the same opt-in or a Core-only regeneration.

Fixture games are also quiet by default: `test/configure_start` applies
`test/quiet_storyteller` once the colony exists (pass `quiet=false` to keep
the ordinary storyteller), and any harness that loads a save or a debug game
can call `test/quiet_storyteller` itself. Quiet means a Custom difficulty at
zero threat scale with no big/intro threats, violent quests or humanlike
hunting, no queued incidents, no storyteller ticks, and every non-colony pawn
removed from the map; because the Custom difficulty is what the save
persists, a quiet save stays quiet after reload while a fixture build is
installed. Interruption harnesses (combat, disconnect, `test/world_incident`
users) must not quiet the game. Issues #91 and #92 track the remaining speed
and quiet work (small maps, stall-based early exit, frozen needs, letter
acknowledgement).

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
| Go controller logic, contracts, persistence | From `go/`: `go test ./...`, `go vet ./...`, `go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor` (also run together by [build.ps1](../../../build.ps1)) | Pin Go via [go/.go-version](../../../go/.go-version); `CGO_ENABLED=0`. Linux race tests need CGO/GCC. Native control and fresh Go-session recovery have separate behavioral checks below. See [go/README.md](../../../go/README.md). |
| Dashboard behavior and build | `build.ps1` runs `pnpm run typecheck`, `pnpm run test` (Vitest) and `pnpm run build` from `dashboard/` | Local pnpm and dashboard dependencies; native UI acceptance is separate. |
| Shared Protobuf contracts | Official C#/Go generation `--check` for both languages | [Generation commands](../../../contracts/schema-generation.md); native adapters additionally need gameplay acceptance. |
| Completed pawn work, recovery or another live-game invariant | A targeted `go/internal/nativeaccept/cmd/*accept` harness (`bedassignaccept`, `billsaccept`, `caravancontrolaccept`, `caravandepartureaccept`, `combataccept`, `constructionaccept`, `developmentaccept`, `draftaccept`, `excavationaccept`, `facilityaccept`, `husbandryaccept`, `movementaccept`, `questacceptaccept`, `questfulfillaccept`, `recoveryareaaccept`, `recoveryserviceaccept`, `researchaccept`, `roomsaccept`, `settlementgiftaccept`, `sustainedfoodaccept`, `tradeaccept`, `wallremovalaccept`, `wallupgradeaccept`, `zoneaccept` and others — see the current set under `go/internal/nativeaccept/cmd/`) | Disposable prepared colony, matching native DLLs, GABS and a real headless RimWorld instance. Never replace installed DLLs while any RimWorld instance is running, including another worktree's tests. Isolated tests must restore temporarily swapped DLLs. Never kill `RimWorldWin64.exe`/`gabs.exe` by image name — that ends every concurrent worktree's game (seen there as GABS's catalog emptying, `availableTotal: 0`); stop your own via `games_stop` or kill only pids whose command line contains your `-root`. A receipt alone does not prove pawn work completed — verify the observed postcondition. |
| Native colony/routine read parity against a retained capture | `go test ./internal/observation -run <TestName> -v` with the matching `RIMGOVERNOR_NATIVE_*_CAPTURE`/`RIMGOVERNOR_NATIVE_*_REFERENCE` environment variable (colony, food forecast, production, naming, power methods, temperature methods, mood — see [go/README.md](../../../go/README.md) for the exact test name and variable per family) | Retained native capture files; verifies decoding/replay parity, not a live game session. |
| Where bridge call time goes (gate wait, GABS round trip, native main-thread queue wait, native tool execution, receipt decode, ProtoJSON decode) and wall TPS over a session | Run `serve` with `--flight-recorder <abs path>`, then `rimgovernor phases [--json] <abs path>` from `go/` (`go run ./cmd/rimgovernor phases ...`). Rows carry per-call `timing` phases and `native_decode` rows; the sampler aggregates per native tool, with describe (`games_tool_detail`) round trips listed separately | Read-only over the recorder's retained segments; no game or authority access. The companion reports its own split beside the payload (`{"payload": ..., "timing": {"queueMs", "executeMs"}}`) for every `rimgovernor/*` tool whose single main-thread hop goes through `ProtoBoundary.OnMainThread`; the `queue ms`/`exec ms` columns are means over the calls that carried it and read `-` (absent, not zero) for an older companion or a multi-hop media capture. Sub-millisecond phases can read 0 on Windows' coarse monotonic clock. Wall TPS comes from reply ticks and includes paused time; tick decreases (load, rewind) are excluded as resets. |
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
