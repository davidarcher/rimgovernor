# Local research companion

The upstream C# source files in `src` were copied from
https://github.com/Snowstar38/rimworld-claude-harness/tree/89c2e90fedd51419a3db55a7f9865b0aef29b270/companion/src
for the user's explicitly requested local research integration. Original namespace
and upstream authorship are retained. No license file was present in that
checkout; this document does not grant or imply redistribution rights.

The original slice compiled observations only. The sections below record the
subsequent gameplay and native clock additions; the Python gateway exposes only
the reviewed subset of the installed tools.
RimWorld and RimBridgeServer SDK assemblies are referenced, never bundled.

Any subsequent source modifications must be recorded here and tested in-game.

Local changes:
- `RecoveryTools.cs`, `RecoveryAreaOwnership.cs` and `identity/RecoveryAreas.cs`
  are original local recovery observation, guarded native WorkGiver dispatch and
  bounded allowed-area ownership code. `ColonyFactsTool.cs` adds native condition
  identities, building service state and allowed-area-aware stock access.
  `DisasterFixture.cs` supplies optional disposable condition, damage and crop-loss
  inputs; normal builds and the model gateway exclude its tools. Recovery labor
  remains native; no game source or assemblies are bundled.
- `WasteTools.cs` is original local waste census and guarded hauling code. It
  discovers hauling WorkGivers and retains their jobs, using the same native
  scanner eligibility pattern documented in the attributed `OrderTool.cs`.
  `WasteFixture.cs` is optional disposable setup, excluded from production builds.
- `TradeTool.cs` exposes native definition-based export protection and accepts
  economic stock floors. Policy acceptance checks actual remaining stack counts
  and rejects protected exports in the native exchange's main-thread operation.
- `PawnImageTool.cs` is original local presentation code. Its offscreen render
  timing and culling design were informed by local inspection of RimMolt's
  `ScreenshotTools`, `Game_UpdatePlay_Patch` and `CameraDriver_CurrentViewRect_Patch`;
  no source was copied. It references RimWorld's `PortraitsCache` and native weapon
  icons, without bundling game assets. `ListPawnsTool.cs` also exposes the current
  job driver's display report. The dedicated pawn image probe records native
  rendering and camera/selection invariance separately from controller fixtures.

- `ResearchTool.cs` adds exact ThingDef/RecipeDef prerequisite and availability
  reads and a main-thread expected-current guard for ordinary research selection.
  Research progress remains owned by normal native pawn work.
- `GearUpkeepTool.cs` is original local code that calls installed native apparel
  scoring, eligibility and ordinary Wear/Equip jobs. It adds durable weapon
  ownership and read-only gear/production candidates to `ColonyFactsTool.cs`.
  `scripts/fixtures/GearFixture.cs` is optional disposable setup, excluded from
  default builds and the model gateway. Game assemblies and decompiled inspection
  artifacts are not distributed with this source.
- `PopulationTool.cs` is original local observation and guarded prisoner-setting code.
  `OrderTool.cs` adds capture through installed RimWorld 1.6
  `FloatMenuOptionProvider_CapturePawn`, `HealthAIUtility.CanRescueNow` and
  `RestUtility.FindBedFor` contracts. Prisoner settings follow
  `ITab_Pawn_Visitor` eligibility and `Pawn_GuestTracker.SetExclusiveInteraction`.
  No native game source or assemblies are bundled. `PopulationFixture` is optional
  disposable starting-state setup, excluded from production/model access; actual
  custody, care and recruitment remain normal native pawn work.
- `scripts/fixtures/FoodObservationFixture.cs` is optional original read-only test
  code. It observes native ingestion and stack splits and retains food references
  for rot readback after destruction. It does not spawn, age or move food, alter
  temperatures, change pawn behavior or advance time. Default builds exclude it.
