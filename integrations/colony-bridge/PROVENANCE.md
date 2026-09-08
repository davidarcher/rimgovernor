# Local research companion

The upstream C# source files in `src` were copied from
https://github.com/Snowstar38/rimworld-claude-harness/tree/89c2e90fedd51419a3db55a7f9865b0aef29b270/companion/src
for the user's explicitly requested local research integration. Original namespace,
comments and authorship context are retained. No license file was present in that
checkout; this document does not grant or imply redistribution rights.

The original slice compiled observations only. The sections below record the
subsequent gameplay and native clock additions; the Python gateway exposes only
the reviewed subset of the installed tools.
RimWorld and RimBridgeServer SDK assemblies are referenced, never bundled.

Any subsequent source modifications must be recorded here and tested in-game.

Local changes:
- Extracted `PawnSettingsRead` from PawnConfigTool.cs and `StockpileFilter` from
  ZoneCellsTool.cs as shared helper classes; no exported write tools included.
- Added `thingId = pawn.GetUniqueLoadID()` to every ListPawnsTool row. Upstream
  only provided an actionable pawn ID in the optional settings block.

## Gameplay slice and dashboard integration

This iteration also compiles upstream ZoneCellsTool, PlaceBuildingTool,
PawnConfigTool, BuildingConfigTool, BillsTool, OrderTool and Watch from the same
pinned source snapshot. The extracted PawnSettingsRead and StockpileFilter files
are excluded from compilation because the full upstream modules define them.
Python validates discovered schemas, restricts the callable gameplay surface,
requires explicit dryRun for companion writes, disables watch delays, and refuses
godMode. Native receipts and post-command observations are kept separately from
claims of completed pawn work.

The React dashboard borrows the upstream overlay's game/chat/short-summary layout
and separate long-term direction, adapted to our existing dashboard styles.
No Twitch credentials, hosted service, OBS setup or inline script is included.
The temporary standalone overlay is replaced by the dashboard on port 8787.

## Persistent projects and instruments audit

- Copied upstream `TradeTool.cs` from the same pinned snapshot unchanged. Python
  keeps map-trader adjacency, disables decorative watch delays, and classifies
  list/sheet/preview/status as inspection rather than transaction execution.
- `ColonyIdentity.cs` is local code: save-backed GameComponent identity plus a
  nonserialized load token. Attaches the component when bridge extension loading
  occurs after Verse has cached component types. Only controller metadata changes.
- Python `receipts.py` copies reason/_outcome/verdict_line from upstream
  instruments/build.py; preserves explicit native placement outcomes.
- The instruments audit identifies the upstream instant gear-drop path as a
  departure from ordinary pawn labor. The model gateway rejects that operation.

- Identity persistence lives in a separate normal mod assembly
  `src/identity/ColonyIdentity.csproj` (installed under Assemblies). The bridge
  extension only exposes its read tool. This fixes Verse's cached type lookup
  during save deserialization; the live save/reload smoke verifies identity
  continuity and load-token rotation.

## Native supervised clock

Copied `SupervisedPlayTool.cs`, `PlayUntilEventTool.cs` and `CombatInjuryHook.cs`
from the pinned snapshot above. The latter two provide shared watcher helpers;
the planner uses nonblocking supervision, not the short blocking waiter. Harmony
is referenced from the installed workshop mod, never bundled.

Local native policy changes:
- Added schema-visible `hostileWithin` (default 40 cells) to supervision. A hostile
  must be within that distance of a colonist to stop play; distant cave inhabitants
  alone are not a stop condition. This is proximity monitoring, not a combat risk
  assessment or permission to enter caves.
- A changed speed after a temporary force pause is never restored automatically.
- The watcher retires on map changes as well as game-instance changes.

Python owns the renewable 15-second lease, renewing every 3 seconds independently
of model inference. External pause/speed changes and watchdog failures require an
explicit player Automate selection before restart. Ordinary danger events go to
the planner while paused; combat mode and specific acknowledged IDs remain explicit
native options. No turn-clock budget or extra inference service is introduced.

Live validation: native lease expiry without heartbeat, external pause latch and
explicit resume, actual pawn movement, and runtime-owned undraft/pause cleanup.
Evidence is recorded locally in `.rimbot/bridge/clock-smoke.json` by
`scripts/native_clock_smoke.py`. This is not a live raid/combat test.

## Native weapon discovery

`home/list_things` adds `category=weapons`, filtered through RimWorld's weapon
flags, with `weapon.ranged` and `weapon.melee` on matching rows. Positions retain
native thing IDs. Equipped weapons remain in pawn equipment/resolve readback;
this item query does not count wielded equipment as loose supplies. Unknown
categories now fail explicitly instead of silently returning all haulables.

Native rescue observation additions: ListPawns now reports `carriedThingId` from
Pawn.carryTracker.CarriedThing and `health.bedThingId` from Pawn.CurrentBed(), using
native load IDs. These fields let the controller distinguish carried-pawn absence
from delivery into a bed. Existing native rescue job creation is unchanged.

## Native research integration

Copied ResearchTool.cs unchanged from the same pinned upstream companion revision
89c2e90fedd51419a3db55a7f9865b0aef29b270. It queries the native research database,
requirements, benches and researchers, and selects via ResearchManager. No research
completion/progress cheat is exposed. Upstream notes describe the native zero-value
progress dictionary insertions that prerequisite queries can trigger; these are
not awarded research points. Gateway enforces Manual/dry-run classification,
refused write handling, no watched UI and fresh selection readback.

Live headless validation selected ComplexFurniture, confirmed current project,
replayed as a no-op, and rejected an unknown project. No research points added.
