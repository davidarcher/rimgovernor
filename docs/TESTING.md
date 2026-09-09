# Development and gameplay verification

Use [README.md](../README.md) for initial setup and launch. Run commands below from
the repository root. All unfinished validation belongs in [BACKLOG.md](BACKLOG.md).

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

## Native execution windows

Run `scripts/native_tick_budget_acceptance.py --source-root <prepared-root>
--output <new-directory>` with `controller` on `PYTHONPATH`. Each speed/budget
case reloads the unchanged baseline. The native companion must independently
pause at 1, 37 and 600 ticks at Normal, Fast and Superfast, retain its deadline
across renewal, and remain paused afterward. The probe also checks external native
speed/pause commands, lease expiry before the deadline and retirement on load
changes. Add `--rendered` for a visible game. Native clock commands reproduce
time-state transitions; they do not certify physical keyboard input, autosaves
or every danger race. Keep all failed reports.

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
GABS and installed observation-DLL fingerprints. Model weights and other mod
binaries remain outside that fingerprint and must be held fixed by the operator.

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

The runner rejects fresh baselines past tick 600 and injects a model client that
fails on any attempted inference. By default it requires two consecutive game
days with all eleven gates verified and reports `SUSTAINED_FOOTHOLD`. Use
`--stability-days 0` for establishment-only `FOOTHOLD_STABLE`, or an explicit
number up to 30 for another duration. `--seconds` bounds the whole episode.

Stability uses the native facts' tick, not wall time or a newer clock reading.
A recorded stability loss resets the window even when recovery occurs between
report samples. Unknown/failed gates and observation gaps over 6,000 ticks also
reset it. Save/load identity changes, rewinds and missing/dead starting colonists
fail the episode. Reports preserve losses, the longest qualifying interval and
maximum observation gap. This certifies sampled maintained gates over the stated
window, not arbitrary long-term survival or difficult-biome coverage.


### Visible dashboard acceptance

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

`scripts/cancel_construction_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` tests the native exact-target cancellation contract in a private
paused colony. It creates ordinary blueprint orders and a zero-work sleeping spot,
then verifies dry-run preservation, stale colony/map/load and metadata refusal,
completed-building refusal, exact blueprint removal and repeated-request refusal
without retargeting its neighbor. Add `--shared` to create a room through the
semantic command and shared Hands, prevalidate its cancellation, drop a successful
native removal receipt, and verify that a fresh request removes only the remaining
targets. Repetition after completion creates no native removals; unrelated pending
orders and completed buildings remain. This does not establish frame refunds,
local-model interpretation or mixed restart acceptance. Install its companion and
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