- `ForecastFacts.cs` is original local read-only animal-feed, crop-work and
  medical/mood input accounting. `FoodSupplyFacts.cs` records native eater policy,
  diet and access eligibility; `ColonyFactsTool.cs` exposes these inputs.
  `ListBuildingsTool.cs` preserves unavailable aggregate watts instead of zero.
  The optional `ForecastFixture` compile flag includes only disposable test setup;
  production builds and the model gateway exclude its tools.
- `scripts/fixtures/ConstructionLedgerFixture.cs` is optional original test code.
  Harmony prefixes/postfixes observe native Frame completion/failure and resource
  counts. The native methods run unchanged; no resources, pawn skills or outcomes
  are written. Default builds exclude this fixture and model execution cannot
  access its `test/` tool.
- `ZoneSettingsContract.cs` reads exact native ThingFilter definition/special-filter
  allowances and hit-point, quality and mental-break ranges without display
  truncation. `ZoneCellsTool` includes that contract in filter previews/readbacks
  and exposes ordinary growing-zone sow/cut settings with explicit dry runs.
  `ZonesTool` preserves unavailable sow/cut reads as null. `ListBuildingsTool`
  exposes invariant native Rot4 integers alongside localized facing labels.
- `TradeTool.cs` binds transactions to the exact native session, participants,
  map and load; requires ordinary paused adjacency; and checks preview signatures,
  current stock eligibility and both parties' funds before acceptance. Direct
  orbital opening is refused in favor of the normal comms-console input path.
  Its introductory commentary is condensed; the upstream source and authorship
  remain attributed to the pinned research companion above.
- `scripts/fixtures/TradeFixture.cs` is original local test-only code. It requests
  native caravan/visitor incidents, orders ordinary trade/dismiss jobs, applies
  the native Home-area clear designator and reads
  actual stock. It is excluded from production builds and model execution.
- `PlaceBuildingTool.cs` reports completed building/terrain passability and native
  door class for projected spatial validation, including custom definitions.
  The metadata read does not place or complete construction.
- `SpatialAccessTool.cs` is original read-only local code. It compares current
  and projected four-neighbor access using native walkability, danger, pawn areas
  and door eligibility, and separately calls native pawn reachability for targets
  and any required footprint egress. It never changes the path grid or simulation.
- `ColonyFactsTool.cs` exposes native crop fertility response, current sowing season,
  remaining seasonal temperature window, fresh animal corpse identities and native
  cooking recipe products, shelf life and bill targets. Product-specific production
  demand includes native animal diets and food policies. These are observations;
  future crop, weather and preservation outcomes require ordinary pawn validation.
- `OrderTool.cs` verifies immediate Equip completion by exact primary-weapon
  identity, in addition to its existing current-job check. A different weapon
  cannot satisfy the receipt.
- `HuntingSafety.cs` is original local code. It reads available hunters and native
  `PathFinder.FindPathNow` routes with `Danger.None`, screening path cells against
  observed wild predators. `ListPawnsTool.cs` exposes that evidence and
  `SupervisedPlayTool.cs` pauses active hunting when a fresh route fails screening.
  No hunting designation, pawn job or path is changed by these reads.
- `VideoStreamTool.cs` is original local presentation code. It captures the Unity
  framebuffer after rendering into a leased RGB24 shared-memory slot on Windows.
  It does not issue input, alter simulation speed or expose editor operations.
- `scripts/fixtures/ScenarioStartFixture.cs` is an optional test-build setup hook.
  Native `ScenPart_ConfigPage_ConfigureStartingPawns.DoEditInterface` supplies the
  1..10 count bounds; the hook changes a `Scenario.CopyForEditing` copy and follows
  `Root_Play.SetupForQuickTestPlay` lifecycle calls with the selected world seed.
  RimWorld generates all pawns, supplies and terrain. The default scenario defs
  remain unchanged. No game assembly or decompiled source file is distributed.
  Optional biome selection chooses a generated surface tile accepted by native
  settlement validation before map generation; it does not edit an existing map.
  The fixture is absent from normal builds and rejects existing colonies.
