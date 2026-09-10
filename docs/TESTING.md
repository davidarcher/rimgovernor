# Development and gameplay verification

Use [README.md](../README.md) for initial setup and launch. Run commands below from
the repository root. All unfinished validation belongs in [BACKLOG.md](BACKLOG.md).

## Choose the test scope

| What changed / what you need to establish | Available support | Requirements and limits |
| --- | --- | --- |
| Controller logic, contracts, persistence | `controller_tests/`; focused pytest or full `build.ps1` | Local Python environment; fixtures do not establish native outcomes. |
| Dashboard behavior and build | `build.ps1` runs typecheck, Vitest and Vite build | Local Python and dashboard dependencies from setup; native UI acceptance is separate. |
| Generated observation DTO matches its schema | `scripts/generate_bridge_observation.py --check` | Local Python environment; run explicitly, outside `build.ps1`. |
| Linux regression checks or independent copies of the suite | [Docker controller checks](#docker-controller-checks-no-game-required) | Host Python 3.12+ and Linux Docker; no game, mods, GABS, LM Studio, local venv or host Node required. Windows-specific tests skip. |
| Native Linux startup, isolation, clock, shutdown and checkpoint retention | [Automated native Docker acceptance](#automated-native-docker-acceptance) | Docker Compose and staged licensed Linux game/mod/profile/GABS inputs; no model inference is exercised. |
| Rendered native container snapshots | Native Docker runner with `--display xvfb` | Same native inputs; private Xvfb/llvmpipe, no host desktop focus. Inspect retained frames. |
| Completed pawn work, recovery or gameplay invariants | Focused native probes below and [headless testing](#headless-testing) | Disposable prepared colony, matching native DLLs and probe-specific prerequisites; read assertions and `--help`. Some probes still require Windows. |
| Actual language interpretation or sustained colony behavior | [Real model probe](#real-model-probe), [campaigns and performance](#campaigns-and-performance) | Configured local LM Studio when inference is involved; bounded lifecycle checks do not establish these outcomes. |

For agents: inspect the affected tests and choose the smallest relevant check,
then run the required broader checks for the change. Report commands, exit status,
skips, artifact locations and what remains unverified. Keep failed trials. A
documentation-only edit normally needs command/flag and link verification, not a
new game session. Do not mark backlog gameplay acceptance complete from fixture
tests, compilation or native receipts alone.

Use an isolated task worktree when peers may be active. A worktree does not inherit
the main checkout's `.venv` or `node_modules`; run setup there for local checks, or
use the Docker runner to build that worktree's source. Do not reuse another task's
mutable image tag or output directory.

## Local checks

```powershell
.venv\Scripts\python.exe scripts\generate_bridge_observation.py --check
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` runs Python tests, dashboard typechecking, Vitest and the Vite build.
It does not compile/install native DLLs or run model/gameplay tests. For a focused
Python check use `.venv\Scripts\python.exe -m pytest -q controller_tests/test_NAME.py`.
Generated dashboard assets, local databases and logs remain outside commits.

With RimWorld closed, build/install native changes using:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_observation_bridge.ps1 -Install
```

Never replace installed DLLs while any RimWorld instance is running. Native build
success is not gameplay acceptance. Preserve source provenance beside copied code.

Shared spatial controller checks are in `test_spatial_constraints.py`,
`test_plan_geometry.py` and `test_construction_preflight.py`. They cover entrances
in all rotations, indoor farm refusal, retained-building and same-batch native
footprint conflicts, unknown geometry, and refusal before dispatch. These use
native-shaped fixtures; B06 still requires ordinary pawn construction, live
player edits and observed access acceptance.

`test_shell_site.py` exercises complete native zone census validation, enclosed
farm refusal, allowed indoor stockpiles, immediate doorway access in all rotations,
unknown/truncated geometry and direction changes. A serialized partial-dispatch
fixture adds a farm between batches and verifies no further shell orders while
retaining the first receipt. Real zone edits and pawn route behavior remain B06
gameplay acceptance; fixtures are not game observations.

`test_shell_connectivity.py` checks four-neighbor interior and local exterior
connectivity, projected neighboring shells, clipped map edges, large-room query
caps, unknown cells and interrupted batch persistence. The bounded exterior margin
does not certify a route to a pawn or unrestricted map-wide connectivity.

Run `scripts/spatial_site_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` with `controller` on `PYTHONPATH` for a paused isolated native
spatial probe. It checks shell preflight, an allowed interior stockpile, and shared
admission refusal after a real interior farm is designated. Complete censuses,
unchanged plan/orders/native tick, zero inference and per-case timings are retained
with the input manifest. This is admission acceptance, not ordinary construction
or route traversal. Keep the installed DLL set fixed throughout the owned game;
the probe never replaces DLLs and stops its isolated session on completion/failure.

`test_project_resource_scheduling.py` covers resource competition in ready order,
dependency gates, uncertain writes, persisted receipts and Hands restock recovery.
Admission still requires enough stock for all accepted commitments. B07 native
acceptance must observe real production consumption and construction progress;
the fixture suite does not establish pawn work or save-rewind recovery.
`test_goal_watchdog_recovery.py` replays delayed tracked completion after a durable
timeout hold, including shell-to-furnishing continuation, dependent chains and
refusal under cancellation, Manual, rewind, failure or a different blocker. Native
delayed labor and paired restart acceptance remain separate B07 checks.

`test_zone_project_postconditions.py` and `test_building_facing_postconditions.py`
cover persisted exact zone and orientation expectations, native-shaped edit
invalidation of dependent work, incomplete-read recovery and legacy records. These
controller fixtures do not establish actual in-game zone or rotation editing.

## Native execution windows

`scripts/native_player_input_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` runs a visible isolated game. After each `ready.json` update,
send the requested Space or number-row 2 key through the actual window input path.
The probe requires native external-pause/speed attribution, a persistent Manual
hold, rejection of a stale controller write, unchanged paused pawn state, and
successful explicit resume to an exact tick boundary. API time-speed calls are
used only for setup and explicit resume, never as the input under test.

For deterministic letter ordering, build `scripts/fixtures/InterruptionFixtures.csproj`
and, with every game stopped, temporarily place its output DLL under the installed
observation mod's `BridgeTools/InterruptionFixtures/` directory. This separate
test assembly calls the real `LetterStack.ReceiveLetter`; it does not change
production code, pawns or saves. Run `scripts/native_interruption_acceptance.py`
with the same source-root/output arguments. It checks exact letter ID attribution,
same-frame non-letter pause/speed precedence, non-pausing threat preemption,
nonstopping announcement delivery, and stale dispatch rejection after native load.
The profile must use the ordinary `MajorThreat` automatic-pause preference.
Remove the test DLL after all owned games stop, and preserve its hash in evidence.
For explicit join-scenario setup, the same test assembly provides
`test/join_incident`: preview eligibility with `dryRun=true`, then use
`dryRun=false` to request the ordinary native WandererJoin event. Record returned
before/after IDs, actual joined-pawn work settings and outcomes separately from
the incident receipt. This setup tool is unavailable to model execution.

For actual injury preemption, run `scripts/native_combat_smoke.py
--require-interruption --source-root <prepared-root> --output <fresh-directory>`.
An ordinary attack on existing wildlife must cause a native colonist health stop,
deliver controller evidence and reject the old attack revision. Target injury
alone cannot pass this variant. The disposable test may acknowledge one observed
Ancient danger warning; production does not automatically acknowledge it.
The isolated game is stopped in cleanup and the report records termination.
The combat probe also accepts `--staged-root /worker/run` after `container_worker`
has staged a fresh private Linux game. It discovers GABS from that root's config.
Mount the task source and its Git metadata read-only with Linux `GIT_DIR`,
`GIT_COMMON_DIR` and `GIT_WORK_TREE` paths for source provenance. Do not combine
the staged-root option with source/output or patient-save. A native injury-stop
probe does not establish autonomous tactics or low-health rearming acceptance.

`test_medical_triage.py`, `test_medical_recovery.py` and
`test_execution_windows.py` cover patient urgency, native preview fallback,
non-preemption of existing tending, direction-bound recovery and repeated
low-health combat clock refusal. These are controller fixtures; actual medical
outcomes and external pawn-order interruption remain native acceptance work.

Run `scripts/native_tick_budget_acceptance.py --source-root <prepared-root>
--output <new-directory>` with `controller` on `PYTHONPATH`. Each speed/budget
case reloads the unchanged baseline. The native companion must independently
pause at 1, 37 and 600 ticks at Normal, Fast and Superfast, retain its deadline
across renewal, and remain paused afterward. The probe also checks external native
speed/pause commands, lease expiry before the deadline and retirement on load
changes. Add `--rendered` for a visible game. Native clock commands reproduce
time-state transitions; they do not certify physical keyboard input, autosaves
or every danger race. Keep all failed reports.

`scripts/native_autosave_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` tests an ordinary one-day autosave during a 61,000-tick
Superfast window. Install both the current observation bridge and headless build
with every game stopped; `build_headless.ps1` accepts an optional `-DotNet` path.
The isolated profile enables the ordinary pause-on-load preference. The test
requires long-event/clear events, an unchanged exact tick deadline, a newly
written native save whose saved tick matches the event, and a paused reload with
the same colony and a new load token. It records observation, identity and headless DLL hashes and
does not edit save XML or pawn state. Other autosave intervals need an appropriate
`--ticks` value. Restore the original installed DLLs after all tests stop.
Add `--mixed` to retain one issued construction placement, its unissued material
reservations, a pending growing zone and a pending work setting through the save
boundary and reload. `--compact-construction` uses two wall placements on scarce-stock
seeds. Ordinary starting supply pods receive 600 ticks to land before setup.
The probe requires unchanged controller receipts, progress, reservations and native
action count, then rejects a stale write after reload. Run
`scripts/mixed_autosave_audit.py --reports <first-result.json> <second-result.json>
--output <audit.json>` to require two distinct colonies, immutable boundary evidence,
unchanged deadlines, exact tick completion and verified process cleanup. Physical
keyboard input is accepted separately.

## Retained cancelled action acceptance

Run `scripts/cancelled_action_acceptance.py --checkpoint <checkpoint.json>
--output <new-directory> --port 8788` with the controller on `PYTHONPATH`.
The probe verifies the paired checkpoint hashes, copies its unchanged native save
into a disposable visible profile, and starts with an empty controller plan.
Watch its dashboard on the selected port. It issues a room through the semantic
player command path, cancels its goal while blueprints remain, and verifies an
unrelated research order completes without changing cancelled receipts or native
orders. It retains `result.json` and stops its owned game/server.
Add `--restart` to save the cancelled room and controller database as a verified
pair, stop the owned game, and resume into a new database before the research
command. This checks the complete plan and conversation, a new load token, the
saved tick (allowing one loading tick), unchanged native blueprint identities,
empty pending manual requests and no reclaimed draft ownership. Repeating the
room intent must return its cancelled state without issuing another order.
The dashboard briefly disconnects during this disposable restart.
This is zero-inference command/executor acceptance; it does not test language
interpretation, completed construction, native blueprint cancellation, or save
rewind. Unit tests separately cover serialized plan restoration.

## Hunting candidate screening

`scripts/hunting_screen_probe.py --source-root <prepared-root> --output <new-directory>
--port 8788` starts a disposable visible colony and samples native wildlife for the
deterministic hunting screen. It retains observations, candidates and predator
rejections in `result.json`, with no hunting orders or model calls. This checks
native observation compatibility; boundary/unknown-data rejection and compiler
integration are tested separately. It does not prove reachability or successful hunting.
Add `--dispatch` to issue one designation through the deterministic compiler and
shared Hands under a scripted PLAYER goal in Manual. It verifies exact-prey native
readback and one write, then stops the disposable game. Rejection races are covered
by runtime tests; this fixture does not certify predator movement during live hunting.

## Real model probe

Launch a fresh disposable colony with an empty plan and leave it in Manual:

```powershell
.venv\Scripts\python.exe scripts\live_planner_probe.py --seconds 180
```

This enables automation and real model orders, records results in
`.rimbot/live-planner-probe.json`, then returns the same session to Manual. It does
not reload a save or reset an existing plan. Compare from the same fixture and
fixed revision/model settings. `orders_observed` proves neither completed shelter
nor survival.

## Headless testing

Close controller/game before installing and launching the isolated headless profile:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_headless.ps1 -Install
powershell -ExecutionPolicy Bypass -File launch.ps1 -Headless -NoBrowser
```

This derives `.rimbot/bridge/headless-profile`, enables HeadlessRimPatch and uses
`-batchmode -nographics`. Native fade readiness still matters. The dashboard stays
available without images; restart without `-Headless` for interactive rendering.

For disposable scripted acceptance, close the interactive controller/game first.
Check each script's `--help` and fixture requirements before running it:

| Area | Scripts under `scripts/` |
| --- | --- |
| Identity and clock | `native_migration_smoke.py`, `native_clock_smoke.py`, `native_stand_down_smoke.py` |
| Construction and installation | `native_strategy_smoke.py --headless --room`, `install_smoke.py`, `test_install.ps1` |
| Combat and medical outcomes | `native_combat_smoke.py --tend`, `native_ranged_smoke.py`, `native_rescue_smoke.py` |
| Native domains and UI | `native_trade_smoke.py`, `native_research_smoke.py`, `native_world_smoke.py`, `native_letters_smoke.py`, `dialog_smoke.py`, `dialog_text_smoke.py`, `companion_inspection_smoke.py` |
| Models and rendering | `execution_schema_smoke.py`, `native_scout_smoke.py`, `native_visual_smoke.py`, `native_render_smoke.py` |

For confirmed treatment interruption and recovery, use a fresh isolated worker:

```powershell
python scripts/native_combat_smoke.py --recovery --source-root <prepared-root> --output <new-worker-root>
```

The fixture requires suitable nearby wildlife and an ordinary combat wound. It
interrupts a confirmed tend job through owned stand-down while paused, verifies
the interruption, recovers the same action with archived receipts, observes native
treatment completion and checks owned-draft cleanup. It does not inject damage or
heal pawns. `--patient-save <native-save>` can copy an existing wounded-patient save
unchanged into the new worker. A fixture with no patient is a failed prerequisite,
not a recovery pass. Preserve all reports. Recovery exhaustion, stale reads, save
rewinds and player overrides also have deterministic replay coverage.

Read assertions before interpreting results: for example, a healthy-pawn rescue
refusal does not validate carrying a patient to bed. Some scripts use real models,
some scripted decisions, and some only inspect/refuse actions. Reports stay local
under `.rimbot/`; retain failures as well as successful runs.

For targeted real-model execution, run
`.venv\Scripts\python.exe scripts\execution_acceptance_smoke.py`, optionally with
`--case supplies`, `--case work` or `--case bill`, plus `--model` and a fresh
`--output` directory. Each case uses an isolated headless baseline and verifies
native readback through the normal commitment/Hands path. The accepted scope is
one selected supply stack allowed, work enabled in checkbox mode and one bill
created on the exact bench. It does not certify numbered priority scheduling or
completed production. All three cases passed with Qwen 3.5 9B; local evidence is
under `.rimbot/execution-acceptance-1788898095948598300/`.

For numbered priorities and actual cooking, run:

```powershell
.venv\Scripts\python.exe scripts\production_acceptance.py --source-root .rimbot/bridge --output .rimbot/production-new
```

This isolated fixture enables numbered work priorities, replaces one starting food
stack with rice, and uses ordinary pawn labor to build a campfire. Qwen commits
Cooking priority 1 and a two-repeat meal bill through the normal Hands path. Fresh
readbacks must show effective/stored priority 1, rice carried during DoBill, fewer
ingredients, produced meals and the exact bill counter changing from 2 to 0.
All rejected model proposals are retained. This accepts controlled worker scheduling
and production, not autonomous food strategy or sustained survival. Ingredient
whitelist selection is a separate player-action acceptance case.

For Windows runtime-file recovery, run the following with the controller environment:

```powershell
.venv\Scripts\python.exe scripts\runtime_file_acceptance.py --source .rimbot/bridge --output .rimbot/runtime-file-new
```

The test owns an isolated game and uses real Windows file handles
to block private runtime-state publication and reads beyond the retry budget.
It verifies unchanged receipts and native object identities after handle release,
without replaying orders. Choose a fresh evidence directory for every run.

For naming and quest-choice checks, `scripts/modal_acceptance.py --source
.rimbot/bridge --output .rimbot/modal-new` requires a temporary `ModalFixture=true`
build in a rendered isolated game. For controlled sleeping, hauling, construction
interruption and removal, `scripts/campaign_metrics_acceptance.py --source
.rimbot/bridge --output .rimbot/metrics-new` requires `CampaignMetricsFixture=true`.
Both fixtures are excluded from production builds. Install and restore DLLs only
after an escalated CIM process check confirms every RimWorld instance is closed.
The harnesses stop their own games; preserve failures and use new output paths.

For selective native inspector checks, run `scripts/inspector_acceptance.py` with
`--source .rimbot/bridge --output .rimbot/inspectors-new`. Its optional `--fixture`
requires a temporary build with `-p:InspectorFixture=true` and populates thermal,
ingredient, power and storage cases. Keep all games closed while swapping DLLs,
restore the previous DLL afterward, and exclude fixture tools from the production
build. The inspector report tests data and scope contracts, not cooling capacity
or completed production.

## Campaigns and performance

For the production deterministic bootstrap, run:

```powershell
$env:PYTHONPATH='controller'
.venv\Scripts\python.exe scripts\deterministic_foothold.py --source-root .rimbot/bridge --output .rimbot/deterministic-new --seconds 1800 --speed Superfast
```

The default is a fresh isolated headless profile; add `--rendered` for a visible
game. The current baseline is the prepared eight-tribal save. No save edits or
inference are permitted. `--speed` selects ordinary native Normal/Fast/Superfast,
not boosted simulation. The harness runs production BridgeRuntime, controller,
validation and Hands, writes a manifest, incremental `progress.json` and final
`result.json`, and exits successfully only for all FOOTHOLD_STABLE predicates.
Results include goal/method/lifecycle evidence, attempted and completed model-call
counts and game ticks. `--checkpoint <native-save.rws>` copies an unmodified save
for targeted debugging; those reports are labelled `saved_checkpoint` and do not
count as fresh-colony acceptance.
A timeout, blocker or partial shelter is not a pass. Stop uses this profile's
PID-owned GABS launch; never terminate all processes by executable name.

Controller replays in `test_colony_controller.py` separately exercise deterministic
layout variants, hysteresis, priorities, cancellation and accounting. They model
labor explicitly and cannot substitute for native gameplay acceptance. Chat tests
cover typed direct orders, maintained goals, policies, follow-ups, provenance,
Manual dispatch and stale-direction rejection. Repeat native acceptance at a
fixed committed revision and restored baseline; iterative debugging runs do not
establish repeatability. Keep temporary binaries, saves and logs outside Git.

```powershell
.venv\Scripts\python.exe scripts\headless_iterations.py --iterations 20 --parallel 2 --output .rimbot/campaign-new
.venv\Scripts\python.exe scripts\parallel_headless_smoke.py --output .rimbot/parallel-new
```

Use fresh output directories, prepared baseline/mods and local LM Studio. Each
worker owns its controller, SQLite state, save/config profile, log and GABS runtime.
Installed game/mod files are shared read-only. Start with two workers; eight is a
configured maximum, not a throughput recommendation. Commit between batches so
workers import a fixed revision. Use `--consecutive 3` for the baseline acceptance
gate and `--direction` to record the same player objective before each run. Use
`--source-root` for a prepared baseline elsewhere. Every dispatched trial receives
an isolated profile. The runner stops after the requested consecutive passing
streak and retains already-running siblings' evidence. A changed revision, model,
or objective resets the streak; interrupted trials cannot contribute.
Workers also save `manifest.json` before startup. Multiple consecutive passes
require matching source-content, effective inference-setting, baseline/profile,
GABS, installed observation and colony identity DLLs, and (for no-graphics launches) installed headless
DLL fingerprints. Listed untracked code is included alongside tracked source so
new modules cannot silently escape the source hash. Model weights and remaining
mod binaries are outside that fingerprint and must be held fixed by the operator.

Regenerate disposable profiles to remove legacy executable-name cleanup fallback.
Generated profiles use DirectPath process ownership; missing or other launch modes
are rejected for headless workers.

For a visible model comparison, use `--rendered --fixed-window --seconds 300`
with `--parallel 1` and the same `--direction` for every model. Rendered workers
use a private normal profile without HeadlessRimPatch or batch/nographics flags.
Load one LM Studio model at a time; preserve load/context/offload settings and
separate startup failures from gameplay outcomes. Reserve GPU memory for rendering.

The short campaign window defaults to 120 seconds and extends after a first action
to allow another 120 seconds. `--fixed-window` disables that extension; holds and
model failures can still stop a trial early. Its narrow foothold check requires eight living
colonists, eight nearby completed beds/spots, a nearby nine-cell stockpile and
allowed starting pemmican. It does not certify shelter, sustained food or survival.
Any fixture-specific warning acknowledgment is test-only; other holds stop play.

Campaigns use the runtime's paused deliberation and bounded execution policy;
the runner does not force Superfast after a review. Preserve older speed settings
in historical reports rather than treating those runs as directly interchangeable.

Each worker freezes `thresholds.json` before startup and samples native building
and zone readbacks for completion. Reports separate retained intent, attempted slots,
accepted effects and current completed objects, including sleeping-place overshoot
and removal deltas. Truncated or unavailable readbacks cannot establish success.
Diagnostic telemetry separately counts retained repeated/rejected calls, model
context budgets, dispatch latency and interventions. First observed pawn progress
requires changed native position or carried item during a continuing work job.
Functional reports distinguish roofed sleeping geometry, observed bed use and
stockpile filter/grid configuration from unknown access and sustained food work.
Observation timestamps are sampling upper bounds, not exact completion times.

With controller/game closed, benchmark disposable simulation separately:

```powershell
.venv\Scripts\python.exe scripts\native_speed_benchmark.py --headless --seconds 3 --repeats 2
```

The benchmark reloads the baseline, uses native forced-speed support and restores
Paused with boost disabled. Boost is excluded from normal strategist gameplay.
Report interruptions, native tick rate and end-to-end throughput separately.
Headless simulation and parallel lifecycle isolation do not establish faster model
inference. Full episode comparisons must include model waits and useful outcomes.


### Interactive semantic commands

`scripts/semantic_command_benchmark.py --output <fresh-directory>` measures the
configured local model against fixed controller facts with no game writes.
It compares complete typed requests, including default reserves, and separates
schema failures, schema-valid semantic errors, extra calls and request failures.
Research labels may resolve only to their exact observed fixture definitions;
goal aliases use the shared goal resolver. Cases cover research, food targets,
policy reserves, cancellation and goal resumption. The explanation case checks
the fixture's steel amounts and absence of writes; this is a limited text check,
not a general measure of answer quality. A single passing run is not a reliability rate.
Add `--model <local-model-id> --repeats 3` to repeat the fixed cases, including
combined spending/reserve changes and multi-resource requests. Complete typed
request multisets must match; duplicates, omitted calls and unrequested fields
fail. The manifest preserves settings and a case/fact/tool fingerprint. Per-case
rates and Wilson intervals describe this fixed benchmark, not arbitrary commands.
`scripts/interactive_commands_probe.py --source-root <prepared-root> --output
<fresh-directory>` runs real chat requests in a private paused colony, then
verifies a work assignment, a persistent food target and a component policy.
The probe uses the normal shared validator and Hands. Preserve failed responses;
fixed-fact model correctness and native command acceptance are separate results.

Add `--extended --model qwen3.5-4b --rendered --port 8788` to watch the disposable
colony in its own dashboard while testing research selection, locked-project
refusal, food-goal cancellation, two autonomous reviews and explicit goal resumption.
Research candidates come from native available/locked catalogs. The probe requires
the requested current-project readback and completed PLAYER action; locked research
must preserve the selected project without adding an action. Cancelled food work
must remain suppressed across the two reviews, which must not invoke a model.
The basic checks also reject unrelated work changes and unrequested component
reserves. Extended checks explicitly set a component reserve, change spending
while retaining that reserve, and clear the reserve without changing spending.
All interactive orders start in Manual; the two cancellation checks
temporarily enable routine autonomous operation in this disposable colony only.
The probe stops its owned game/server afterward and retains results and failures.

Extended checks also combine a food target with steel reserves and component
spending, then change both resource policies while preserving reserves. They reject
unrequested advanced-component policies and extra native actions. Add `--restart`
without `--port` to save and stop the owned game, resume the paired controller/native
checkpoint, compare the complete plan and conversation, and exercise goal cancellation
and resumption afterward. A passed paused-session check does not prove pawn production.
The semantic benchmark includes native labels for related resources as distractors;
score exact resolved definitions rather than accepting extra resource changes.

For receipt retention, run `scripts/plan_retention_audit.py --evidence <native-result.json>
--output <fresh-directory> --steps 1000`. The source must contain completed plan
receipts. This synthetic workload uses their shape without executing native orders,
checks SQLite round trips, and records active/retired counts, snapshot/database size
and serialization time. It measures retention overhead, not native recovery.
The audit uses the production archive transaction, reloads the compact plan every
hundred actions and checks all live/archived receipts at the end. Use `--steps 10000`
to distinguish a bounded live working set from the growing immutable ledger.
Add `--lifecycle` to retain a continuously active goal's method evidence and
interleave meaningful/diagnostic events from two colonies. Samples include method
bytes, event bytes, recent-history timings and query plans. This is a synthetic
growth workload, not evidence that a native colony survived that many actions.

Run `scripts/history_query_audit.py --source <controller.sqlite> --output
<fresh-directory>` to measure an actual campaign database. It opens the source
read-only, backs it up, applies current schema/index migration to the copy, and
verifies the full event digest and exact recent history before/after migration.
Both diagnostic modes and an absent colony are checked. Add `--unindexed-baseline`
to remove only the copy's history indexes before the comparison. Source databases
and their receipts remain unchanged.

`scripts/goal_method_archive_audit.py --source <controller.sqlite> --output
<fresh-directory>` retires completed native action records only on a read-only
source's copy, archives eligible method associations, and verifies exact mappings,
deduplication, goal reopening and event preservation after SQLite backup. This
does not run the game. For a real paired save/stop/load check, add `--archive
--methods` to `scripts/session_checkpoint_acceptance.py`; it verifies the archived
method alongside its exact native hauling outcome, a new load token, Manual mode
and zero replayed actions. Installed DLL replacement/restoration still requires
every game to be stopped.


### Ordinary Crashlanded acceptance

`scripts/shelter_handoff_acceptance.py --source-root <prepared-root> --output
<fresh-directory> --seconds 600` permits observed starting supplies through the
production compiler/Hands and requests a player shelter shell. Ordinary pawn
labor must complete it, after which the deterministic controller furnishes sleeping
places in that roofed native room without issuing an autonomous shell. The report
retains native rooms, player progress and zero-inference counters. This is bounded
sleeping handoff acceptance, not food survival or complete adopted-room furnishing.
Add `--services` to request a different observed site while retaining the old
starter cache. Completion also requires a native campfire, active cooking bill
and nine food-storage cells inside the adopted room. The report preserves the
source/input manifest, blocked trials and zero-inference counters. This does not
certify sustained food replacement or heating/cooling under temperature extremes.

Prepare a fresh isolated native start, then run the production controller:

```powershell
python scripts/prepare_crashlanded.py --source-root <prepared-bridge-root> --output <fresh-bridge-root>
python scripts/deterministic_foothold.py --source-root <fresh-bridge-root> --output <fresh-report-directory> --seconds 1800 --speed Superfast
```

The preparer discovers `rimworld/start_debug_game_ready`. That native lifecycle
entry uses the ordinary Crashlanded scenario, Cassandra/Rough and normal world/
pawn generation. It waits for the three starting colonists to arrive, pauses and
uses native SaveGame; it does not alter pawn stats, needs, supplies or save XML.
Its report records the initial tick and save provenance. The inherited baseline
filename still says `tribal8`; the saved scenario and observed colonist count are
the authority. Generate another isolated root for another random map seed.

For a configurable starting population, build the companion with
`-p:ScenarioStartFixture=true` and temporarily install it while every game is
closed. Run `scripts/prepare_scenario.py --source-root <prepared-bridge-root>
--output <fresh-bridge-root> --scenario Crashlanded --count 10 --seed <world-seed>`.
The test-only `test/list_start_scenarios` discovers other native ScenarioDef names.
The fixture copies the chosen scenario, applies the native editor's 1..10 pawn
count and runs ordinary world/pawn generation. It leaves supplies and pawn stats
to that scenario. World seed alone does not promise identical pawn rolls or
starting tiles across preparations. The report records the installed fixture
hash, roster, unchanged global definitions and an unchanged native save hash.
It also verifies that setup refuses an existing colony. Failed trials are retained.
After preparation stops, rebuild without the fixture flag and install the
production companion before running gameplay acceptance on that saved baseline.
Restore the original installed DLL after testing. The fixture is excluded from
normal builds and the controller's gameplay capability surface.

Run `scripts/work_batch_audit.py --report <foothold-result.json> --output <audit.json>`
to verify a larger colony's work-setting batches. It requires more than eight
starting colonists and changed pawns, a full eight-action batch followed by another
batch, native per-setting readbacks, an observed work-coverage gate and no inference.
It does not certify pawn labor, a mid-campaign joining event or a sustained foothold;
the audit retains the source report hash and the campaign's separate outcome.

Run `scripts/shelter_capacity_audit.py --report <foothold-result.json> --output <audit.json>`
for a larger starter on fragmented soil. It requires completed native shell and
sleeping actions, enough observed indoor sleeping capacity for the starting
population, smaller accepted field patches and an observed production gate.
Its scope excludes actual bed use, crop harvest replacement and sustained survival.

The runner rejects fresh baselines past tick 600 and injects a model client that
fails on any attempted inference. By default it requires two consecutive game
days with all eleven gates verified and reports `SUSTAINED_FOOTHOLD`. Use
`--stability-days 0` for establishment-only `FOOTHOLD_STABLE`, or an explicit
number up to 30 for another duration. `--seconds` bounds the whole episode.

Add `--lifecycle-days 2` to measure a bounded native lifecycle campaign separately
from food-gate acceptance. The report samples real pawn `inBed`/`bedThingId`, exact
live action identities and receipts, goal evidence bytes, immutable archive/event
counts, SQLite/WAL/page growth and indexed recent-history latency. Native autosave
long-event, recovery and execution-boundary events retain the contemporaneous plan
and deadline. `LIFECYCLE_WINDOW` means the requested native duration completed with
the original colonists alive and no inference; it does not certify all food gates,
bed use by every pawn, joining events or arbitrary long-term survival. Evaluate
those readbacks explicitly against the relevant acceptance checklist.
With the separate interruption fixture installed, add `--join-count 3` to request
ordinary WandererJoin incidents after tick 50000. Each request first requires the
native incident worker's `CanFireNow`, then calls its usual `TryExecute`; failed
eligibility fails the probe instead of forcing pawn creation. The report retains
the original and joined roster IDs. This test-only scenario setup is excluded from
the gameplay gateway and does not edit pawn stats or saves.

Stability uses the native facts' tick, not wall time or a newer clock reading.
A recorded stability loss resets the window even when recovery occurs between
report samples. Unknown/failed gates and observation gaps over 6,000 ticks also
reset it. Save/load identity changes, rewinds and missing/dead starting colonists
fail the episode. Reports preserve losses, the longest qualifying interval and
maximum observation gap. This certifies sampled maintained gates over the stated
window, not arbitrary long-term survival or difficult-biome coverage.


### Visible dashboard acceptance

`setup.ps1` installs the `video` extra. For an existing Python environment run
`python -m pip install -e '.[video]'`. Build/install the observation companion
only with every RimWorld process stopped; older companions use snapshot fallback.
Run `scripts/video_stream_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` with `controller` on `PYTHONPATH` to receive native frames over
a real local WebRTC connection in a disposable paused colony. It checks decoded
dimensions, advancing frames, peer cleanup and unchanged paused native tick.
Restore any temporarily installed companion after the owned game stops. This
probe measures native-to-aiortc delivery, not Chrome capture-to-display latency,
hardware encoding, input safety or simulation throughput.

In Chrome, verify Live video, Pause video retaining the current frame, resume,
hidden-tab cleanup, load changes, multiple viewers and snapshot fallback after a
stream stall. Resize/fullscreen must retain the image aspect ratio. The 30–60 fps,
latency and CPU/GPU/TPS acceptance work remains in B18.

For browser lifecycle work without a game or installed-DLL changes, build the
dashboard and run `scripts/video_browser_fixture.py --port 8791` with `controller`
on `PYTHONPATH`, then open `http://127.0.0.1:8791/fixture`. Its explicit synthetic
controls stall/resume frames, change session identity and alternate landscape/
portrait resolution. Verify automatic reconnect, retained images, Pause video,
Expand/Exit fullscreen and session cleanup. The fixture never contacts GABS or
starts a game. Use `/api/video/status` to inspect bounded delivery counters;
hover the video badge for browser decode/jitter statistics. This is browser and
protocol acceptance only; retain native and Chrome performance work in B18.

Use a rendered prepared profile and the local web server for player-facing tests.
Verify that `/api/camera` supplies complete immutable PNG responses while native
captures advance; pause/play video must retain the last good frame. Check an actual
local-model request and verify its resulting goal or action in shared state.

On Autopilot, change a target or speed and confirm its effective persisted value.
Reject invalid threshold ordering and stale policy versions without altering the
plan. Keep unsaved drafts across background refreshes, including when chat changes
settings. Verification limits are read-only. The API requires the normal local
mutation header and current colony identity.

Stopping the controller may terminate its owned disposable game through the process
lifecycle. Preserve native saves before planned restarts; do not assume the game
survives killing the controller process. A player-facing session intentionally left
running needs its matching installed companion until that session is closed.

### Paired checkpoints and restart

`scripts/construction_refinement_acceptance.py --source-root <prepared-root>
--output <fresh-directory> --model <local-model-id>` checks policy refusal before
removal, real-model relocation with dependent shared execution, interrupted removal,
player preservation and paired restart. Failed trials retain their checkpoint.
`scripts/construction_resume_acceptance.py --source-report <failed-result.json>
--output <fresh-directory>` can finish the load checks from that immutable checkpoint
after an infrastructure startup failure. It deliberately queues the old cancellation
in the new load to test its context guard, then requires a fresh local-model request
and exact native removal delta. Neither probe certifies pawn construction or cooling.

`scripts/construction_recovery_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` uses ordinary forbid/unforbid designators to make a costed
two-wall commitment temporarily unaffordable. It requires an exact pre-write
resource failure, refusal while stock remains forbidden, same-action recovery after
native availability returns, and exactly two observed native blueprint identities.
The probe runs shared Hands and preserves the recovery history with no inference;
blueprint issuance does not certify subsequent pawn construction.
`scripts/supply_recovery_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` makes a starter-stock allow preview obsolete through an ordinary
allow command. It requires a known pre-write refusal, fresh native absence counts,
same-action completion and retained recovery history with zero Hands writes.
Buffered player pause/speed interruption guards are covered by controller tests.
`scripts/resource_consumption_acceptance.py --checkpoint <native-resource-checkpoint>
--output <fresh-directory>` copies an unchanged native save into a separate profile.
It admits an ordinary bill job, reads unfinished work from ordinary native saves,
then forbids a remote ingredient stack while verifying the same job remains active.
Completion must stop with nonpositive work remaining, unchanged unfinished
ingredients and no product. Ordinary unforbid must permit exactly one output while
the reserve prevents another cycle. Bill settings, filters and suspension remain
unchanged. The manifest records the actual imported controller source and separate
harness hash; no policy setter interrupts the job during the stock-change test.
Use `--material-identity` to verify that ordinary replacement of a separately
grounded Steel wall blueprint produces distinct Wood blueprint identities and
correct native material readbacks. Different materials must not return a false
`already_present` receipt. Native replacement is allowed; this is not a placement
refusal or construction-completion test.

Add `--recovery-fixture` to a lifecycle campaign to retain the native resource
recovery history through subsequent ordinary simulation. Measurements include
immutable archive hashes, live or archived recovery histories, native action
counts and the native clock state at autosave boundaries. The lifecycle audit
rejects changed or missing prior archive records and recovery history.
Joined-pawn work acceptance compares completed deterministic assignment deltas
with later native work-table readbacks for every joined pawn. These readbacks
verify applied settings; actual bed use separately requires native `inBed` and
`bedThingId` observations for every starting and joined colonist.
`scripts/native_bed_use_acceptance.py --source-root <ordinary-prepared-root>
--output <fresh-directory>` verifies additional-seed actual bed use. Routine
construction must supply indoor capacity for the entire starting roster. Once
native threats, tending needs and active mental states are clear, the fixture
enters Manual and applies an ordinary sleep timetable. Every starter must then
have a native bed identity and `inBed` observation. This bounded fixture certifies
bed use, not sustained survival or emergency handling.
Add `--archive-fixture` to start a lifecycle campaign with an ordinary completed
work-setting action and method in the durable archive. Native work readback must
verify the action before explicit retirement. The audit requires nonempty initial
archive hashes and their unchanged preservation throughout the campaign.
`scripts/hunting_archive_acceptance.py --source-root <ordinary-prepared-root>
--output <fresh-directory>` uses screened wildlife and shared Hands to verify an
actual Hunt designation. Explicit retirement must preserve the exact action,
method and target/signature in three nonempty archive tables through at least
6,000 native ticks. This verifies designation evidence retention, not killed prey
or completed pawn labor.

Add `--mixed` to `scripts/session_checkpoint_acceptance.py` for an issued shell
slot, its unissued material reservations, a pending growing zone and pending work
setting. The paired native restart must retain exact controller state and native
building IDs while staying in Manual with no replay or stale manual requests.
Add `--uncertain-zone` with `--mixed` to issue ordinary zone creation and deliberately
lose its successful receipt before Hands receives it. The checkpoint must preserve
the blocked action and unconfirmed issued slot alongside the exact native zone
identity, geometry and crop. Paired restart and delivery of the obsolete queued
request must retain that uncertainty in Manual with zero native replay. This
separately covers an issued non-idempotent operation; a wholly pending zone does not.
Add `--rewind` with `--mixed` to reload the unchanged older native baseline after
the paired restart. The probe requires later blueprint identities to disappear,
discards an explicitly delivered obsolete queued request, and rejects an old-load
native write without changing the observed building set or leaving Manual.

`scripts/freezer_expansion_acceptance.py --source-root <prepared-root>
--output <fresh-directory> --model <local-model-id>` builds two separate powered
cold-storage rooms with ordinary harvested materials, insulation and pawn labor. Actual local-model requests set
cooler temperatures, reserve steel and expand storage while preserving the first
freezer. Completion requires both roofed rooms below freezing with their native
stockpile cells, including three stable native windows after expansion.
Use `--resume-report <result.json>` instead of `--source-root` to resume an
immutable powered, supplied or working-freezer checkpoint after an infrastructure failure.
`scripts/adopted_room_thermal_acceptance.py --freezer-report <passed-result.json>
--output <fresh-directory> --variant hot|cold` then adopts an edited room, preserves
existing furniture, fills native sleeping capacity and verifies ordinary fueled
thermal furniture and temperature recovery. Each variant resumes the immutable
checkpoint independently, verifies exact native food filters and storage priority, and
records both freezers across three loaded native windows before editing. These probes require the named local model to be loaded;
retain failed reports and distinguish powered construction from actual cooling.
For an integrated controller source check, run
`scripts/adopted_room_resume_acceptance.py --thermal-report <passed-hot-or-cold-result>
--output <fresh-directory>`. It reloads the completed paired checkpoint, verifies
native room and fueled furniture state, refreshes adoption with the local model,
performs one legal furniture edit through current spatial admission and Hands,
and verifies that invalid adoption geometry preserves the current plan.


`scripts/cancel_construction_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` tests the native exact-target cancellation contract in a private
paused colony. It creates ordinary blueprint orders and a zero-work sleeping spot,
then verifies dry-run preservation, stale colony/map/load and metadata refusal,
completed-building refusal, exact blueprint removal and repeated-request refusal
without retargeting its neighbor. Add `--shared` to create a room through the
semantic command and shared Hands, prevalidate its cancellation, drop a successful
native removal receipt, and verify that a fresh request removes only the remaining
targets. Repetition after completion creates no native removals; unrelated pending
orders and completed buildings remain. Add `--frame` with `--shared` to assign
ordinary builders and observe a partly built wooden bed before cancellation.
The frame's observed held materials must return to spawned stock while the native
tick remains unchanged. The fixture rejects footprints overlapping existing orders.
Its explicit simulation driver waits for controller review pauses; an ancient-danger
warning can be acknowledged only after recording it and verifying no active hostile
or hunting-predator count. Other danger stops fail the probe. This does not establish
mixed restart acceptance. Add `--chat --model <local-model-id>` with `--shared`
to send the fresh cancellation request through actual player chat after the lost
receipt. The probe checks the resulting exact target set, native removal, preserved
unrelated orders and paused Manual mode. This single request is not a general
model reliability measurement. Install its companion and
restore the previous DLL only with every game stopped.

On a checkpoint-capable owned session, use Autopilot's **Save checkpoint and pause**
or run `scripts/restart_session.ps1 -Port 8787`. The restart command first saves and
verifies the native game and controller snapshot; unsupported older servers remain
running. A worktree can supply `-Python <venv-python.exe>`. Keep the existing game
DLLs installed until all sessions have closed.

For a retained checkpoint, run `python -m rimbot --resume <checkpoint.json> --port 8787`.
Close the previous owned process before manually resuming. This restores into a new
SQLite database and starts in Manual. Resume preserves the saved colony/map and
allows at most one native loading tick with pause-on-load enabled. Larger changes
fail closed. Do not edit or separate `game.rws`, `bridge.sqlite` and `checkpoint.json`.

Run `python scripts/session_checkpoint_acceptance.py --source-root <prepared-root>
--output <new-output>` for native save, process shutdown, restart and state comparison.
Add `--rendered` for the visible profile. The probe verifies a PLAYER food goal,
policy and conversation, unchanged native colony identity, a new load token and no
model calls. Checkpoint tampering, failed saves, stale direction and unresolved drafts
are also covered by focused tests.
Add `--archive` to execute and verify a native hauling-priority change through
Hands, retire its completed action, and check the exact archived receipt and
native assignment after paired restart. Reintroducing its old identity must be
rejected with no native action. This check uses no inference and does not certify
mixed pending construction or interrupted non-idempotent actions.

For an owned Windows legacy server without the checkpoint endpoint, first install
the current Python source in the checkout reported by `/api/health`. Keep native
DLLs unchanged. With that checkout's `controller` on `PYTHONPATH`, run:

```powershell
python scripts/migrate_legacy_session.py --port 8787 --root <existing-bridge-root> --database <existing-bridge.sqlite> --source <serving-checkout>
```

The command pauses routine operation and briefly suspends the old backend while
copying its database and handing GABS ownership to the migrator. A detached watchdog
resumes the backend if the migration process dies. This does not restore the old
GABS connection after takeover. The game is saved through the native tool, then
stopped and resumed using the paired checkpoint. Success requires unchanged shared
plan, settings and conversation, the same colony/map, and at most one loading tick.
Leave the colony in Manual until the player explicitly resumes it.

Artifacts and logs remain under `<root>/migrations/<id>`. Before native shutdown,
an interrupted handoff can be retried with the same arguments plus
`--recover-disconnected`; this bypasses the disconnected old control endpoint but
still requires its Manual/paused state, matching database, native load and tick.
After native shutdown, use the retained checkpoint's normal `--resume` command
instead. Verify the old server process has exited before starting a replacement
on its port. Do not enable automation or send chat through a disconnected legacy
dashboard. There is no checkpoint-only takeover that transparently returns ownership.


### Outpost dashboard and throughput

The Watch clock buttons request native time and enter Manual; Pause video affects
only the camera feed. Action follow uses native presentation on supported writes
and defaults off. Test these in a disposable rendered session; a read-only preview
or mocked API test does not establish game-level acceptance. Confirm unsaved chat,
project and policy drafts survive tab changes. Inspect Priorities and Work with
blocked, active and verified goals, and confirm raw IDs stay in diagnostics.

For a non-invasive throughput sample of an existing controller:

```powershell
.venv\Scripts\python.exe scripts/dashboard_throughput.py --port 8787 --seconds 120 --output .rimbot/throughput-sample
```

The output directory must be new. This sends only GET requests to cached dashboard
state; it does not change speed, enable rendering, dismiss interruptions or take
control. Wall TPS includes pauses. Paused time is a sampled approximation, and
stop-reason counts count samples rather than distinct incidents. It excludes
intervals across disconnects, rewinds and session changes. A paused colony yields
zero TPS; peak speed and safety require separate isolated gameplay acceptance.

## Docker workers (B17)

There are two runners: `container_checks.py` runs Python tests without a game;
`container_native_acceptance.py` starts two actual Linux games. Both build from
the checkout containing the script, pin the resulting image ID, and retain local
evidence. `docker compose up` alone starts a worker; it is not a test assertion.

### Docker controller checks (no game required)

From the task worktree root, use host Python 3.12+ and a running Docker daemon in
Linux-container mode. The runner uses only the Python standard library on the
host; it installs project/test and dashboard dependencies in the image. The first
build needs access to base images and package registries. Check Docker first:

```powershell
python --version
docker info --format '{{.OSType}}'
```

The Docker result must be `linux`. Native runs additionally require
`docker compose version`. If `docker` is not on PATH, add Docker Desktop's
`resources/bin` directory; the Python runners also discover standard Windows
Docker Desktop install locations.

If `python` is unavailable on PATH, substitute `py -3.12` or an absolute path to
an existing Python 3.12+ executable (for example the checkout's
`.venv\Scripts\python.exe`). A quoted executable path in PowerShell needs the
call operator: `& 'C:/path/to/python.exe' scripts/container_checks.py --help`.

```powershell
python scripts/container_checks.py --workers 2 --image rimbot-checks:my-task --output .rimbot/docker-checks-01
Get-Content .rimbot/docker-checks-01/result.json
```

Do not create the output directory first: the runner creates it and refuses an
existing path. `--workers` accepts 1 through 8 (default 2). Each worker runs the
**entire** Python suite; this is isolation/repetition, not sharding. Use 1 for a
single regression run. `--timeout 600` is the default per-worker limit in seconds;
the image build has a separate 1,800-second limit. Dashboard typecheck, Vitest and
build run in the image build stage (which Docker may cache), not in each worker.
Neither native DLL compilation nor the separate schema-generation check runs here.

Success requires process exit code 0 and `result.json` with `passed: true`,
including every worker's successful exit, JUnit presence and cleanup. Inspect:

| Artifact under the output directory | Purpose |
| --- | --- |
| `build.log` | Image/dependency/dashboard build output; absent with `--no-build`. |
| `result.json` | Immutable image ID, scope, timings and worker results. |
| `0/pytest.log`, `0/junit.xml` (and `1/`, etc.) | Test failures, counts and platform skips per worker. |
| `0/cleanup.log` (and peers) | Removal of only this invocation's named containers. |

A build or image-inspection failure can occur before `result.json` exists; inspect
the console and `build.log` rather than treating missing results as a pass. Preserve
the directory and choose a fresh name after fixing a failure.

To repeat an unchanged image, use
`--image rimbot-checks:my-task --no-build --output .rimbot/docker-checks-02`.
Omit `--no-build` after source changes: source is copied into the image, not mounted
from the worktree. Keep image tags unique between concurrent tasks.

For a focused test, the wrapper has no pytest-argument forwarding. Build the same
test target and invoke pytest directly instead:

```powershell
docker build -f containers/Dockerfile --target tests -t rimbot-checks:my-task .
docker run --rm --init rimbot-checks:my-task python -m pytest -q controller_tests/test_container_worker.py
```

This direct command reports to the terminal; it does not produce the wrapper's
retained result manifest or JUnit artifacts.

### Native Docker inputs

Docker Engine/Desktop with Linux containers and Compose is required. The image
build runs dashboard typechecking/tests/build; the `tests` target runs the Python
suite. Windows-specific tests skip on Linux. Native acceptance is separate. Compose
workers set `gc-max-time-slice=0` in their private Unity boot.config to mitigate
observed Mono startup crashes. Original game inputs remain unchanged. Set
`RIMBOT_UNITY_GC_TIME_SLICE=source` to preserve the original setting for comparisons.
The source boot hash, prepared boot hash and effective override are retained in
`staging.json`/`inputs.json`. Broader startup and GC-pause acceptance remains in B17.
Windows game executables cannot run in this image. Supply your licensed Linux
RimWorld installation (including Data/Mono files), a Linux amd64 GABS executable
named `gabs`, a complete `Mods` directory, and a prepared profile containing
`Config/Prefs.xml`, `Config/ModsConfig.xml` and
`Saves/RimBot-tribal8-baseline.rws`. The save is copied unchanged.

The mod directory must contain Core/DLC content where required by the game,
Harmony, RimBridgeServer, RimBotObservations (including its identity assembly and
BridgeTools) and RimBotHeadless. Resolve workshop links into this snapshot and
match the active package IDs in ModsConfig.xml. Build task DLLs into a private
staging directory using the native projects' path properties; do not use `-Install`
on a shared running Windows installation. Finish staging all inputs before launch.
Images contain controller/dashboard code only; licensed game files are mounted
at runtime and never included in the build context.

### Manual native Docker worker

For a native worker, set absolute input paths and create a fresh output directory:

```powershell
$env:RIMBOT_LINUX_GAME = 'D:/RimBotInputs/linux-game'
$env:RIMBOT_WORKER_MODS = 'D:/RimBotInputs/mods-build-a'
$env:RIMBOT_WORKER_PROFILE = 'D:/RimBotInputs/profile'
$env:RIMBOT_LINUX_GABS = 'D:/RimBotInputs/linux-gabs'
$env:RIMBOT_WORKER_OUTPUT = 'D:/RimBotRuns/a'
$env:RIMBOT_WORKER_PORT = '8788'
New-Item -ItemType Directory $env:RIMBOT_WORKER_OUTPUT
docker compose -f containers/compose.yaml -p rimbot-a up --build -d
```

In a second terminal/worktree set the same input variables, select that task's mod
snapshot, and use a fresh output directory, port `8789` and project `rimbot-b`.
Do not use `--scale`: each worker needs its own output mount and host port. Images
are built per Compose project, so worktree changes do not replace a peer's image.
The dashboard is at `http://127.0.0.1:8788` (or the selected port).

LM Studio must accept connections from Docker on port 1234 with the configured
local model loaded. Compose explicitly permits `host.docker.internal`; it does
not enable arbitrary remote model URLs. Host firewall/server configuration may
be needed. Verify a real local model response before measuring inference.
Docker's [host networking documentation](https://docs.docker.com/compose/how-tos/networking/)
and [loopback port publishing](https://docs.docker.com/engine/network/port-publishing/)
describe these mappings.

Each startup copies game/mod binaries into the private container-local
`/opt/rimbot-game` directory and the prepared profile to `/worker/run`. The game
uses Linux filesystem semantics; later input DLL replacements cannot change its
running snapshot. `run/inputs.json` records staged hashes; profiles,
GABS configuration/claims, logs, controller SQLite and checkpoints stay under the
output mount. The private game copy is removed with the container. An existing
`run` or private game directory is refused, including after an incomplete
startup. Preserve it as evidence and select a fresh output for another run.
`docker compose ... down` stops only that project; it does not delete bind-mounted
artifacts. Do not use Docker restart as a checkpoint restore procedure.

### Automated native Docker acceptance

Run two separate Compose projects with automatic free loopback ports and native
clock/checkpoint verification:

```powershell
python scripts/container_native_acceptance.py --game <linux-game> --mods <private-mods> --profile <prepared-profile> --gabs <linux-gabs-directory> --image rimbot-worker:my-task --output .rimbot/docker-native-01
```

The runner builds/pins the worker image, loads two private copies of the baseline,
advances one colony while the other remains paused, stops the first project and
requires a fresh native read from the survivor. It then saves a paired native and
controller checkpoint. Both projects are removed afterward; output trees, logs,
input hashes, checkpoint and result manifest remain. Failures are retained. Add
`--image <tag> --no-build` to use an existing image, or `--startup-timeout 480` for
slow Windows bind mounts. `run/staging.json` measures the input-copy time.

Replace the angle-bracket placeholders with existing absolute paths, quoting paths
with spaces. `--gabs` is a directory containing `gabs`, not the executable path.
Unlike manual Compose setup, do not pre-create `--output`; the runner creates it
and supplies the input/output/port environment variables itself. Startup timeout
defaults to 240 seconds. The runner does not send player chat or measure inference;
LM Studio is needed when subsequently testing model-dependent behavior.

Require exit code 0 and `result.json` with `passed: true`; inspect its cleanup and
checkpoint hash results too. Each numbered worker directory retains `compose.log`,
`container.log`, `cleanup.log` and `run/` evidence. For startup failures inspect
`run/Player.log`, `run/staging.json` and, in rendered mode, `run/display/`. Missing
input paths, a Windows game/GABS binary, mismatched mods or a reused output require
fixing the inputs and choosing a fresh run directory. Do not silently retry native
crashes or clean up other tasks with global Docker prune commands.

The current runner's assertions cover lifecycle, not completed pawn work. Docker
can run controller and native outcome assertions together; use or port gameplay
scenarios to verify actual pawn work. Reusable scenario and recorder improvements
are tracked under B17 in [BACKLOG.md](BACKLOG.md). Throughput needs separate
measurement. Startup failures are never retried silently. Remaining probes with hard-coded Windows paths must be ported
before use in containers. Headless remains the default. For rendered tests set `RIMBOT_DISPLAY=xvfb`,
`RIMBOT_DISPLAY_RESOLUTION=1280x720` (640x480 through 3840x2160) and
`RIMBOT_DISPLAY_RENDERER=llvmpipe`. Each container owns Xvfb `:99` in its own
namespace with TCP disabled; no host display socket or desktop focus is used.
The worker verifies software OpenGL before launching the controller, sets private
resolution/fullscreen preferences with UI scale 1, removes the
HeadlessRim active package in its private profile and uses Unity OpenGL rendering.
The rendered profile does not require the HeadlessRim DLL. Game logs remain in
`run/Player.log`; `run/display` retains Xvfb, display capability and renderer logs,
plus exit status. Startup/display failures fail the worker without retries.
The display stays alive through controller cleanup and stops with its container.

Add `--display xvfb --resolution 1280x720` to the native acceptance command to
retain exact-resolution `frame.png` for each worker and a changed `survivor.png`
after its peer stops and native camera pan completes, alongside normal
clock/checkpoint evidence. The survivor must remain at its paused native tick. Inspect these frames for actual colony content;
PNG presence and size alone do not establish visual correctness. Use a fresh output
for a matching headless comparison. Frame transport, input gestures and sustained
rendering overhead require their own acceptance under B17/B18.

For B18 handoff and stable-ID selection, also pass `--player-input` with
`--display xvfb`. The survivor acquires a lease, rejects other viewers and stale
credentials, selects a current-map colonist, confirms the selection through a
separate native read, clears it and releases into Manual. The probe renews its
lease like the browser and retains per-request evidence in `player-input.json`.
These checks do not exercise raw image coordinates, drag/modifiers or WebRTC.

### Existing diagnostic recording

There are useful flight-recorder components, but no complete correlated failure
bundle or general offline replay workflow yet:

| Evidence | Available behavior and limits |
| --- | --- |
| Controller SQLite (`store.py`) | Persists state, events and retired action/method evidence. Event history can include diagnostics; default history excludes diagnostic kinds. |
| `GET /api/diagnostics` | Read-only latest 100 events for the current colony, including diagnostics; not a complete run export. |
| Tool diagnostics | Planner tool arguments/results, outcomes and timing are recorded with bounded payloads. Runtime native dispatches record tool results and receipts; this is not exhaustive coverage of background reads or failed/pre-dispatch calls. |
| `ReviewEvidence` | Exact observations within a review, held in memory with a byte budget and eviction; not durable recording across process failure. |
| Decision storage helpers | `Store.decision`, `finish_decision` and `read_decision` support compressed snapshots capped at 64, but currently have no runtime callers. Do not assume a run populated them. |
| Native Docker output | Worker logs, `run/Player.log`, staging/input manifests, controller data, and any captured frames or successful paired checkpoints survive container removal. The native runner's `result.json` describes its assertions and cleanup. |

For a failed Docker run, start with `result.json` and the numbered worker's
`container.log`, then inspect native logs and retained controller evidence. A
failure before report creation may leave only console/build output. Keep the
whole output tree; a game crash may prevent a final paired checkpoint. Existing
evidence cannot be assumed to reconstruct every observation or pawn transition.
The correlated recorder, automatic failure export and reusable regression loop
are unfinished B17 work in [BACKLOG.md](BACKLOG.md).

### Steam Linux inputs

Use the signed-in Steam client's console (`steam://nav/console`) to query
`app_info_print 294100`. In its `depots` section, choose the base depot with
`oslist` set to `linux` (294103) and its current `public` manifest. Run
`download_depot 294100 294103 <manifest>`. Steam reports completion and the separate
`steamapps/content/app_294100/depot_294103` directory; this does not switch the
installed Windows game's platform. Wait for completion before copying the files.

For installed DLC, select each Linux depot with the corresponding `dlcappid` from
the same metadata and download it through the signed-in client. Royalty, Ideology,
Biotech and Odyssey use 1149643, 294108, 367686 and 294116 respectively. Use Steam's
current manifests, and download only owned DLC required by the profile. Copy the
base depot into a fresh private game directory, then merge the downloaded DLC
`Data` directories into that directory's `Data`. Retain depot/manifest IDs and
`Version.txt` beside the test evidence. Never put licensed inputs in Git or images.

Copy Harmony and the complete RimBridgeServer mod into the private mod directory.
Build task-local companion binaries against the Linux references, then copy their
About/Assemblies/BridgeTools folders into private RimBotObservations and
RimBotHeadless directories. For example (use an available .NET SDK):

```powershell
dotnet build integrations/colony-bridge/src/ColonyObservations.csproj -c Release "-p:RimWorldManagedDir=<linux-game>/RimWorldLinux_Data/Managed" "-p:RimBridgeSdkDir=<private-mods>/RimBridgeServer/1.6/Assemblies" "-p:HarmonyAssembly=<private-mods>/Harmony/Current/Assemblies/0Harmony.dll"
dotnet build integrations/headless-rim/src/HeadlessRim.csproj -c Release "-p:RimWorldManagedDir=<linux-game>/RimWorldLinux_Data/Managed" "-p:HarmonyAssembly=<private-mods>/Harmony/Current/Assemblies/0Harmony.dll"
```

Use a Linux GABS release matching the tested bridge version, verify the upstream
release asset SHA-256, and retain its LICENSE/provenance alongside the executable.
Do not install these task builds into the shared Windows game.
### Resource production budgets

`scripts/resource_policy_acceptance.py --source-root <ordinary-prepared-root>
--output <fresh-directory>` creates a normal crafting spot and a native bill,
then verifies stopped production, one actual pawn-produced output, exact input
consumption and a reserve preventing another cycle. It also records native sources
for the named resource targets. Install both the current observation and identity
assemblies plus the current headless companion before launching; restore originals
only after every game closes. This focused probe does not certify unavailable
industrial recipes, every material alternative or sustained production.

With `--acquisition`, the resource policy probe also requires pawn-produced steel,
components and herbal medicine from observed normal mining/harvest sources. It
compiles native target work types through the shared work allocator and records
assignment receipts plus actual stock increases; designation receipts alone fail.

`scripts/resource_substitution_acceptance.py --checkpoint <paired-manifest>
--output <new-directory>` requires a native checkpoint with observed wood and steel.
It reserves all available wood, requests a wall with WoodLog/Steel alternatives,
and requires an ordinarily completed native Steel wall, actual steel consumption
and the preserved wood floor. The checkpoint remains immutable.

`scripts/resource_fuel_acceptance.py --source-root <ordinary-industrial-start>
--output <new-directory>` requires normal construction of a research bench,
ordinary BiofuelRefining research, an ordinarily built generator and refinery,
then actual chemfuel from the shared resource target bill. Missing prerequisites
must be reported before the new infrastructure and reconsidered afterward.
The wall-clock limit is an acceptance bound, not a simulation or research shortcut.

Add `--capacity` to the substitution probe to observe an existing bill covering a
resource target, explicitly reduce that fixture bill's target, and require a new
shared target bill plus actual additional output. The original bill settings must
remain unchanged when capacity is added.
Add `--lease` with `--capacity` to require an unissued native construction budget
to stop production during supervised ticks, then actual production and exact
ingredient consumption under ordinary Manual play after the lease ends. The
fixture settles pending controller reviews before starting the Manual clock and
records native time, pawn, bill and stock evidence so a pause cannot be mistaken
for a production-policy refusal.
Fuel acceptance uses ordinary food acquisition/cooking and two ordinary research
benches so prerequisite research shares the colony's real labor and food budget.
Research progress checkpoints and an optional unchanged native autosave input
preserve real work across disposable test runs; neither supplies research points.
Use `--checkpoint <paired-manifest>` to preserve both native research and controller
ownership. Completed explicit fixture setup orders are archived through normal
plan revisions; their verified receipts remain in the paired controller database.
Force-paused research dialogs are captured with UI targets and a new paired
checkpoint before cleanup, preserving the native prerequisite outcome for review.
Use `--production-resume --checkpoint <paired-manifest>` to continue an existing
refinery and bill without repeating prerequisite setup. The probe records actual
pawn jobs and stock through production. An observed supported animal threat can
hand control to the shared deterministic defense method, then return to Manual
after native threat clearance. Native guards remain active throughout.

The named-resource matrix covers actual steel, component, herbal-medicine and
wood acquisition, plus chemfuel production. Industrial-medicine target resolution
does not certify industrial-medicine manufacturing or unavailable prerequisites.
