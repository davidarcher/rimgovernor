# Run focused headless probes

[Documentation](../../README.md)

Choose a native domain probe and satisfy its fixture requirements before launching a
disposable game.

[Native test prerequisites](README.md#native-test-prerequisites).

Build/install the unified package using [Windows setup](../../players/setup.md),
then launch the isolated headless profile:

```powershell
powershell -ExecutionPolicy Bypass -File launch.ps1 -Headless -NoBrowser
```

This derives `.rimgovernor/bridge/headless-profile`, keeps the unified runtime enabled and uses
`-batchmode -nographics`. Native fade readiness still matters. The dashboard stays
available without images; restart without `-Headless` for interactive rendering.

For disposable scripted acceptance, close the interactive controller/game first. Check
each script's `--help` and fixture requirements before running it:

| Area | Scripts under `scripts/` |
| --- | --- |
| Identity and clock | `native_migration_smoke.py`, `native_clock_smoke.py`, `native_stand_down_smoke.py` |
| Construction and installation | `native_strategy_smoke.py --headless --room`, `install_smoke.py`, `test_install.ps1` |
| Combat and medical outcomes | `native_combat_smoke.py --tend`, `native_ranged_smoke.py`, `native_rescue_smoke.py` |
| Native domains and UI | `native_trade_smoke.py`, `native_research_smoke.py`, `native_world_smoke.py`, `native_letters_smoke.py`, `dialog_smoke.py`, `dialog_text_smoke.py`, `companion_inspection_smoke.py` |
| Models and rendering | `execution_schema_smoke.py`, `native_scout_smoke.py`, `native_visual_smoke.py`, `native_render_smoke.py` |

## Advance a native scenario

Use the shared test operation for new bounded simulation waits:

```python
from rimgovernor.native_scenario import advance_game

clock = await advance_game(rt, 600, report, timeout=120)
```

It owns the runtime writer lock while the independent clock watcher renews the
lease. Ancient danger (`ThreatBig`, English fixture label) is the default approved
warning. The operation requires the exact letter ID from the native pause event,
complete current safety observations, no active hostiles/hunting predators,
no dead, downed or bleeding colonists and no modal window. It records acknowledgment and
continues only the unspent tick budget. It does not dismiss notifications or release
player holds. Localized fixtures can supply their exact `(label, letterDef)` pairs
through `expected_letters`; unknown labels fail closed.

Pass `expected_letters=()` when testing interruptions; the stop raises
`ScenarioInterrupted` with the native evidence. Reports accumulate windows,
interruptions and failures under `simulation`; persist the report in the scenario's
existing failure/finally path. Timeouts attempt to pause only the owned game.
Do not call this operation while already holding `rt.lock`.

Husbandry, freezer expansion and construction cancellation use this path. Older
specialized probes retain their own clock behavior pending migration; production
automation uses its existing deterministic review policy.

## Treatment recovery

Inside a fresh Docker worker, run `python scripts/native_combat_smoke.py
--prepared-root /worker/run --require-interruption --tend` through the worker's
command override. Select task source with `PYTHONPATH=/app/controller` when
mounting source over an image. The report records staged inputs and Python hashes,
native draft claim loss across external undraft/redraft, real wounds, injury
preemption, stale-order refusal and completed tending. The external setter probe
does not simulate mouse input. Use fresh outputs and private task-built mod inputs.

For confirmed treatment interruption and recovery, use a fresh isolated worker:

```powershell
python scripts/native_combat_smoke.py --recovery --source-root <prepared-root> --output <new-worker-root>
```

The fixture requires suitable nearby wildlife and an ordinary combat wound. It
interrupts a confirmed tend job through owned stand-down while paused, verifies the
interruption, recovers the same action with archived receipts, observes native treatment
completion and checks owned-draft cleanup. It does not inject damage or heal pawns.
`--patient-save <native-save>` can copy an existing wounded-patient save unchanged into
the new worker. A fixture with no patient is a failed prerequisite, not a recovery pass.
Preserve all reports. Recovery exhaustion, stale reads, save rewinds and player
overrides also have deterministic replay coverage.

## Emergency and development acceptance

`scripts/b04f_acceptance.py --root <staged-worker-root> --case
development|medical|combat|health` runs the deterministic emergency/development matrix.
Build the task-private companion with `-p:EmergencyDevelopmentFixture=true` and stage
it with the local Docker worker. Keep writable runtime files on Docker's Linux filesystem
(for example `container_worker --root /native/run`) and copy the reports to the task's
bind-mounted output directory after shutdown. Windows bind-mounted GABS claim files can
produce publication races. Use a fresh source/mod snapshot and worker root for every run.

The development case uses normal Peaceful difficulty and nonperishable starting food.
Use `--new-crashlanded` for ordinary native three-colonist generation and starting
technology; the case must complete a newly selected available research project.
For an unchanged native autosave continuation, pass `--research-project <defName>`
to retain that completion requirement. Keep the original save and its input hash.
The case saves `B04f-continuation` after completed construction and reuses an
observed lamp/generator on continuation. It checks their exact shared power network,
positive generator output and completed conduits. Pawn construction skills remain
unchanged; native skill prerequisites can block an unsuitable electrical fixture.
The test explicitly acknowledges optional quest offers and animal-roaming notices
without accepting quests or changing the production interruption policy.
The test-only fixture supplies starting resources, clubs for ordinary equip jobs, opponents, wounds, unavailable-doctor
mental state and native external orders. It never supplies finished buildings, research
or treatment. The cases require actual expanded indoor capacity, completed research,
connected powered loads and completed comfort furniture; two treated patients after a
provider replacement and a preserved player order; multi-opponent defense with triage;
and compiler/dispatch/clock health holds including stale-preview and repeated admissions.
Keep `b04f-result.json`, input/source manifests, SQLite observations and game logs together.
Build again without the fixture property for production; never install fixture DLLs into
a shared running game.


Read assertions before interpreting results: for example, a healthy-pawn rescue refusal
does not validate carrying a patient to bed. Some scripts use real models, some scripted
decisions, and some only inspect/refuse actions. Reports stay local under `.rimgovernor/`;
retain failures as well as successful runs.

## Real-model execution

### Reuse one game between execution cases

Add `--reuse-game` to `scripts/execution_acceptance_smoke.py --case all` to run the
supply, work and bill cases sequentially in one private headless game. Use
`--source-root <prepared-root>` for Windows or a staged Linux worker. For example:

```powershell
$env:PYTHONPATH='controller'
.venv\Scripts\python.exe scripts/execution_acceptance_smoke.py --case all --reuse-game --source-root .rimgovernor/bridge --output .rimgovernor/execution-reuse-01
```

Each case reloads the unchanged baseline paused at its saved tick (at most one native
tick of advancement), requires a new load token, and creates a fresh runtime and SQLite
database. Before reuse, the helper verifies pause, stopped clock supervision and released
draft ownership, then revokes the previous case's client. Failures, unfinished native
requests, unexpected load changes and changed fingerprinted inputs retire the worker;
remaining cases are reported as not run. Only the worker's GABS-owned process is stopped.
No model provider or interpretation behavior changes; these cases still require the
configured local LM Studio model.

Omit `--reuse-game` for a new process per case. Reuse does not reset mod static state,
Unity caches or process-wide mod settings and is not fresh-process acceptance. Keep all
game/mod inputs fixed, and restart after changing binaries, mod content, load order or
preferences. The helper checks the baseline, launch configuration, mod selection,
executable, GABS and mod file hashes before each case; it does not fingerprint every
base-game asset. Do not use this mode for startup, crash recovery, paired-restart,
rendering or static-state isolation tests. Other native probes and campaigns retain
their existing process lifecycle.

`worker/reuse.json` records startup, per-case ready timings, native load identities,
baseline tick, fingerprints, cleanup and shutdown evidence. Each case has its own
database under the worker and a `<case>.json` report at the output root. Fresh-process
mode instead uses `worker-<case>/reuse.json`. Neither mode retries failed writes.

For model-free acceptance of the reuse boundary itself, run:

```powershell
.venv\Scripts\python.exe scripts/game_reuse_acceptance.py --source-root .rimgovernor/bridge --output .rimgovernor/reuse-native-01
```

This runs three baseline loads in one process. Each case verifies a scoped native Allow
change and owned drafting; the next case must see the original forbidden supplies, a
new load identity, no prior controller marker/chat/queued request and a revoked old
client. Cleanup separately verifies the pawn is undrafted. This verifies reuse and
ordinary native mutations, not pawn labor or model interpretation. In Docker, run the
same script as a `rimgovernor.container_worker` command with `--source-root /worker/run` and
an unused output under `/worker`; the cached input staging workflow is unchanged.

For targeted real-model execution, run `.venv\Scripts\python.exe
scripts\execution_acceptance_smoke.py`, optionally with `--case supplies`, `--case work`
or `--case bill`, plus `--model` and a fresh `--output` directory. Each case uses an
isolated headless baseline and verifies native readback through the normal
commitment/Hands path. The accepted scope is one selected supply stack allowed, work
enabled in checkbox mode and one bill created on the exact bench. It does not certify
numbered priority scheduling or completed production. All three cases passed with Qwen
3.5 9B; local evidence is under `.rimgovernor/execution-acceptance-1788898095948598300/`.

## Priorities and cooking

For numbered priorities and actual cooking, run:

```powershell
.venv\Scripts\python.exe scripts\production_acceptance.py --source-root .rimgovernor/bridge --output .rimgovernor/production-new
```

This isolated fixture enables numbered work priorities, replaces one starting food stack
with rice, and uses ordinary pawn labor to build a campfire. Qwen commits Cooking
priority 1 and a two-repeat meal bill through the normal Hands path. Fresh readbacks
must show effective/stored priority 1, rice carried during DoBill, fewer ingredients,
produced meals and the exact bill counter changing from 2 to 0. All rejected model
proposals are retained. This accepts controlled worker scheduling and production, not
autonomous food strategy or sustained survival. Ingredient whitelist selection is a
separate player-action acceptance case.

## Windows runtime-file recovery

For Windows runtime-file recovery, run the following with the controller environment:

```powershell
.venv\Scripts\python.exe scripts\runtime_file_acceptance.py --source .rimgovernor/bridge --output .rimgovernor/runtime-file-new
```

The test owns an isolated game and uses real Windows file handles to block private
runtime-state publication and reads beyond the retry budget. It verifies unchanged
receipts and native object identities after handle release, without replaying orders.
Choose a fresh evidence directory for every run.

## Dialogs and inspectors

For naming and quest-choice checks, `scripts/modal_acceptance.py --source .rimgovernor/bridge
--output .rimgovernor/modal-new` requires a temporary `ModalFixture=true` build in a rendered
isolated game. For controlled sleeping, hauling, construction interruption and removal,
`scripts/campaign_metrics_acceptance.py --source .rimgovernor/bridge --output
.rimgovernor/metrics-new` requires `CampaignMetricsFixture=true`. Both fixtures are excluded
from production builds. Install and restore DLLs only after an escalated CIM process
check confirms every RimWorld instance is closed. The harnesses stop their own games;
preserve failures and use new output paths.

For selective native inspector checks, run `scripts/inspector_acceptance.py` with
`--source .rimgovernor/bridge --output .rimgovernor/inspectors-new`. Its optional `--fixture`
requires a temporary build with `-p:InspectorFixture=true` and populates thermal,
ingredient, power and storage cases. Keep all games closed while swapping DLLs, restore
the previous DLL afterward, and exclude fixture tools from the production build. The
inspector report tests data and scope contracts, not cooling capacity or completed
production.

## Related reading

[Testing](README.md) · [Issues](https://github.com/davidarcher/rimgovernor/issues)

## Combat and rescue in Docker

The same Docker worker command override supports:

- `scripts/native_ranged_smoke.py --prepared-root /worker/run`: ordinary weapon
  equip, native movement completion and an observed ranged wound.
- `scripts/native_rescue_smoke.py --prepared-root /worker/run --chat`: ordinary
  combat downs an observed Wimp patient, then the configured local model selects
  patient and rescuer through `RescuePawn`. Require the exact carried identity,
  living patient in a native bed and shared action completion. Without `--chat`,
  test the deterministic semantic admission and delivery path. Both variants
  reject new standing-only attack orders against the downed patient.
- `scripts/native_raid_acceptance.py --prepared-root /worker/run`: a separate
  test-only `CombatFixtures.csproj` assembly invokes an eligible native RaidEnemy
  incident. Build against the same game/SDK references and stage its DLL under
  the private mod's `BridgeTools/CombatFixtures`. Use an unchanged ordinary-play
  save beyond native raid grace periods, retaining its source hash; a new colony
  can legitimately refuse the incident. Never force eligibility or edit ticks.
  Require ordinary enemy defeat, no incapacitated colonists, shared deterministic
  defense and controller-selected owned stand-down. The single melee raider test
  does not certify ranged raids, groups, or general tactics.

Rescue legality includes native bed, reservation and path checks, but the native
order path permits `Danger.Deadly`. It is not evidence of safe fire/heat traversal.
These probes do not enable automatic rescue, firefighting or heat escape. Keep
test incident tools outside model execution and preserve failed reports.