- `FoodSupplyFacts.cs` reads native fed consumption, individual held food,
  `FoodUtility.NutritionForEater` and `CompRottable.TicksUntilRotAtCurrentTemp`.
  `ColonyFactsTool.cs` includes these observations and native wild-plant nutrition
  yield/pending harvest totals. These reads do not manufacture food, change rot
  progress, transfer inventory or issue harvest orders.
- `SupervisedPlayTool.cs` accepts an optional ordinary-game tick budget and
  pauses from a Harmony postfix on `TickManager.DoSingleTick`. The native
  `TickManagerUpdate` loop checks `Paused` after each tick. Heartbeat renewal
  leaves the deadline unchanged; the existing frame watcher still handles
  danger, long events, external holds and session changes.
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

Local `DraftOwnership.cs` instruments the native draft setter with ephemeral
controller claims. The adapted `OrderTool` acquires claims only on new draft
transitions, checks ownership atomically before release, and optionally refuses
attacks on downed targets. Ordinary job construction and native eligibility remain
upstream-derived. The separate local `scripts/fixtures/CombatFixture.cs` uses native
incident APIs only for disposable acceptance setup; it is not production tooling.
The local supervised-play guard excludes downed/dead enemies from active hostile
pauses, matching its existing conscious-hostile clearance event. Standing enemies
and colonist injury thresholds remain guarded.

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
  occurs after Verse has cached component types. Disposable launches with
  `-rimbot-pause-on-load` also select ordinary Paused speed in the loaded-game
  callback, before simulation advances. Saved ticks and game contents are not edited;
  launches without the flag retain native load behavior.
- Python `receipts.py` copies reason/_outcome/verdict_line from upstream
  instruments/build.py; preserves explicit native placement outcomes and accepts
  the native `reason` field for guarded recovery refusal diagnostics.
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

## World context

WorldTool.cs was copied from pinned upstream revision
89c2e90fedd51419a3db55a7f9865b0aef29b270. Local change: skip PlayerRelationKind
and PlayerGoodwill for the player faction itself; those native getters logged
GetSituations errors during the live test. Return null for these inapplicable
fields while retaining isPlayer. Python exposes reads only and rejects show:true.
Corrected live headless read/filter/no-view checks passed.

## Spatial query migration

CellsPlusTool.cs copied unchanged from pinned companion revision
89c2e90fedd51419a3db55a7f9865b0aef29b270. Exposed as an on-demand read for the
strategist and scout. Native rectangular/filter/sparse/summary and refusal tests
passed in the headless baseline; no changes to native execution or geometry.

## Pawn settings identity compatibility

PawnConfig's exact-ID resolver now compares both ThingID and GetUniqueLoadID().
Our ListPawns top-level identity uses the latter; the copied resolver previously
accepted only the former and rejected valid observed pawn IDs. The same resolver
serves master selection. Native name ambiguity handling is unchanged. A real
local-model commitment changed selfTend using the observed load ID, with paused
native readback confirming the result.

## Naming-dialog reuse

Copied `DialogTextTool.cs` from pinned companion revision
`89c2e90fedd51419a3db55a7f9865b0aef29b270`. Local changes require the observed
window ID and an exact field name; removed partial/default field selection.
Writes are limited to reviewed naming inputs (`curName`, `curSecondName`, and
editable Dialog_NamePawn name-context rows), while other dialogs are read-only.
All validation and writing remain in the upstream single main-thread dispatch.
Listings add `windowId` and `writable`; the latter identifies a supported naming
dialog, not a guarantee that every reflected string field is an input.

Live headless acceptance: opened a disposable Dialog_NamePlayerFaction, rejected
wrong window ID and partial field, changed `curName`, and verified a fresh field
listing while paused. Final rename acceptance and other naming dialog types are
not covered by that fixture. No game-setting or simulation patches were added.

