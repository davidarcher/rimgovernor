# RimBridgeServer replacement

Decision: replace RIMAPI incrementally with GABS + RimBridgeServer. Retain the Python controller, goals/memory, advisors, arbitration, outcome tracking, reservations and web console. One backend and one writer per session.

## First slice

Implemented the standard MCP SDK connection to GABS, on-demand schemas, paginated tool discovery, native receipt/error preservation, isolated profile preparation and a repeatable live probe. No emulated RIMAPI responses or automatic mutation retries. The transport remains harness-only until a reviewed gameplay catalog is connected.

Tested binaries: GABS v1.1.1, RimBridgeServer v2.1.1, RimWorld 1.6.4871. Both upstream projects are MIT licensed; no implementation copied. Source inspected at RimBridgeServer ca5997c9e02f53609358223a673834e35a1d1ee1 and GABS b5f441a04fa908852fdece18a08a8876ac970a4d. These source heads are not asserted to match release binary revisions.

Live evidence: `.rimbot/bridge/evidence/20260907-173107`. Same eight-tribal fixture, Harmony + official content + RimBridgeServer, no RIMAPI and no model calls.

| Path | Observed result |
| --- | --- |
| Colony | Eight native pawn IDs, positions, current jobs |
| Time | Paused readback; startup ultrafast boost disabled |
| Architect | Native categories and designators enumerated |
| Sleeping spot | One immediately built SleepingSpot in cell readback |
| Stockpile | Two-cell zone, readback cellCount=2 |
| Draft | Draft true and undraft false both read back |
| Selection | Grouped gizmos and selection semantics returned |
| Notifications | Message/letter queries responded; push untested |
| Screenshot | Capture returned successfully in about 230ms |

Most game calls took 20â€“50ms. These are API timings, not inference or starter-base performance. GABS stdio shutdown ended its launched game in these trials; a short-lived client must not promise to leave the game running.

## Defects and gaps

- **Single-cell zone creation fails.** Dry-run accepts, apply returns success=false: method not implemented. `RimWorldArchitect.ApplyArchitectDesignatorResponse` selects `DesignateSingleCell` for one accepted cell; zones require the multi-cell native path. Two-cell stockpile is a separate passing test, not a production workaround. Failed receipt is in an earlier evidence folder's `stockpile-apply.json`. Fix upstream/fork and retain one-cell regression before migrating storage.
- **Readiness:** mapData can precede Playing and yield zero colonists. Probe waits for visual readiness and asserts eight colonists.
- **Receipts:** isError=false and operation.Success=true can coexist with payload success=false. Client rejects the latter. A pause during loading said "Game paused" with paused=false; probe now verifies actual paused state after visual readiness.
- **Gameplay policy:** 125 discovered tools include spawning, settings, save/load and scripting. Expose an explicit reviewed gameplay subset to the model; lifecycle access belongs to the harness. Verify startup debug defaults before production.
- **Coverage:** compact cell thing records omit stable IDs. Resource aggregates, needs/social detail, work priorities, bills, filters, construction material/rotation and right-click execution still need contract/live checks.
- **Dashboard:** screenshots do not yet replace HTTP video. Controller and dashboard remain on RIMAPI until adapter gates pass.

## Replacement gates, in order

### Companion reuse direction

