# Run focused headless probes

[Documentation](../README.md)

Choose a native domain probe and satisfy its fixture requirements before launching a
disposable game.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimbot/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

Close controller/game before installing and launching the isolated headless profile:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_headless.ps1 -Install
powershell -ExecutionPolicy Bypass -File launch.ps1 -Headless -NoBrowser
```

This derives `.rimbot/bridge/headless-profile`, enables HeadlessRimPatch and uses
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

Read assertions before interpreting results: for example, a healthy-pawn rescue refusal
does not validate carrying a patient to bed. Some scripts use real models, some scripted
decisions, and some only inspect/refuse actions. Reports stay local under `.rimbot/`;
retain failures as well as successful runs.

## Real-model execution

For targeted real-model execution, run `.venv\Scripts\python.exe
scripts\execution_acceptance_smoke.py`, optionally with `--case supplies`, `--case work`
or `--case bill`, plus `--model` and a fresh `--output` directory. Each case uses an
isolated headless baseline and verifies native readback through the normal
commitment/Hands path. The accepted scope is one selected supply stack allowed, work
enabled in checkbox mode and one bill created on the exact bench. It does not certify
numbered priority scheduling or completed production. All three cases passed with Qwen
3.5 9B; local evidence is under `.rimbot/execution-acceptance-1788898095948598300/`.

## Priorities and cooking

For numbered priorities and actual cooking, run:

```powershell
.venv\Scripts\python.exe scripts\production_acceptance.py --source-root .rimbot/bridge --output .rimbot/production-new
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
.venv\Scripts\python.exe scripts\runtime_file_acceptance.py --source .rimbot/bridge --output .rimbot/runtime-file-new
```

The test owns an isolated game and uses real Windows file handles to block private
runtime-state publication and reads beyond the retry budget. It verifies unchanged
receipts and native object identities after handle release, without replaying orders.
Choose a fresh evidence directory for every run.

## Dialogs and inspectors

For naming and quest-choice checks, `scripts/modal_acceptance.py --source .rimbot/bridge
--output .rimbot/modal-new` requires a temporary `ModalFixture=true` build in a rendered
isolated game. For controlled sleeping, hauling, construction interruption and removal,
`scripts/campaign_metrics_acceptance.py --source .rimbot/bridge --output
.rimbot/metrics-new` requires `CampaignMetricsFixture=true`. Both fixtures are excluded
from production builds. Install and restore DLLs only after an escalated CIM process
check confirms every RimWorld instance is closed. The harnesses stop their own games;
preserve failures and use new output paths.

For selective native inspector checks, run `scripts/inspector_acceptance.py` with
`--source .rimbot/bridge --output .rimbot/inspectors-new`. Its optional `--fixture`
requires a temporary build with `-p:InspectorFixture=true` and populates thermal,
ingredient, power and storage cases. Keep all games closed while swapping DLLs, restore
the previous DLL afterward, and exclude fixture tools from the production build. The
inspector report tests data and scope contracts, not cooling capacity or completed
production.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)

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