## Packed furniture installation

`InstallTool.cs` is a local native equivalent of the inspected
`instruments/mini_install.py` workflow from the pinned companion revision.
It retains exact-item validation, destination validation and observed completion,
but does not copy its camera moves, English gizmo matching or repeated clicks.
The installed game's `Designator_Install` implementation was inspected: the tool
uses its `GenConstruct.CanPlaceBlueprintAt`, `GenSpawn.WipeExistingThings` with
Deconstruct mode, and `GenConstruct.PlaceBlueprintForInstall` calls. No global
selection or designator state is changed. Conflicting existing installation orders
are refused rather than silently cancelled.

`Blueprint_Install.ThingToInstall` identifies the existing building; native
installation spawns that same object. Project tracking uses its inner load ID,
destination and rotation, not a same-definition replacement building. No forced
completion, resource injection or altered pawn work exists in the gameplay tool.

`scripts/test_install.ps1` temporarily compiles the separately gated
`scripts/fixtures/InstallFixture.cs`, creates a packed bed in an isolated test
profile, and enables construction for capable fixture pawns. Normal pawn labor
must finish the installation. It restores the prior DLL and then installs the
normal build without fixture tools. Live tests passed north/east installation,
same-order replay, conflicting-destination refusal and invalid ID/cell refusal.
This proves native execution, not autonomous model furniture selection.

`ThermalSides.cs` reports rotation-dependent temperature-exchange cells from the
installed game's `Building_Cooler.TickRare` and
`GenTemperature.EqualizeTemperaturesThroughBuilding` contracts. Cooler south is
intake and north is exhaust before rotation; vents exchange across both facing
sides of each occupied cell. Only the exact native cooler/vent classes are
recognized; custom subclasses remain unsupported. Fogged or out-of-bounds cell
passability is null. The report reads geometry without invoking temperature
updates and makes no capacity or completed-cooling claim.

`InspectorFixture.cs` is excluded unless `InspectorFixture=true` is passed to the
build. It creates isolated thermal, bill, battery and stockpile test objects for
`scripts/inspector_acceptance.py --fixture`; fixture creation is not gameplay.

Naming input recognizes the native `Dialog_Rename<T>`, `Dialog_GiveName` and
`Dialog_NamePawn` families. It reads their actual input-length limits and excludes
non-editable pawn name contexts. Native `DoWindowContents` performs validation and
the final game-object write, so confirmation uses an exact captured UI control;
the accept-key shortcut is refused before changing text. Window IDs and fresh
layout targets retain native stale-dialog protections.

`ModalFixture.cs` is excluded unless `ModalFixture=true` is passed to the build.
It instantiates native naming dialogs, supplies a two-branch native quest and
reads affected game objects for `scripts/modal_acceptance.py`. Fixture setup is
separate from the naming/quest actions, which use the normal UI confirmation path.

`PawnConfigTool.cs` marks pawn identity required in SDK discovery, matching the
native resolver's refusal of an empty target. Execution cannot rely on a selected
pawn or silently substitute a target.


Campaign metrics acceptance uses the original test-only
`scripts/fixtures/CampaignMetricsFixture.cs`, included only with
`CampaignMetricsFixture=true`. It clears a disposable patch, places a roofed
room and sleeping spots, prepares rice/wood and storage, and initializes needs,
bed assignments and schedules. The fixture does not complete the observed pawn
work: sleeping, hauling, the committed wall and deconstruction run through normal
jobs. Production builds exclude this capability. The acceptance harness preserves
native snapshots and failed trials separately from unit-test evidence.