Inspected [Snowstar38's companion](https://github.com/Snowstar38/rimworld-claude-harness/tree/main/companion/src) at 89c2e90fedd51419a3db55a7f9865b0aef29b270. It is the missing gameplay domain layer, overlapping much of our RIMAPI extensions. Prioritize this review before building another companion:

- `ListPawnsTool`: queries the pawn registry directly, with optional health/needs/equipment/work/social blocks. Comments document replacing thousands of cell inspections with a pawn query.
- `ListThingsTool`, `ListBuildingsTool`, `ListRoomsTool`, `StatusTool`: compact censuses and actionable exceptions, supply ownership/forbidden/storage partitions, jobs and alerts. Candidates for our observation adapter.
- `ZoneCellsTool`: filters/crops and before/after verification; documents overlapping zone-list/grid corruption in the upstream direct AddCell path. Important regression to reproduce, beyond the single-cell failure we observed.
- `PlaceBuildingTool`: material/rotation, native footprint/refusal checks and refund-aware replacement. Calls native construction internals directly, reproducing the designator sequence; it is not merely a generic registry wrapper. Audit eligibility and instant-build semantics before adoption.
- `OrderTool`: explicit IDs, native ordered jobs and CurJob readback; documents the same target ambiguity and unnecessary hostility preconditions we encountered. Reproduces native checks/job shapes, so maintenance and fidelity still need tests.
- `BillsTool`, `PawnConfigTool`, `BuildingConfigTool`, `ResearchTool`: likely substitutes for much of our hand-maintained action layer.
- `Watch`/supervised playback: distinguish presentation delays and colony-specific stop policies from native mechanics. Do not inherit stream-oriented behavior by default.

The user authorized local research copying on September 7. Selected observation sources are now integrated with attribution in `integrations/colony-bridge`; no upstream license grant is implied. See [companion analysis and implemented boundary](COMPANION_REUSE_ANALYSIS.md). Stock GABS/RimBridgeServer remain MIT. Commented bug histories are regression candidates, not proof of correctness on our game version.

### Execution sequence

1. State adapter and gameplay catalog: native schemas, typed observations, query names then fetch one schema. Keep native receipts in diagnostics and concise outcomes in the UI.
2. Native coverage: fix one-cell zone dispatch; verify materials, rotation, forbidden supplies, equipment/context menus, bills, priorities, zone filters. Prefer upstream tools; add an SDK companion only for demonstrated gaps. No arbitrary C#/Lua execution in gameplay catalog.
3. Execution/reconciliation: connect semantic goals to native actions; keep one writer/reservations; verify immediate effects immediately and queued jobs later. Accepted cells do not mean finished construction.
4. Controller/dashboard: switch one backend per session; migrate state, diagnostics, screenshots/video, and pause ownership. Retain RIMAPI rollback.
5. Eight-tribal comparison: same save/model; measure first useful order, pawn progress, storage, eight sleeping places, usable shelter and sustainable food. Remove RIMAPI only after success without player repair.

## Re-run

Close other RimWorld sessions. Install [RimBridgeServer v2.1.1](https://github.com/pardeike/RimBridgeServer/releases/tag/v2.1.1) into `Mods/RimBridgeServer`. Extract [GABS v1.1.1 Windows amd64](https://github.com/pardeike/GABS/releases/tag/v1.1.1) under `.rimbot/bridge/gabs`, or pass `--gabs`.

```powershell
.venv/Scripts/python.exe -m pip install -e '.[bridge]'
.venv/Scripts/python.exe scripts/prepare_bridge_trial.py --source-profile 'C:/Users/darch/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios' --rimworld 'C:/Program Files (x86)/Steam/steamapps/common/RimWorld'
.venv/Scripts/python.exe scripts/bridge_trial.py --start --load-fixture --placement-test
```

Preparation verifies the original fixture hash and copies to `.rimbot/bridge/profile`. Only the old RIMAPI game component and mod metadata are removed from the disposable copy. Normal saves/config stay untouched. Logs, saves, binaries and upstream source remain untracked.

Unfinished bespoke action code is preserved in RIMAPI's `checkpoint/native-actions-before-rimbridge` branch at `54132f4`. Active submodule and generated controller contracts are restored to their working revision. Patch backups: `.rimbot/migration-checkpoint`.

## Observation companion trial

Build/install while RimWorld is closed using `scripts/build_observation_bridge.ps1 -Install`. Prepare the isolated profile with `scripts/prepare_bridge_trial.py --observations` plus the same source/game arguments above. Run `scripts/bridge_trial.py --start --load-fixture --observations` to produce native receipts, discovered schemas and the generated typed summary. Add `--placement-test` for the previously verified native write smoke checks. The normal dashboard remains on RIMAPI in this slice.
