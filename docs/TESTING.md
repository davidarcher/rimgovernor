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