Deterministic control reads in `ColonyFactsTool.cs` use native nutrition, food
eligibility, work tables, crop growth, resource definitions and construction
costs. `ZoneCellsTool.cs` accepts a crop during growing-zone creation: the dry run
resolves ground-sowable definitions and checks the accepted cells using the same
pollution conditions as native `PollutionUtility.CanPlantAt`, without allocating
a zone ID. Creation uses `SetPlantDefToGrow` and returns the observed definition.
Status and supervised play share prey ownership for predator threat detection;
known wildlife hunts are ignored, while unreadable prey retains a danger hold.


`ColonyNamingTool.cs` follows the initial `Dialog_GiveName` OK branch for the
specific `Dialog_NamePlayerFactionAndSettlement` dialog. It checks the exact
observed window and suggestions, invokes that dialog's native `IsValidName`,
`IsValidSecondName`, `Named` and `NamedSecond` callbacks, posts the ordinary
completion message and removes the dialog. It verifies faction/settlement names
and closure; it does not use the ineffective accept-key shortcut or generic
window-field writes. The supervisor distinguishes this known bootstrap request
from unrelated forced dialogs.

`CancelConstructionTool.cs` follows the blueprint/frame branch of native
`RimWorld.Designator_Cancel.DesignateThing`: `Thing.Destroy(DestroyMode.Cancel)`
owns resource refunds and construction cleanup. The companion narrows admission
to an exact player `Blueprint_Build` or `Frame`, verifies saved colony/map/load,
material, build definition and position while paused, and observes removal in the
same main-thread callback. It does not use cell-wide cancellation, which can also
remove unrelated designations, or cancel completed buildings and installation
blueprints. Blueprint refusal/removal and cancellation of an ordinarily built
partial wooden-bed frame have native acceptance. The frame's 45 delivered wood
returned to spawned stock at an unchanged game tick; this is a bounded refund
case, not coverage of every material, partial delivery or modded frame.

The separate `scripts/fixtures/InterruptionFixtures.csproj` acceptance assembly
delivers disposable letters through `LetterMaker.MakeLetter` and
`LetterStack.ReceiveLetter`, following vanilla automatic-pause preferences.
Subsequent same-frame `TickManager.Pause` and speed-setting calls test source
precedence; actual keyboard input is checked separately. The assembly is excluded
from production builds and the model gameplay capability list. It does not edit
resources, save contents or production supervisor code. Its separate joining
scenario tool discovers the native `WandererJoin` incident, requires
`CanFireNow`, and invokes `TryExecute` using current storyteller parameters.
RimWorld owns pawn generation, entry-cell legality, relationships and the joining
letter; the fixture records roster IDs without directly creating or editing pawns.

Resource policies use native recipe quantities, ingredient selection and recipe
completion to enforce ordinary production budgets. Original RimBot guard code
wraps these callbacks without copying game implementation or replacing bill
settings. Exact-identity acquisition invokes the normal mining/plant designators.
The identity assembly stores resource policy metadata beside colony identity;
transient controller construction holds are excluded from native saves.

Wild plant and mature tree acquisition uses the native harvest and harvest-wood
designators. Both generate HarvestPlant work consumed by WorkGiver_PlantsCut;
source metadata therefore requests PlantCutting, while mining requests Mining.
The source list and final dispatch both honor native designation eligibility.

Special stockpile filters enumerate native configurable SpecialThingFilterDef
definitions and call ThingFilter.SetAllow on live or detached preview filters.
This original extension uses the native settings API and copies no game source.

The native clock event journal is original RimBot code. It retains typed event
payloads and colony/map/load identity in immutable XML rows under the private
profile, publishes flushed rows by rename and recovers complete staged rows.
It does not serialize or edit simulation state. Journal failures pause supervised
play and surface an explicit failure instead of silently discarding history.

HusbandryTools is an original extension using native animal settings, training,
slaughter eligibility, enclosed pen lookup and pawn reachability APIs. It preserves
normal simulation rules and copies no game implementation. The existing attributed
PawnSettingsRead supplies animal training and product observations. HusbandryFixture
is a separately enabled test-only prerequisite builder, absent from production DLLs.
