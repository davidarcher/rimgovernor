# Static-data census: native reads and observation fields

Census for #2641 under epic #2621. Every `Native*.cs` and helper file under
`integrations/rimgovernor-native/src/Bridge/Protocol/` (203 files) and every top-level message and enum in
`contracts/proto/observations.proto` (499 messages, 31 enums) is classified. Nothing is deleted here;
deletions are the child issues listed in [Clusters](#clusters).

Classes:

- **state**: per-frame or per-world game state, or an operation acting on it.
- **code**: computed by game logic; it cannot become a mirrored def row.
- **static**: copies def data or game constants the mirror carries (or will carry, #2626).
- **mixed**: both; the static part is named.
- **excluded**: Biotech (#2630), Anomaly (#2631), Odyssey and Race (#2632) already have children.

Replacements name a `defs.proto` field (`Message.field`), a planned `GameConstants` entry (#2626), or a Go rule over mirrored rows. Classification was by reading each file or message with its producer; large files were skimmed for def reads and constants, so a mixed file may hide a small static item.

## Totals

| Class | Native files | Proto messages and enums |
|---|---|---|
| state | 105 | 309 |
| code | 53 | 57 |
| static | 3 | 16 |
| mixed | 35 | 56 |
| excluded | 7 | 92 |
| total | 203 | 530 |

## Clusters

Static and mixed findings, grouped into one child issue each (all labelled `area:G01`, `area:N01`, `area:simplify`, linked from #2621):

| Issue | Cluster | Replacement |
|---|---|---|
| #2652 | Game constants hardcoded in native (trade, diplomacy, hive, temperature, danger) | `GameConstants` (#2626/#2628) |
| #2653 | Cost, work, hit-point, value stats copied from `ThingDef` | `ThingDef.costList`, `statBases`, `stackLimit` via Go rules |
| #2654 | Research, recipe and gear catalogs | `ResearchProjectDef`, `RecipeDef` fields |
| #2655 | Verb, food, plant and race facts | `ThingDef.verbs`, `ingestible`, `plant`, `race` |
| #2656 | Health, royalty, psycast, prisoner and site-part flags | `HediffDef`, `RoyalTitleDef`, `AbilityDef`, `PrisonerInteractionModeDef`, `SitePartDef` |
| #2657 | Label, enum and catalog echoes | `XDef.label`, `defs.proto` enums, `DefSets` |

Out of scope and unchanged: files classed state or code, and the excluded Biotech, Anomaly, Odyssey and Race items (#2630, #2631, #2632).

## Native files

| File | Lines | Class | What it does | Static items | Replacement |
|---|---|---|---|---|---|
| CellGridCache.cs | 81 | state | Per-map cache of walkability, doorway, empty storage, terrain light, terrain/foundation names and room per cell, invalidated by map events. |  |  |
| CellGridEncoder.cs | 374 | mixed | Reads the whole-map cell grid each frame and encodes keyframe/delta arrays; per-cell terrain light flag and foundation affordance list are def data. | terrain light (terrain.affordances contains TerrainAffordanceDefOf.Light) and foundation affordances string join (baseTerrain.affordances) per terrain def | TerrainDef.affordances (Go derives supports_light and foundation_affordances from terrain row by terrain name); about 2 expressions |
| CellGridThings.cs | 199 | state | Reads the non-pawn things on each cell into ThingRec with flags (edifice, impassable, haulable, deconstructible, forbidden, designated) for the grid. | flags from def: passability impassable, holdsRoof, Minifiable, EverHaulable, building.isNaturalRock (copied per thing) | ThingDef.passability / holds_roof / minifiable / ever_haulable / building.is_natural_rock (Go could derive these four flag bits from the def row via thing def name) |
| DefMirrorFill.cs | 304 | code | Reflection-based filler that mirrors game def objects into defs.proto messages from clr_type options. |  |  |
| DeterministicGenSeed.cs | 61 | code | Harmony patch replacing per-process-salted HashCode.Combine map-gen seeds with a fixed hash. |  |  |
| GovernorStatusPanel.cs | 91 | state | Draws the in-game bot connection label from authority state. | layout constants Margin Pad Height (UI) |  |
| Guards/NativeDesignationGuards.cs | 300 | code | Registers the enclosure, mine-safety, acquisition, wall-upgrade and wastepack designation guards and the Harmony job hooks that re-check them. |  | none |
| MainThreadAdmission.cs | 262 | state | Orders main-thread hops by admission class (control before observation) under a per-frame allowance. | DefaultAllowanceMs=4.0 StaleMs=250 (tuning) |  |
| MainThreadWatchdog.cs | 117 | state | Background timer that logs main-thread hops that stalled queued or running. | StallSeconds SlowMillis (tuning) |  |
| NativeAbilityOperations.cs | 390 | code | AbilityIntent handler: guards and uses a royal permit or ability via the game worker validation and favor/cooldown checks. |  |  |
| NativeAcquisitionToken.cs | 31 | state | Hash token for acquisition snapshots covering resource, cell, harvestability and designation. |  |  |
| NativeActionDispatch.cs | 312 | state | Maps every Action arm to a handler and keeps a bounded replay LRU of results per idempotency key. | handler registry; ReplayCapacity=256 |  |
| NativeAnomalyColony.cs | 112 | excluded | Anomaly colony facts section. |  |  |
| NativeAnomalyFacts.cs | 327 | excluded | Anomaly catalog and per-pawn/building row blocks. |  |  |
| NativeApparelPolicyOperations.cs | 135 | mixed | Reads and writes a pawn outfit and wear requirements (title, role, precepts); census of apparel defs filtered by ApparelMeetsRequirement. | apparelDefs census list (ThingDef.IsApparel) and work type list copied per pawn (DefDatabase<WorkTypeDef>.AllDefs) | ThingDef.apparel (row set where is_apparel) and WorkTypeDef rows (Go can enumerate); requirement matching stays code |
| NativeApplyPreconditions.cs | 67 | code | Apply-time precondition rule runner ending in snapshot token comparison. |  |  |
| NativeAreaIntent.cs | 108 | state | Creates, edits or deletes a bot-owned allowed area or edits the home area. |  |  |
| NativeAreaPlantCut.cs | 101 | code | Designates non-crop non-forage plants on cells for cutting or tree chopping via game designators. |  |  |
| NativeArrestOperations.cs | 67 | state | GiveJobIntent Arrest: gives the arrester the Arrest job onto an exact prisoner bed after live checks. |  |  |
| NativeAssignOperations.cs | 124 | state | AssignIntent: assigns one colonist ownership of a bed/throne/grave via CompAssignableToPawn. |  |  |
| NativeAttemptLedger.cs | 263 | state | Unsaved per-colony ledger of admitted attempts with identity comparison. | Capacity=4096 (bridge tuning) |  |
| NativeAuthorityControlTools.cs | 148 | state | Tool that sets authority mode for the player-control owner. |  |  |
| NativeAuthorityTools.cs | 136 | state | Tool projecting current authority status, revocation reason and generation. |  |  |
| NativeAutoHomeArea.cs | 41 | state | Sets the save-level auto home area play setting. |  |  |
| NativeAutoRefuel.cs | 47 | state | Toggles CompRefuelable.allowAutoRefuel on one player building. |  |  |
| NativeBedUse.cs | 112 | state | Writes bed medical, prisoner and slave flags on a building after live guard checks. |  |  |
| NativeBillConfiguration.cs | 21 | state | Binary hash input name annotations for bill configuration tokens. |  |  |
| NativeBillsObservationTools.cs | 152 | mixed | Read-only bench census with bill stacks and per-recipe availability; recipes read lists bench.def.AllRecipes and finds the serving work type. | Capacity=BillStack.MaxCount (game constant); bench recipe list (bench.def.AllRecipes); WorkType() scan of WorkGiverDef for the bench def | GameConstants BillStack.MaxCount (#2626); ThingDef.all_recipes and RecipeDef.recipe_users; WorkGiverDef.fixed_bill_giver_defs / WorkGiverDef.work_type for WorkType() |
| NativeBiotechColony.cs | 206 | excluded | Biotech colony facts section. |  |  |
| NativeBiotechFacts.cs | 302 | excluded | Biotech catalog and per-pawn row block. |  |  |
| NativeBuildingObservationTools.cs | 286 | mixed | Lists buildings, blueprints and frames with construction cost deficits, service/power/fuel state and settings snapshots; fuel allowed defs and work-to-build are def reads. | AllowedFuelDefs from CompProperties_Refuelable.fuelFilter; TotalWork from StatDefOf.WorkToBuild on the def; source thing request groups list | CompProperties_Refuelable.fuel_filter (ThingFilter allowed_defs) and stat WorkToBuild stat_bases in ThingDef; costs ThingDef.cost_list |
| NativeBuildingTemperature.cs | 121 | mixed | BuildingPatchIntent temperature arm and the patch action router; writes CompTempControl.targetTemperature after range check. | MinCelsius=-273.15 MaxCelsius=1000 (interface range) | GameConstants temperature interface range (#2626); or CompProperties_TempControl.min/max if present |
| NativeCaravanCatalog.cs | 37 | code | Reflection wrapper calling the private Dialog_FormCaravan calculation without opening the window. |  |  |
| NativeCaravanOperations.cs | 246 | code | FormCaravanIntent handler: builds the dialog, selects crew and cargo, runs the game form/reform caravan call. |  |  |
| NativeChoiceDialogOperations.cs | 50 | state | DialogIntent: activates one exact option of the open force-pausing choice dialog. |  |  |
| NativeClaimBuilding.cs | 92 | state | BuildingPatchIntent claim arm: eligibility check, CAS token and SetFaction on a live building. |  |  |
| NativeCleanOperations.cs | 75 | state | GiveJob Clean order on one exact filth using WorkGiver_CleanFilth rules. |  |  |
| NativeClearCover.cs | 47 | state | Classifies a map thing as plant/mineable/chunk/building cover and reads standing removal designations. | Mine/CutPlant/Haul/Deconstruct const strings (4 entries) | none needed; designation names are protocol tokens |
| NativeClearanceObservationTools.cs | 428 | mixed | Clearance target census with salvage evidence cache, ancient-danger and hive-danger checks; mostly live state. | Classify() ThingDefOf.ShipChunk/Wall checks (3 defs); HiveDangerRadius=14; salvageSignatureEveryTicks=60 | Go rule over ThingDef rows (ShipChunk/Wall by defName/thingClass); HiveDangerRadius into GameConstants (#2626) |
| NativeClockEventProjection.cs | 212 | code | Projects clock-journal event rows into typed Clock.Event protos; clock transport infrastructure. |  |  |
| NativeClockRuntime.cs | 549 | state | Clock epoch runtime: leases, speed, pause holds, event journal pages and authority. |  |  |
| NativeClockTools.cs | 161 | state | Tool handlers for clock start/renew/change-speed/read-events. |  |  |
| NativeColonyObservationTools.cs | 558 | mixed | Reads the whole colony facts frame (food, power, wealth, crops, conditions) from live game state. | GameRoomRoles (4 ThingDefOf checks: BabyDecoration/Blackboard/SchoolDesk/ToyBox); conduit defName list (PowerConduit/HiddenConduit/WaterproofConduit); CropDefs DefDatabase enumeration | room roles and conduits: Go rule over ThingDef rows (defName/thingClass/comps in defs.proto ThingDef); CropDefs: Go filter over ThingDef.plant |
| NativeCombatGeometryTools.cs | 234 | code | Combat geometry read: cover, line of fire and path ticks via CoverUtility/GenSight/pathfinder. |  |  |
| NativeCombatOperations.cs | 47 | code | Attack rules: picks melee or ranged attack job per vanilla float-menu logic. |  |  |
| NativeCombatOrders.cs | 488 | state | Applies a batch of combat micro orders (draft, goto, attack, rescue, repair, mortar, animal orders). |  |  |
| NativeCommsTradeRequests.cs | 122 | mixed | Executes trader requests through the real comms console job with eligibility checks. | goodwill cost -30/-15 and cooldown 900000/240000 ticks hardcoded for orbital vs caravan requests | GameConstants (#2626): trader request goodwill costs and cooldown ticks |
| NativeComplexSecurity.cs | 98 | code | Computes ancient complex security threat cost from layout threats via reflected vanilla ranges and DefDatabase maxima. |  |  |
| NativeConstruction.cs | 119 | state | Native factory scope placing blueprints, frames and floors through ordinary architect behavior. |  |  |
| NativeConstructionCausality.cs | 38 | code | Tracks exact created and spawned objects within a synchronous construction scope. |  |  |
| NativeConstructionHookSet.cs | 36 | code | Helper identifying declared MethodInfo implementations for Harmony hook sets. |  |  |
| NativeConsumptionTool.cs | 45 | state | Reads realized hourly consumption ledger entries. |  |  |
| NativeCustodyOperations.cs | 141 | state | GiveJob Capture/Rescue and entity holding-platform carry orders with vanilla gates. |  |  |
| NativeCutPlant.cs | 76 | state | CutPlant designation on one exact plant with apply-time preconditions. | DesignationDef and Kind const strings | none needed |
| NativeDeepResources.cs | 87 | state | Reads deep drill and scanner state and deposit progress via reflection. |  |  |
| NativeDefenseObservationTools.cs | 171 | code | Line-of-fire and edge-reachability reads using CoverUtility/GenSight/pathing. | MaximumLineCells=64 request cap | none (request limit) |
| NativeDefenseStats.cs | 51 | code | Computes ranged and turret DPS from verbs and stats. |  |  |
| NativeDefinitionCatalogTool.cs | 308 | code | The def mirror emitter itself: reflects every ThingDef/TerrainDef/other def into protobuf rows plus per-stuff stat values. | PlannerStats set (1 entry DeteriorationRate) | this is the producer of the defs.proto rows; PlannerStats stays as the extra-stat rule |
| NativeDeliveryLedger.cs | 220 | state | Cumulative production delivery counters latched by Harmony hooks at harvest, fish, gather, kill and butcher. |  |  |
| NativeDesignate.cs | 316 | state | Generic Designate operation placing removal designations through the game's designators. |  |  |
| NativeDraftProtocol.cs | 32 | code | Identity and token validation helpers for draft-type protocol messages. |  |  |
| NativeDropOperations.cs | 78 | state | GiveJob DropWeapon found by driver class JobDriver_DropEquipment. |  |  |
| NativeDrugPolicy.cs | 103 | state | Creates or edits a named DrugPolicy to carry exactly the given entries. |  |  |
| NativeEnums.Clock.cs | 18 | code | RimWorld-free clock pause-hold name mapping. |  |  |
| NativeEnums.cs | 167 | code | Maps vanilla enums and BillRepeatModeDefOf values onto proto enums. |  |  |
| NativeEquipOperations.cs | 69 | state | GiveJob Equip following FloatMenuOptionProvider_Equip gates. |  |  |
| NativeExcavationSite.cs | 91 | code | Certifies a rock cell set for mining via reachability and support counterfactual. |  |  |
| NativeFloorRemoval.cs | 56 | state | RemoveFloor designation on one constructed-floor cell. | DesignationDef RemoveFloor const | none needed |
| NativeFoodChannels.cs | 187 | state | Reads livestock food channels (slaughter, milk, eggs, fishing water, feeder nutrition) from live pens and pawns. |  |  |
| NativeFoodPolicy.cs | 137 | mixed | Food policy apply and read, plus per-ThingDef food classification (Kind, Facts) emitted into the def catalog. | Kind(ThingDef) preferability/foodType/IsMeat/IsFungus/IsAnimalProduct to Obs.FoodKind; Facts() ThingDefFacts (RawMeat, Medicine, MealIngredients via FoodUtility.GetFoodKind, FoodKind); IsFood predicate; Foods() DefDatabase enumeration; Eaters() babiesCanIngest rule | Go rule over ThingDef rows: ThingDef.ingestible.preferability, ingestible.foodType, ingestible.babiesCanIngest plus IsMeat/IsMedicine derivation; MealIngredients from RecipeDef ingredient filters (FoodKind enum in defs.proto) |
| NativeGathering.cs | 93 | state | GatheringIntent apply: starts a vanilla gathering via GatheringDef.Worker.TryExecute and records the spot. |  |  |
| NativeGearFacts.cs | 304 | mixed | Gear census per colonist (loadout, candidates, storage, climate, equipment) with a loadout model whose option catalog is built from RecipeDefs. | Catalog() producible-apparel recipes (RecipeDef.ProducedThingDef IsApparel, one per def); BillOption research prereqs and CostListAdjusted ingredients; Option() Smokepop verb check (Verb_SmokePop) | Go rule over rows: RecipeDef.products + ThingDef.apparel + RecipeDef.researchPrerequisite/researchPrerequisites + ThingDef.costList/stuffCategories; ThingDef.verbs[].verbClass for smokepop; caps are tool constants not game constants |
| NativeGearOperations.cs | 58 | state | Wear order: validates pawn and apparel live and issues JobDefOf.Wear as ordered work. |  |  |
| NativeGiveItemOperations.cs | 83 | mixed | GiveItem order: hauler delivers an item to a recipient waiting on a lord toil request, validated live. | Allowed(def) hardcoded 5 defNames: Silver, MedicineHerbal, MedicineIndustrial, Penoxycyline, Beer | Go-side policy list or ThingDef rows filtered by the quest request defs; not a game constant |
| NativeGiveJob.cs | 256 | code | GiveJobIntent dispatcher: classifies intent into order/need/use/prioritized arms and applies prioritized work-giver jobs. |  | Kinds job-name to JobOrderKind map (17 entries) is a dispatch table, not def data |
| NativeGravEngineInspectionOperations.cs | 49 | state | Orders a colonist to inspect the grav engine with JobDefOf.InspectGravEngine. |  |  |
| NativeGrowerCrop.cs | 90 | state | BuildingPatchIntent plant_def arm: sets a plant grower's crop with sowable and research checks and CAS token. |  | Sowable() duplicates PlantProperties.sowTags/sowResearchPrerequisites; live check stays native |
| NativeHackDesignationOperations.cs | 46 | state | Toggles CompHackable autohack via the native gizmo toggle. |  |  |
| NativeHaulOperations.cs | 102 | state | HaulIntent: orders a colonist to haul an item through the real Hauling WorkGiver. |  |  |
| NativeHuntAcquisition.cs | 220 | mixed | Hunt acquisition census (prey rows, hunter and butcher-bench facts, route safety) plus hunt apply. | ProjectileKind/VerbFacts/WeaponFacts copy VerbProperties (range, warmupTime, ai_IsWeapon, IsMelee, defaultProjectile damageDef, explosionRadius, workerClass); PestRace rule race.Eats(Tree); Meleeable thresholds (BodySize<=1, manhunterOnDamageChance==0) | ThingDef.verbs[].range/warmupTime/defaultProjectile (VerbProperties) and ProjectileProperties.damageDef/explosionRadius; RaceProperties.foodType (Tree flag), predator and manhunterOnDamageChance via Go rule over race rows |
| NativeHusbandryObservation.cs | 113 | state | ReadHusbandry tool: per-animal training, designations, tame/release/slaughter eligibility, pregnancy, milk and wool fullness. |  |  |
| NativeHusbandryOperations.cs | 342 | state | Husbandry helpers and HusbandryIntent handler (train, area, master, follow, slaughter, tame, release, sterilize) with CAS tokens. |  | HerdFacts arithmetic on lifeStageAges and gestationPeriodDays could use RaceProperties rows |
| NativeIdeoRoleAssignment.cs | 68 | state | AssignIntent for ideology role precepts: validates and assigns a pawn to a Precept_Role. |  |  |
| NativeIdeoligionDesign.cs | 101 | code | Validates and builds a candidate Ideo from a design using the game ideo generator and meme/precept rules. |  | Meme and precept compatibility rules live in game code (IdeoGenerator); stays native |
| NativeIdeoligionReform.cs | 53 | state | IdeoligionReform action handler: compares design and reform count and applies the candidate ideo. |  |  |
| NativeIdeologyObservation.cs | 64 | state | Emits the primary ideoligion snapshot (memes, roles, rituals, buildings, precepts, reform points). |  |  |
| NativeIgnite.cs | 64 | state | IgniteIntent: drafted colonist throws the molotov at a cell via the attack_ground combat order. |  |  |
| NativeInteractOperations.cs | 65 | state | Orders a colonist to interact with a void structure or void node (CompInteractable.CanInteract). |  |  |
| NativeJoinerLetters.cs | 165 | state | Snapshot and answer of walk-in joiner and creepjoiner choice letters with CAS tokens. |  |  |
| NativeMechBills.cs | 100 | mixed | Bill_Mech write on a Building_MechGestator and the mech-recipe to PawnKind mapping. | Kind(RecipeDef) mapping mechResurrection/gestationCycles/ProducedThingDef.race.IsMechanoid to AnyPawnKind defName; Handles() | RecipeDef.mechResurrection, RecipeDef.gestationCycles, RecipeDef.products then ThingDef.race (RaceProperties) then PawnKindDef via Go rule over rows |
| NativeMineAcquisition.cs | 69 | state | Mine acquisition: CAS token, miner eligibility, apply-time preconditions and acquisition guard for a rock. |  |  |
| NativeMonolithOperations.cs | 56 | state | Orders a colonist to investigate or activate the void monolith. |  |  |
| NativeMoodReliefOperations.cs | 91 | code | Need relief: runs the game's food, rest or joy JobGiver and starts the resulting job. |  |  |
| NativeMoveBuilding.cs | 121 | state | RelocateIntent: places reinstall or install blueprints for a building or packed item. |  |  |
| NativeMovementOperations.cs | 69 | state | MoveIntent: Goto order for a drafted pawn with standable and reachable checks. |  |  |
| NativeNotificationReadTools.cs | 278 | state | Reads letter stack, live messages and active alerts into the Notifications reply. |  | Limits (40/12/40, ceiling 256) are tool constants, not game constants |
| NativeObservationSnapshot.cs | 36 | code | Stateless SHA256 row-level CAS token helper. |  |  |
| NativeObservationTools.cs | 389 | mixed | Status, cells and excavation-site read tools plus the threat, pawn-row, job-row and thing-row builders. | HiveBoundaryCells=10 hardcoded hive trespass radius; HostileBuilding rule (building.combatPower>0 or IsMortar, or Hive) | GameConstants (#2626) for the hive boundary radius if it mirrors a game value (else Go policy constant); BuildingProperties.combatPower/isMortar rows for the hostile-building rule |
| NativeOdysseyColony.cs | 89 | excluded | Odyssey colony reads (not read). |  |  |
| NativeOdysseyFacts.cs | 100 | excluded | Odyssey facts (not read). |  |  |
| NativeOpenCasketOperations.cs | 80 | state | Open job on an ancient cryptosleep casket with a temporary Open designation. |  |  |
| NativeOperationTools.cs | 83 | code | Per-game operation state ledger holder and zone preview and receipt lookup tool entry points. |  |  |
| NativeOrderDanger.cs | 16 | static | One constant: Danger.Deadly used for forced-order reach checks. | OrderDanger = Danger.Deadly | GameConstants (#2626): the Danger level vanilla float menu passes to CanReach |
| NativePawnControlObservation.cs | 35 | state | Attaches the native pawn control snapshot ref (or an issue) to a pawn row. |  |  |
| NativePawnDetails.cs | 655 | mixed | Builds per-pawn needs, health, surgery options, gear, biography, settings, social and animal rows from live pawn state; surgery success chance and tend gates are game-method code. | Hediff.Bad=def.isBad; BodyPart Vital=part.def.tags.vital; InstalledPart.SpawnThingDefName and Harvest/Yield MarketValue copy def.spawnThingOnRemoved and BaseMarketValue; SingleWorkTags enum list (WorkTags flags); TendRows=64 const (bot bound, not game) | HediffDef.isBad; BodyPartDef.tags -> BodyPartTagDef.vital; HediffDef.spawnThingOnRemoved + ThingDef.statBases MarketValue; WorkTags enum -> GameConstants |
| NativePawnObservationTools.cs | 230 | state | ListPawns tool plus the core pawn row builder (position, hostility, standing, nearest colonist, job target). |  |  |
| NativePawnRoyalty.cs | 90 | mixed | Emits a colonist royalty block: holdings, permit cooldowns, psycasts and psyfocus/entropy. | Psycast(): level, PsyfocusCost, EntropyGain, CooldownTicks (cooldownTicksRange.max), TargetKind derived from verbProperties.targetParams | AbilityDef.level; AbilityDef.cooldownTicksRange; AbilityDef.psyfocusCostRange; AbilityDef.verbProperties.targetParams (canTarget* flags); entropy gain from AbilityDef.comps (Go rule over rows) |
| NativePawnSettings.cs | 494 | state | Applies PawnSettingsIntent: nickname, medical care, hostility, self-tend, policies, medicine carry, mech mode, permit, bioferrite flag. | Medicine carry range uses InventoryStockGroupDefOf.Medicine min/max; permit checks read RoyalTitlePermitDef minTitle/prerequisite/permitPointCost | InventoryStockGroupDef.min/max (range check in Go from rows); RoyalTitlePermitDef.permitPointCost/minTitle/prerequisite |
| NativePlantAcquisition.cs | 179 | state | Census of eligible plants to harvest and the acquire designate/withdraw handler with live preconditions. |  |  |
| NativePolicyFacts.cs | 138 | mixed | Reads outfit/drug/food/reading policy databases, per-pawn policy inputs, biome diseases and allowed areas. | BiomeDiseases: IncidentDef.diseaseIncident filtered by Biome.CommonalityOfDisease; Requirement(): RoyalTitleDef.requiredApparel (bodyPartGroupsMatchAny, requiredDefs, requiredTags, allowedTags) emitted as TitleApparel | BiomeDef.diseases joined with IncidentDef.diseaseIncident in Go; RoyalTitleDef.requiredApparel -> ApparelRequirement message |
| NativePolicyPrune.cs | 131 | state | Deletes policies or allowed areas after reassigning holder pawns to per-pawn policies. |  |  |
| NativePopulationObservation.cs | 169 | mixed | Reads humanlike population, custody, surgery/harvest facts, storyteller population outlook and supported prisoner interactions. | SupportedInteractions name list (AttemptRecruit MaintainOnly ReduceResistance Release Enslave Convert); DeathOnDownedChance/UnrecruitableChance evaluate HealthTuning SimpleCurves; harvest goodwill constant -70 | PrisonerInteractionModeDef rows (defName,label) filtered in Go by the intent enum; HealthTuning curves and -70 harvest goodwill -> planned GameConstants (#2626), evaluated in Go from PopulationIntent |
| NativePresentationReadTools.cs | 156 | state | Reads graphical camera, selection and colonist roster for presentation tools. |  |  |
| NativePrisonerInteractionOperations.cs | 98 | mixed | Applies PrisonerInteractionIntent and hashes custody settings for snapshot tokens. | DefName(enum) 6-entry map from PrisonerInteraction to defName; Supported() mirrors ITab_Pawn_Visitor gates from def flags | PrisonerInteractionModeDef.isNonExclusiveInteraction, hideIfNotRecruitable, allowOnWildMan, allowInClassicIdeoMode (Go rule over rows); enum to defName map is a Go/proto table |
| NativeProductionBillSettings.cs | 41 | code | Validates a ProductionBillIntent shape against the exact canonical bill settings the planner may send. | IngredientSearchRadius=40 and DropOnFloor store mode literals; target unpause threshold max(1,target/2) | none for the canonical contract; 40 duplicates the vanilla default Bill.ingredientSearchRadius (GameConstants candidate) |
| NativeProductionBills.cs | 273 | mixed | Bill config hashing/snapshots, bill census rows, filter configuration and the production_bill/remove_production_bill action handlers. | ConfigureCorpses special-filter names AllowFresh, AllowCorpsesColonist, AllowCorpsesSlave, AllowCorpsesStranger; CorpsesHumanlike parent category literal; Product() category rule (Item non-corpse or minifiable CompArt building); HumanlikeCorpse via ingestible.sourceDef.race.humanlike; Skilled/NoWorker radius 40 | SpecialThingFilterDef.parentCategory + defName (Go rule over rows); ThingDef.category/minifiable/comps(CompArt); ThingDef.ingestible.sourceDef -> race.humanlike; radius 40 -> GameConstants |
| NativeProductionTracking.cs | 43 | mixed | Harmony hook tracking bills the bot placed and the ordinary-meal predicate. | OrdinaryMeal(): product ingestible.preferability between MealSimple and MealLavish | RecipeDef.products -> ThingDef.ingestible.preferability (Go rule over rows; FoodPreferability ordinals in GameConstants) |
| NativeQuestGravEngine.cs | 37 | state | Reads the grav engine inspect objective for the MechanoidSignal quest. |  |  |
| NativeQuestHackGift.cs | 97 | state | Reads hack-target and beg-for-items quest objectives with eligible hackers and haulers. |  |  |
| NativeQuestJoinerOperations.cs | 42 | state | Orders a colonist to take the OfferHelp job on a willing rescue joiner. |  |  |
| NativeQuestMonuments.cs | 105 | state | Reads monument marker quest state: install cells, sketch pieces, costs, resources and eligible haulers. |  |  |
| NativeQuestOperations.cs | 106 | state | Accepts a visible quest offer under the game's acceptance rules and hashes quest state tokens. |  |  |
| NativeQuestRefugees.cs | 24 | state | Lists refugee pod pawns for rescue objectives. |  |  |
| NativeQuestShuttleOperations.cs | 99 | state | Loads pawns into and launches a quest shuttle via vanilla gizmos. |  |  |
| NativeQuestShuttles.cs | 48 | state | Reads quest shuttle loading state and lodger mood thresholds. |  |  |
| NativeQuestSiteMining.cs | 28 | state | Lists mineable precious-lump targets on a quest site map. |  |  |
| NativeQuestSitePeople.cs | 27 | state | Lists reward pawns of site rescue quests. |  |  |
| NativeQuestSurvey.cs | 36 | state | Reads survey scanner quest part timing via reflection. |  |  |
| NativeQuestTargetIdentity.cs | 8 | static | Matches the vanilla worshipped-terminal hacking-started signal string for a quest id. | Quest{id}.terminal.HackingStarted format string | Go rule: string pattern; vanilla quest signal tag, no def row (a one-line GameConstants entry at most) |
| NativeQuestThreats.cs | 42 | code | Sums scripted raider combat power and incident budgets from quest parts. |  |  |
| NativeQuestWorkers.cs | 40 | mixed | Reads per-colonist quest worker facts: health, fighting ability, carry capacity, negotiation and work rates. | stats list = RecipeDef.workSpeedStat for all recipes plus ConstructionSpeed and PlantWorkSpeed | Go rule: distinct RecipeDef.workSpeedStat over recipe rows plus ConstructionSpeed and PlantWorkSpeed stat names; per-pawn stat values stay live |
| NativeQuestWorkload.cs | 41 | code | Forecasts quest work amount and rate factor from plants or an existing usable bench recipe. |  |  |
| NativeRaceFacts.cs | 85 | excluded | Race facts (Biotech/Odyssey race scope). |  |  |
| NativeReadingPolicy.cs | 67 | mixed | Creates/updates ReadingPolicy allowing a set of book defs and exposes book enumeration. | IsBook (CompProperties_Book on ThingDef) and Books() enumerating all ThingDefs | ThingDef.comps containing CompProperties_Book (Go rule over ThingDef rows); policy allowed list stays live state |
| NativeRecipeRoles.cs | 19 | code | Recipe role predicates by worker counter class and recipe lookup by name. | ButcherFlesh = WorkerCounter is RecipeWorkerCounter_ButcherAnimals | RecipeDef.workerCounter class equals RecipeWorkerCounter_ButcherAnimals (Go rule over rows) |
| NativeRecoveryFacts.cs | 39 | state | Reads colonist buildings and per-pawn allowed area for the recovery census. |  |  |
| NativeRecoveryOperations.cs | 110 | state | Orders a pawn to fix a breakdown or refuel a building using the native work giver. |  |  |
| NativeRef.cs | 47 | state | Builds Common.Ref from load ids and collects things referenced during a frame capture. |  |  |
| NativeRemoveRoof.cs | 93 | code | RemoveRoofIntent: validates cells and designates NoRoof area via Designator_AreaNoRoof plus a Harmony patch on WorkGiver_BuildRoof. |  |  |
| NativeRepairOperations.cs | 62 | code | GiveJobIntent Repair: checks reach and WorkGiver_Repair.HasJobOnThing then issues a forced Repair job. |  |  |
| NativeResearchObservationTools.cs | 282 | mixed | Research snapshot (progress, slots, lock reasons, benches, researchers) plus a static per-project catalog row. | Static(ResearchProjectDef,Faction) ~18 lines: base_cost, cost factor, tab, knowledge category, prerequisites, hidden prerequisites, techprint count, required building and facilities | ResearchProjectDef.baseCost/prerequisites/hiddenPrerequisites/techLevel/tab/knowledgeCategory/techprintCount/requiredResearchBuilding/requiredResearchFacilities; cost factor = Go rule over TechLevel (GameConstants) |
| NativeResearchSelectOperations.cs | 54 | code | ResearchIntent: sets current project through ResearchManager.SetCurrentProject after CanStartNow checks. |  |  |
| NativeResourceSourcesTool.cs | 195 | mixed | Lists reachable mine/harvest/cut sources and storage capacity for a resource def from live map state. | Cheap census filter def.plant.harvestedThingDef==resource or def.building.mineableThing==resource (~3 lines); Method string mine/cut/harvest from def.plant.IsTree; AutoHomeAreaMaker.BorderWidth read by reflection | ThingDef.plant.harvestedThingDef / ThingDef.building.mineableThing / plant.isTree via Go rule over ThingDef rows; BorderWidth from GameConstants (#2626) |
| NativeRitual.cs | 243 | code | RitualIntent: starts bestowing ceremony via the bestower toil gizmo and begins held ritual precepts through Command_Ritual and Dialog_BeginRitual. |  |  |
| NativeRoomObservationTools.cs | 221 | state | Room census with geometry, temperature, beds, doors, stockpile membership and room stats. | Fixed list of 5 RoomStatDefOf (Cleanliness, Wealth, Space, Beauty, Impressiveness) read per room | RoomStatDef rows (stat names only; values stay live) |
| NativeRoyaltyColony.cs | 110 | mixed | Royalty colony section: player thrones, pending bestowing ceremonies, and neuroformer/neurotrainer stock. | ReadNeuroformers def enumeration (PsychicAmplifier plus ThingCategory NeurotrainersPsycast defs), TeachesPsycast from CompProperties_UseEffect_GainAbility, Craftable (any recipe product), Tradeable (def.tradeability), ~20 lines | ThingDef.thingCategories + ThingDef.comps (use-effect ability) + ThingDef.tradeability; Craftable = Go rule over RecipeDef.products; Held count and ceremonies stay state |
| NativeRuleBook.cs | 135 | state | Lease ledger of attached native rules with validation, per-actor cooldown and firing counts. |  |  |
| NativeRuleRuntime.cs | 174 | code | Harmony DoSingleTick hook that fires PreyKilled rules by finding nearest designated prey and issuing a prioritized Hunt job. |  |  |
| NativeRuleTools.cs | 68 | state | Bridge tools rules_attach, rules_clear and rules_read_status delegating to the rule runtime. |  |  |
| NativeRulesAttach.cs | 39 | state | RulesAttachIntent action handler that validates through a throwaway rule book and attaches rules with a lease. |  |  |
| NativeShrineBreachSafety.cs | 38 | code | Computes structural cells of ancient shrine rooms around a candidate breach wall for the roof-support check. |  |  |
| NativeShrineObservationTools.cs | 160 | state | Ancient shrine census: casket groups, room rect, guards, occupants and safe breach walls. |  |  |
| NativeSiteExtraction.cs | 62 | code | Quest site extraction facts: crew, exit cells, caravan carry capacity, food days, cargo and home routes via caravan dialog and path finders. |  |  |
| NativeSiteSecurity.cs | 124 | mixed | Quest site security facts: initial threat points, active/dormant threat, traps, pending raid points from site parts and linked quests. | ClosedPart switch (about 14 SitePartDef defNames mapped to expected workerClass), LoadedGroundPart switch (8 opportunity-site defNames), InitialBudget special cases GravshipWreckage/BanditGang, SleepingMechCurve read by reflection from QuestPart_SleepingMechs; ~35 lines | SitePartDef.workerClass (defs.proto SitePartDef field 11) with a Go set of defNames; SleepingMechCurve and MinPointsToGenerate from GameConstants (#2626) |
| NativeSpatialAccessTool.cs | 146 | code | Single-frame pawn spatial access audit: BFS reachability over walkable cells with blocked cells vs native CanReach. |  |  |
| NativeStockpilePatch.cs | 115 | state | Stockpile/storage-building settings patch handler plus storage snapshot token. |  |  |
| NativeStockpilePlacement.cs | 107 | code | Stockpile placement preview and apply: flood-fills free zoneable cells into components and creates Zone_Stockpile with resolved filter. |  |  |
| NativeStockpileSettings.cs | 214 | state | Resolves and applies stockpile priority, preset and ThingFilter selectors, and projects a ThingFilter to proto. | PresetName maps 7 FilterPreset enum values to preset strings (7 lines) | Protocol enum to string mapping; no def mirror field |
| NativeStrip.cs | 56 | code | Strip designation handler using Designator_Strip with precondition rules. |  |  |
| NativeSubdueOperations.cs | 68 | code | Subdue job: drafts the pawn and issues an AttackMelee job with a blunt verb until the target is downed. |  |  |
| NativeSuppliesObservationTools.cs | 274 | state | Supplies census: stock per def across spawned, carried, contained and trader holders with ownership and corpse detail. |  |  |
| NativeSupplyAllow.cs | 96 | state | Allow/Forbid designation handler and Allow snapshot token for haulable items. |  |  |
| NativeSurgery.cs | 96 | code | Queues a surgery bill via HealthCardUtility.CreateSurgeryBill after RecipeDef availability and body-part checks. |  |  |
| NativeTeamPolicy.cs | 184 | mixed | Starting-team hard-reject, coverage and reroll scoring policy over PawnFacts. | HardReject trait defNames and degrees (Pyromaniac, Nerves -2, Wimp, Industriousness -2, DrugDesire 2); Score trait bonuses (Nerves 2, Tough, Industriousness 2, FastLearner, Nimble); skill targets 8/6/4; learn multipliers 1.5/1/0.35 | TraitDef.degreeDatas (defs.proto TraitDef field 8) for trait names/degrees; the remaining numbers are rimgovernor policy kept in Go |
| NativeTendOperations.cs | 88 | code | Tend job: validates doctor, patient and reach then takes the job built by WorkGiver_Tend or a draftedTend fallback. |  |  |
| NativeThreatClassifier.cs | 96 | code | Classifies pawns as engaged or proximity threats from precomputed ThreatFacts and builds ThreatPawn rows. |  |  |
| NativeTradeAcquisition.cs | 144 | mixed | Trade acquisition facts: queued arrivals, comms consoles, negotiators, passing ships and requestable trader options with goodwill cost and eligibility. | Goodwill deltas -30 orbital / -15 caravan; cooldowns 900000 orbital / 240000 caravan ticks; arrival windows 2500-5000 orbital / 120000 caravan; relation thresholds +-75 and +-100; faction request flags and trader-kind enumeration (~12 lines) | Numbers from GameConstants (#2626); FactionDef.canRequestTraders/canRequestOrbitalTrader/caravanTraderKinds/orbitalTraderKinds/allowedArrivalTemperatureRange and TraderKindDef.requestable via Go rule over FactionDef rows |
| NativeTradeFoodFacts.cs | 21 | static | Computes edible-food nutrition for a trade line from ThingDef ingestible properties and the Nutrition stat. | Whole file (~10 lines): ingestible.HumanEdible, foodType flags excluding Corpse/Kibble, IsMedicine/IsWeapon/IsApparel exclusions, Nutrition stat base | ThingDef.ingestible (IngestibleProperties) and ThingDef.statBases Nutrition via Go rule over rows; NativeFoodPolicy.IsFood is a separate shared rule |
| NativeTradeObservationTools.cs | 293 | state | Reads traders, negotiators, active trade session and the full trade sheet lines with prices, counts and pawn details. |  |  |
| NativeTradeOperations.cs | 690 | state | Trade session ledger and operations: open, walk-to-trader, set lines, accept and end with TradeDeal and Harmony arrival hooks. |  |  |
| NativeTradeWorldSessions.cs | 215 | state | World-trade extension of the session ledger: settlement caravan trades and orbital comms contact via TradeShip.TryOpenComms hook. |  |  |
| NativeUninstallBuilding.cs | 58 | state | Validates and applies the Uninstall designation on one exact player building and returns installation evidence. |  | none |
| NativeUpkeepFacts.cs | 495 | mixed | Per-frame upkeep census (items, structures, fires, filth, home area, lighting, flooring, routes, people, beds, animals) with path and reach measurement. | SleepingRelations royal title bedroom requirement decoding (~25 lines); lamp GlowRadius from CompGlower.Props; RepairPriority bucket (CompTempControl/CompPowerPlant/medical bed=0, holdsRoof/WorkTable/Bed=1); route facility kind selection by class and surfaceType Eat and bed_humanlike; animal Diet from RaceProps.foodType | RoyalTitleDef.bedroomRequirements; CompProperties_Glower.glowRadius and ThingDef.comps; ThingDef.holdsRoof and building.bed_humanlike and surfaceType; ThingDef.race.foodType; priority and facility kind as Go rules over building rows |
| NativeUseItemOperations.cs | 140 | state | Orders one colonist to use an item via a worn target-effect verb or a CompUsable job and verifies the job started. |  | none |
| NativeWallRemovalOperations.cs | 161 | state | Resolves the admissible wall-upgrade site for a Designate on a wall cell and commits the guarded deconstruct designation. |  | none |
| NativeWallUpgradeObservationTools.cs | 201 | mixed | Read-only census of wall replacement and cleanup sites with backup cells, supports, blockers and snapshot tokens. | Materials(): stony stuffs allowed for ThingDefOf.Wall with CostListAdjusted costs (~12 lines); ThingDefOf.Wall hardcoded as the only wall def | ThingDef.stuffCategories and StuffProperties.categories plus ThingDef.costList and costStuffCount for Wall rows; Go rule picks stony stuffs and computes cost |
| NativeWorkSettings.cs | 158 | state | Applies work priorities, allowed area and 24-hour timetable to one free colonist with readback and evidence. | ScheduleHours=24 constant | GameConstants (#2626) hours per day |
| NativeWorldObservation.cs | 123 | state | Reads settlements near a world tile with faction relation, goodwill and self-computed CAS tokens. |  | none |
| NativeWorldProgressionObservation.cs | 395 | state | Census of maps, colonists, factions, caravans with routes and inventory, assemblies and quests with objectives and rewards. |  | none |
| NativeWorldSites.cs | 74 | mixed | Lists world sites and quest-anchored world objects with security, mining, extraction, peace talks and caravan route estimate. | PeaceTalks goodwill ranges DiplomacyTuning.Goodwill_PeaceTalksDisasterRange and TriumphRange and MaxGoodwill (3 lines) | GameConstants (#2626) DiplomacyTuning constants |
| NativeZoneCellEdit.cs | 122 | state | Adds or removes explicit cells on an existing zone with grid consistency checks. |  | none |
| NativeZoneCreation.cs | 214 | state | Validates, previews and creates growing or fishing zones and returns zone evidence. | Required labels Fishing and Crops; crop sowTags contains Ground check; Fishing needs Odyssey and Fishing research (Odyssey part belongs to #2632) | ThingDef.plant.sowTags (PlantProperties.sowTags); label constants stay as protocol |
| NativeZoneDeletion.cs | 53 | state | Deletes a zone after phantom-cell and haul-grid checks. |  | none |
| NativeZoneIntent.cs | 50 | code | Routes a ZoneIntent to the create, delete, cell-edit, stockpile or settings handler. |  | none |
| NativeZoneObservationTools.cs | 138 | mixed | Lists zones with bounds, stockpile priority and filter, and growing-zone farm facts per crop. | Farm facts copy PlantProperties (fertilityMin, minGrowthTemperature, minOptimalGrowthTemperature, maxOptimalGrowthTemperature, maxGrowthTemperature, harvestYield, harvestedThingDef nutrition); FoodStorage scan enumerates DefDatabase<ThingDef> for human food with Rottable comp allowed by the filter (~15 lines) | ThingDef.plant.* (PlantProperties) and harvestedThingDef.ingestible nutrition; FoodStorage as Go rule over ThingDef rows (ingestible, CompProperties_Rottable) and the filter rows |
| NullableAttributes.cs | 17 | code | net472 shims for NotNullWhen and MaybeNullWhen attributes. |  | none |
| PlacementProtocol.cs | 148 | code | Validates a placement request and maps native preview results to the wire CandidateReply. |  | none |
| ProtoBoundary.cs | 617 | code | ProtoJSON parse and encode boundary, main-thread hop timing, identity and context validation, failure and refusal-class mapping. |  | none |
| ProtoGovernorStateTools.cs | 79 | state | Reads and replaces the opaque governor-state blobs saved with the game. |  | none |
| ProtoIdentityTools.cs | 174 | state | Reads native tick and pause state and the identity with a hand-written capability list. | Hand-written capability table of about 30 Capability rows with method names and detail text (~120 lines) | none; not def data. Candidate for deletion or generation from proto services |
| ProtoLifecycleLoadTools.cs | 321 | state | Starts an async native save load and polls readiness, with a Harmony visual-ready tracker. |  | none |
| ProtoLifecycleNewColonyTools.cs | 495 | mixed | Drives new-colony generation through world, tile, colonists and map phases then saves; applies team-composition rerolls. | Resolve() lookups listing DefDatabase Scenario, Difficulty, Storyteller and BiomeDef (canBuildBase) for error text; SkillDefFor and WorkTypeFor TeamSkill to def maps (~25 lines); bounds colonist_count 1..10, map_size 100..400, planet_coverage 0.05..1; Wall constructionSkillPrerequisite in ReadFacts | ScenarioDef, DifficultyDef, StorytellerDef, BiomeDef.canBuildBase rows; SkillDef and WorkTypeDef rows; ThingDef.constructionSkillPrerequisite; bounds as Go validation or GameConstants (#2626) |
| ProtoLifecycleSaveSignalTools.cs | 61 | state | Long-poll and ack tools for the pre-save handshake. |  | none |
| ProtoLifecycleSaveTools.cs | 192 | state | Performs the trusted pause-gated checkpoint save with identity and tick re-verification and a request-outcome table. |  | none |
| ProtoOverlayTools.cs | 110 | state | Draws a named controller overlay layer of cell shapes and labels on the map. |  | none |
| ProtoPresentationMediaTools.cs | 71 | state | Reads and leases the controller rendering demand state. |  | none |
| ReplyEncoder.cs | 52 | code | Bounded encoder-worker slot reservation for detached replies. |  | none |
| ReplyRing.cs | 156 | code | Shared-memory reply ring for large proto-shm replies. |  | none |
| RowDiff.cs | 57 | code | Per-row hash compare of a keyed family to report changed and removed ids. |  | none |
| SnapshotFrames.cs | 282 | mixed | Captures one whole snapshot stream frame of all state families plus combat inputs in one hop. | CombatInputs copies mortar verbProps minRange and range and caps (64 doors, 16 mortars); hive ThingDefOf constant | ThingDef verbs (VerbProperties.minRange and range) of the turret gun def; caps as GameConstants (#2626) or Go |
| SnapshotSections.cs | 174 | code | Elides unchanged singleton sections and delta-encodes keyed tables with watermarks. |  | none |
| SnapshotStream.cs | 367 | state | Opens the snapshot ring, schedules frame captures on ticks, pause edges and writes, and publishes frames. |  | none |
| SocialBeerBill.cs | 32 | code | Harmony patch making a reserve beer bill count stocked beer and fermenting wort. | Hardcoded defName Beer lookup (1 line) | ThingDef row by defName Beer or a Go rule |

## observations.proto messages and enums

| Name | Kind | Line | Fields | Class | What it carries | Static fields | Replacement |
|---|---|---|---|---|---|---|---|
| ReadScope | message | 19 | 1 | state | Request scope carrying expected identity |  |  |
| SnapshotRef | message | 20 | 3 | state | Context plus entity id and CAS token |  |  |
| Completeness | message | 28 | 1 | state | Count of rows a query filter excluded |  |  |
| ReadIssue | message | 31 | 2 | state | Per-field read problem marker |  |  |
| Rectangle | message | 32 | 2 | state | Cell-rect helper |  |  |
| ClearanceClass | enum | 36 | 5 | code | Tag for how a clearance target is classified |  | Go rule over ClearanceTarget.def_name |
| ClearanceTarget | message | 43 | 12 | mixed | Non-player building touching Home with deconstruction facts | class; deconstructible | class: Go rule over def_name (ThingDef.defName); deconstructible: constant true (native writes it unconditionally) or ThingDef.building deconstructible flag |
| ClearanceFloor | message | 65 | 3 | state | Constructed floor cell on planned ground with designation flag |  |  |
| SalvageYield | message | 70 | 4 | code | Computed salvage yield per def with count value and storage headroom |  |  |
| SalvageEvidence | message | 76 | 5 | code | Native-computed salvage safety path length labor and yields |  |  |
| ClearanceChunk | message | 88 | 6 | state | Rock or slag chunk stack on a Home cell with haul flags |  |  |
| ClearanceTargetsRequest | message | 102 | 7 | code | Request with planned ground and Go-supplied salvage time budgets |  |  |
| ClearanceTargetsSnapshot | message | 116 | 4 | state | Snapshot of clearance targets chunks and floors |  |  |
| ClearanceTargetsReply | message | 122 | 3 | state | Reply envelope oneof snapshot or unavailable or failure |  |  |
| ShrineGuardKind | enum | 134 | 7 | code | Tag for guard category derived from pawn race and kind |  | Go rule over PawnKindDef/ThingDef race |
| ShrineCasket | message | 143 | 7 | mixed | Ancient casket with hit points contents and claim state | max_hit_points | ThingDef.statBases MaxHitPoints for the casket def (no stuff) |
| ShrineGuard | message | 152 | 4 | state | Hostile pawn or hive inside a shrine room |  |  |
| ShrineOccupant | message | 160 | 6 | state | Non-player humanlike or corpse inside the room |  |  |
| ShrineBreachWall | message | 168 | 4 | state | Perimeter wall the player may deconstruct plus outside cell |  |  |
| AncientShrine | message | 174 | 9 | state | One sealed or opened ancient-danger room as a unit |  |  |
| AncientShrinesRequest | message | 185 | 1 | state | Read scope request |  |  |
| AncientShrinesSnapshot | message | 186 | 2 | state | Snapshot of ancient shrines |  |  |
| AncientShrinesReply | message | 190 | 3 | state | Reply envelope |  |  |
| MapSize | message | 195 | 2 | state | Map width and height |  |  |
| DefinitionRef | message | 196 | 2 | mixed | Def name plus label reference | label | label: XDef.label of the referenced def (any Def message in defs.proto) |
| EntityRef | message | 197 | 5 | state | Instance id def name label map id and position |  |  |
| TargetRef | message | 204 | 3 | state | Oneof entity or cell or unavailable |  |  |
| Quantity | message | 205 | 2 | state | Def name with unit count |  |  |
| Amount | message | 206 | 2 | state | Def name with amount |  |  |
| JobEvidence | message | 207 | 12 | state | Pawn current job def load id target and priority |  |  |
| PawnNeeds | message | 225 | 13 | mixed | Pawn need levels hunger category break risk thresholds | break_threshold_minor; break_threshold_major; break_threshold_extreme | Pawn-computed (traits genes) so effectively code; MentalBreakDef thresholds only the base |
| Hediff | message | 235 | 20 | mixed | One health condition on a pawn with severity tend and immunity state | part_label; bad; severity_label | part_label: BodyPartDef.label; bad: HediffDef.isBad; severity_label: HediffDef.stages HediffStage.label by severity |
| Capacity | message | 246 | 3 | state | Pawn capacity level |  |  |
| SurgeryBill | message | 247 | 4 | state | Queued surgery bill on a pawn |  |  |
| PawnHealth | message | 248 | 22 | state | Pawn health summary conditions capacities hediffs and surgery facts |  |  |
| InstalledPart | message | 267 | 4 | mixed | Installed added part | spawn_thing_def_name | HediffDef.spawnThingOnRemoved |
| MissingBodyPart | message | 269 | 5 | mixed | Missing body part at its common ancestor | part_def_name; parent_index; parent_def_name; vital | BodyDef BodyPartRecord tree (def and parent per index); vital: BodyPartDef.tags BodyPartTagDef.vital |
| SurgeryKind | enum | 270 | 7 | code | Tag classifying a surgery recipe |  | Go rule over RecipeDef (addsHediff removesHediff workerClass) |
| SurgeryOperation | message | 283 | 16 | mixed | Available operation on one part with success chance and market values | kind; yield_market_value; yield_thing_def; medicine_market_value (base value) | kind: Go rule over RecipeDef; yield_thing_def: HediffDef.spawnThingOnRemoved; yield_market_value and medicine_market_value: ThingDef.statBases MarketValue x count |
| Quality | enum | 310 | 8 | state | Quality tag |  |  |
| GearItem | message | 311 | 15 | code | Worn or held gear with quality hp and computed armor and insulation |  |  |
| PawnEquipment | message | 321 | 10 | code | Pawn equipment and apparel lists with ranged and melee DPS |  |  |
| Passion | enum | 331 | 4 | state | Skill passion level tag |  |  |
| Skill | message | 333 | 5 | state | Pawn skill level passion |  |  |
| Trait | message | 334 | 2 | state | Pawn trait def and degree |  |  |
| WorkSetting | message | 335 | 3 | state | Pawn work priority |  |  |
| TimetableSlot | message | 336 | 2 | state | Pawn schedule hour assignment |  |  |
| PawnBiography | message | 337 | 12 | state | Pawn ages backstories skills traits incapabilities |  |  |
| Thought | message | 343 | 4 | state | Mood thought def count offsets |  |  |
| Relation | message | 344 | 4 | state | Pawn relation to another pawn |  |  |
| PawnSocial | message | 345 | 6 | state | Memories situational thoughts relations |  |  |
| PawnSettings | message | 346 | 15 | state | Pawn medical care areas work priorities schedule policies |  |  |
| PawnPolicyInputs | message | 363 | 18 | mixed | Inputs for per-pawn outfit drug food planners | title_apparel; role_apparel | title_apparel: RoyalTitleDef.requiredApparel; role_apparel: Precept_Role apparel requirements on the PreceptDef |
| InventoryStockSetting | message | 389 | 3 | state | Pawn inventory stock tracker entry |  |  |
| ChemicalState | message | 393 | 4 | state | Pawn addiction and tolerance per chemical |  |  |
| ApparelRequirementFact | message | 394 | 4 | static | Apparel requirement body-part groups required defs and tags | body_part_groups; required_defs; required_tags; allowed_tags | ApparelRequirement fields in defs.proto (bodyPartGroupsMatchAny requiredDefs requiredTags allowedTags) |
| TrainingEntry | message | 395 | 5 | mixed | Animal training state per trainable | def_name | def_name is a TrainableDef key (static key only) |
| AnimalState | message | 396 | 40 | mixed | Player animal husbandry facts and settings | tameable (partly) | tameable: Go rule over race wildness (RaceProperties) plus pawn state |
| PawnState | message | 443 | 47 | state | Canonical pawn row with flags needs health gear social and status |  |  |
| PawnStanding | message | 501 | 5 | state | Faction standing of a humanlike pawn |  |  |
| PawnGene | message | 519 | 3 | excluded | Biotech pawn gene entry |  |  #2630 Biotech |
| PawnMechanitor | message | 520 | 5 | excluded | Biotech mechanitor bandwidth facts |  |  #2630 Biotech |
| PawnMech | message | 527 | 6 | excluded | Biotech mech overseer and energy facts |  |  #2630 Biotech |
| PawnDeathrest | message | 531 | 7 | excluded | Biotech deathrest facts |  |  #2630 Biotech |
| PawnBiotech | message | 535 | 17 | excluded | Biotech pawn facts |  |  #2630 Biotech |
| PawnTendDoctor | message | 553 | 8 | code | Doctor-side tend gates and reachability |  |  |
| PawnFilter | message | 572 | 14 | state | Pawn list query filter |  |  |
| PawnDetails | message | 578 | 11 | state | Pawn list detail projection flags |  |  |
| PawnSnapshot | message | 588 | 5 | state | Pawn list snapshot with completeness |  |  |
| ListPawnsRequest | message | 594 | 3 | state | List pawns request |  |  |
| ListPawnsReply | message | 595 | 3 | state | List pawns reply envelope |  |  |
| HungerCategory | enum | 598 | 5 | state | Hunger band tag |  |  |
| BreakRisk | enum | 600 | 5 | state | Mental break band tag |  |  |
| HolderKind | enum | 603 | 5 | state | What holds a stack |  |  |
| WasteKind | enum | 605 | 3 | state | Why an item is waste |  |  |
| BenchUnusableReason | enum | 607 | 6 | state | Why a bench takes no bills |  |  |
| StockItem | message | 614 | 7 | mixed | One stack in a stock row | market_value (of packed item) | ThingDef.statBases MarketValue with quality factor via Go rule over rows |
| HeldStock | message | 618 | 3 | state | Holder of a held stack |  |  |
| CorpseState | message | 619 | 6 | state | Corpse with inner pawn race rot stage |  |  |
| ResourceStock | message | 620 | 20 | mixed | Per-def stock counts and items | definition.label | XDef.label of the ThingDef |
| StockCategory | enum | 634 | 6 | state | Supplies read category tag |  |  |
| StockOwnership | enum | 635 | 3 | state | Supplies read ownership tag |  |  |
| StockFilter | message | 636 | 8 | state | Supplies query filter |  |  |
| SuppliesSnapshot | message | 637 | 3 | state | Supplies snapshot |  |  |
| ListSuppliesRequest | message | 638 | 2 | state | List supplies request |  |  |
| ListSuppliesReply | message | 639 | 3 | state | List supplies reply envelope |  |  |
| IngredientRequirement | message | 640 | 7 | code | Required available and missing ingredient amounts for a bill |  |  |
| FilterSpecialRule | message | 641 | 2 | state | Special filter rule toggle |  |  |
| StockpileFilter | message | 642 | 8 | state | Bill or zone thing filter |  |  |
| BillState | message | 647 | 26 | mixed | One bill on a bench | recipe.label | RecipeDef.label |
| IngredientReservation | message | 662 | 2 | state | Live bill job promised ingredients |  |  |
| BillStack | message | 664 | 7 | state | Bench bill stack with usability and work speed |  |  |
| RecipeState | message | 666 | 3 | mixed | Whether a bench offers a recipe now | recipe.label | RecipeDef.label |
| BuildingSettings | message | 669 | 18 | mixed | Building forbid switch power temperature assignment settings | flickable; maximum_assigned_pawns | flickable: ThingDef.comps CompProperties_Flickable present; maximum_assigned_pawns: CompProperties_AssignableToPawn.maxAssignedPawnsCount (or bed slots from ThingDef) |
| MaterialDeficit | message | 680 | 4 | state | Construction material need have still needed |  |  |
| ConstructionState | message | 681 | 13 | mixed | Construction frame or blueprint progress and finishing gates | total_work; minimum_finishing_skill | total_work: ThingDef.statBases WorkToBuild with stuff factors (Go rule over rows); minimum_finishing_skill: ThingDef.constructionSkillPrerequisite |
| ThermalSide | message | 689 | 8 | state | Thermal reading beside a building side |  |  |
| BuildingServiceState | message | 690 | 11 | mixed | Building power fuel and breakdown state | allowed_fuel_defs | CompProperties_Refuelable.fuelFilter allowed defs on ThingDef.comps |
| BuildingStatus | enum | 696 | 4 | state | Construction stage tag |  |  |
| BuildingState | message | 702 | 22 | mixed | Canonical building row | uses_hit_points | ThingDef.useHitPoints |
| PowerNetwork | message | 718 | 15 | state | Power network aggregate producers consumers watts and storage |  |  |
| BuildingsSnapshot | message | 725 | 5 | state | Buildings table plus power networks for the map |  |  |
| ListBuildingsRequest | message | 728 | 9 | state | Read request parameters |  |  |
| ListBuildingsReply | message | 729 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| RoomStat | message | 730 | 6 | code | One room stat (value, display) computed by RoomStatDef workers |  |  |
| RoomBedMembership | message | 731 | 4 | state | Which pawns own/use/can access a bed in a room |  |  |
| StockpileMembership | message | 732 | 2 | state | Stockpile zone in a room with its contents |  |  |
| RoomState | message | 733 | 28 | state | Room census row: role, temperature, cells, pawns, beds, doors |  |  |
| RoomDoor | message | 751 | 9 | state | Door in a room boundary with open/forbidden state |  |  |
| RoomsSnapshot | message | 760 | 3 | state | Rooms table with completeness |  |  |
| ListRoomsRequest | message | 761 | 5 | state | Read request parameters |  |  |
| ListRoomsReply | message | 762 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| ZoneState | message | 763 | 14 | state | Zone row: bounds, priority, crop, filter, contents, farm facts |  |  |
| ZonesSnapshot | message | 771 | 3 | state | Zones table |  |  |
| ListZonesRequest | message | 772 | 6 | state | Read request parameters |  |  |
| ListZonesReply | message | 773 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| Thing | message | 784 | 22 | state | Canonical thing row: stack, stuff, hp, forbidden, food facts |  |  |
| ThingsSnapshot | message | 790 | 3 | state | Things table with removed ids |  |  |
| CellsSnapshot | message | 799 | 3 | state | Map size plus requested CellGrid keyframe |  |  |
| GetCellsRequest | message | 806 | 2 | state | Read request parameters |  |  |
| GetCellsReply | message | 810 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| ResearchUnlock | message | 811 | 3 | static | What a research project unlocks (def, native type, label) | def_name;native_type;label | Go rule over ThingDef.researchPrerequisites, RecipeDef.researchPrerequisite(s), TerrainDef.researchPrerequisites rows keyed by ResearchProjectDef.defName |
| TechLevel | enum | 813 | 9 | static | Vanilla TechLevel enum | all | defs.proto enum TechLevel |
| ResearchProject | message | 814 | 23 | mixed | Research project row: definition facts plus progress and availability | tab;tech_level;base_cost;prerequisites;hidden_prerequisites;techprints_needed;required_building;required_facilities;unlocks;category | ResearchProjectDef.tab, techLevel, baseCost, prerequisites, hiddenPrerequisites, techprintCount, requiredResearchBuilding, requiredResearchFacilities; unlocks = Go rule over rows; category = Go rule over ResearchProjectDef.tab |
| Researcher | message | 823 | 6 | code | Pawn research ability/priority/active row |  |  |
| ResearchFacility | message | 824 | 2 | state | Linked research facility and whether active |  |  |
| ResearchBench | message | 825 | 3 | state | Research bench with linked facilities |  |  |
| ResearchSlot | message | 826 | 2 | state | Current project per research category |  |  |
| ResearchSnapshot | message | 828 | 10 | state | Research snapshot: slots, projects, benches, researchers |  |  |
| ResearchRequest | message | 833 | 7 | state | Read request parameters |  |  |
| ResearchReply | message | 834 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| AccessTarget | message | 836 | 4 | code | Per-cell native/projected reachability |  |  |
| PawnAccess | message | 837 | 8 | code | Reachability counts and per-target access for a pawn |  |  |
| SpatialAccessSnapshot | message | 840 | 4 | code | Pawn access results and map walkable counts |  |  |
| SpatialAccessRequest | message | 841 | 4 | state | Read request parameters |  |  |
| SpatialAccessReply | message | 842 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| DefenseCell | message | 848 | 19 | state | Per-cell defense census: cover, walkable, edge reachability, designations |  |  |
| CoverKind | enum | 866 | 5 | code | How the game would remove a cover thing (plant/chunk/mineable/building) |  |  |
| RaidTrack | message | 871 | 7 | state | Observed hostile lord spawn and trail since load |  |  |
| DefenseSiteSnapshot | message | 877 | 6 | mixed | Defense census cells plus cover threshold and raid tracks | cover_threshold | Go rule over ThingDef rows: min positive ThingDef.fillPercent (loaded defs) |
| DefenseSiteRequest | message | 881 | 2 | state | Read request parameters |  |  |
| DefenseSiteReply | message | 882 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| LineOfFire | message | 887 | 7 | code | Line of sight and cover chance for a firing/approach pair |  |  |
| LinesOfFireSnapshot | message | 888 | 2 | code | Lines of fire results |  |  |
| LinesOfFireRequest | message | 889 | 3 | state | Read request parameters |  |  |
| LinesOfFireReply | message | 890 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| RoofSupportCell | message | 891 | 4 | code | Roof cell support counterfactual |  |  |
| RoofSupportSnapshot | message | 892 | 3 | code | Roof support for removing a target |  |  |
| RoofSupportRequest | message | 893 | 2 | state | Read request parameters |  |  |
| RoofSupportReply | message | 894 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| ExcavationSupport | enum | 899 | 4 | code | Support outcome after removal |  |  |
| ExcavationCell | message | 900 | 7 | code | Per-cell excavation eligibility and blocker |  |  |
| ExcavationSiteSnapshot | message | 906 | 9 | code | Excavation certification: support, collapse, workers, access |  |  |
| ExcavationSiteRequest | message | 912 | 3 | state | Read request parameters |  |  |
| ExcavationSiteReply | message | 913 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| StuffOption | message | 916 | 2 | static | A stuff and the cost list for building from it | stuff;costs | Go rule over ThingDef.costList + costStuffCount for stuffCategories stuff rows |
| WallUpgradeSite | message | 917 | 21 | mixed | Wall upgrade planning row: target, backups, workers, geometry | costs;replacement_materials | Go rule over ThingDef.costList/costStuffCount/stuffCategories rows |
| WallUpgradeSnapshot | message | 930 | 2 | state | Snapshot table of rows for the read |  |  |
| WallUpgradeSitesRequest | message | 931 | 2 | state | Read request parameters |  |  |
| WallUpgradeSitesReply | message | 932 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| ResourceSource | message | 933 | 17 | state | Resource source row: yield, reachability, designation, depletion |  |  |
| StorageCapacity | message | 943 | 8 | mixed | Storage capacity for a resource: capacity, stored, haulers | stack_limit | ThingDef.stackLimit |
| ExtractionWorkType | message | 944 | 2 | static | Work type and work givers that mine a deposit | definition;work_givers | Go rule over WorkGiverDef rows by workType (WorkTypeDef) |
| ExtractionSite | message | 945 | 9 | mixed | Candidate extraction building site with power and work types | power_w;work_types;resource | power_w = CompProperties_Power.basePowerConsumption on ThingDef.comps; work_types = Go rule over WorkGiverDef; resource = CompProperties_DeepDrill on ThingDef.comps |
| OwnedDrill | message | 946 | 10 | state | Owned drill with recovered amount, stock target, depletion |  |  |
| ExtractionDevelopment | message | 947 | 8 | mixed | Extraction development: deposits, definitions, costs, research, sites, owned drills | definitions;costs;research;flick_work_type | Go rule over ThingDef rows with deep-drill/mining comps; ThingDef.costList; ThingDef.researchPrerequisites; WorkGiverDef rows |
| ResourceSourcesSnapshot | message | 948 | 6 | state | Resource sources, storage and development for a resource |  |  |
| ResourceSourcesRequest | message | 949 | 3 | state | Read request parameters |  |  |
| ResourceSourcesReply | message | 950 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| ConsumptionRow | message | 958 | 3 | state | Realized consumption per definition and reason |  |  |
| ConsumptionHour | message | 959 | 2 | state | One hour of consumption rows |  |  |
| ConsumptionSnapshot | message | 960 | 3 | state | Hourly consumption ring window |  |  |
| ConsumptionRequest | message | 961 | 1 | state | Read request parameters |  |  |
| ConsumptionReply | message | 962 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| HandlerState | message | 963 | 5 | code | Handler skill/priority/eligibility for an animal |  |  |
| HusbandryAnimal | message | 964 | 3 | state | Animal with handlers |  |  |
| HusbandrySnapshot | message | 965 | 3 | state | Husbandry census |  |  |
| HusbandryRequest | message | 966 | 2 | state | Read request parameters |  |  |
| HusbandryReply | message | 967 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| WasteLocation | enum | 968 | 4 | state | Waste item location state (exposed/relocated/buried) |  |  |
| WasteItem | message | 969 | 11 | state | Waste item row |  |  |
| WasteSnapshot | message | 975 | 2 | state | Waste items |  |  |
| WasteReply | message | 976 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| RecoveryRestriction | message | 977 | 4 | state | Recovery area restriction/lease on a pawn |  |  |
| RecoverySnapshot | message | 978 | 3 | state | Recovery restrictions and buildings |  |  |
| RecoveryRequest | message | 979 | 1 | state | Read request parameters |  |  |
| RecoveryReply | message | 980 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| PopulationPerson | message | 983 | 25 | state | Prisoner/guest/colonist population facts |  |  |
| PopulationSnapshot | message | 1015 | 11 | state | Population persons, ideology flags and storyteller outlook |  |  |
| OwnedName | message | 1032 | 3 | state | Owned pawn short name and thing id |  |  |
| PopulationRequest | message | 1033 | 1 | state | Read request parameters |  |  |
| PopulationReply | message | 1034 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| Settlement | message | 1035 | 13 | state | World settlement row |  |  |
| WorldTile | message | 1036 | 17 | state | World tile climate and geography row |  |  |
| WorldSnapshot | message | 1043 | 3 | state | World tile plus settlements |  |  |
| WorldRequest | message | 1044 | 3 | state | Read request parameters |  |  |
| WorldReply | message | 1045 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| WorldRoute | message | 1046 | 9 | code | Route estimate to a settlement or home |  |  |
| CaravanState | message | 1047 | 14 | state | Caravan row with pawns, inventory, routes |  |  |
| QuestTradeRequest | message | 1053 | 3 | state | Quest trade request resource/count/destination |  |  |
| QuestReward | message | 1054 | 11 | state | Quest reward choice |  |  |
| QuestObjectiveKind | enum | 1055 | 15 | state | Quest objective kind tag |  |  |
| QuestObjective | message | 1072 | 19 | state | Quest objective details (item, count, deadline, sub-objectives) |  |  |
| QuestHackTarget | message | 1094 | 6 | excluded | Odyssey quest hack target |  |  #2632 Odyssey/Race |
| QuestGiftRequest | message | 1095 | 7 | state | Quest gift request state |  |  |
| QuestHackRisk | message | 1096 | 2 | excluded | Odyssey quest hack risk |  |  #2632 Odyssey/Race |
| QuestSurveyScanner | message | 1097 | 7 | excluded | Odyssey quest survey scanner |  |  #2632 Odyssey/Race |
| QuestGravEngine | message | 1102 | 7 | excluded | Odyssey quest grav engine |  |  #2632 Odyssey/Race |
| WorldSiteState | enum | 1107 | 5 | state | World site lifecycle state tag |  |  |
| WorldSite | message | 1108 | 18 | state | World site row |  |  |
| QuestSiteMiningTarget | message | 1118 | 5 | excluded | Odyssey site mining target |  |  #2632 Odyssey/Race |
| QuestSiteSecurity | message | 1119 | 9 | state | Site security threat facts |  |  |
| QuestSiteExtraction | message | 1124 | 9 | state | Site extraction crew, cargo and routes |  |  |
| QuestSiteCargo | message | 1132 | 7 | mixed | Site cargo item with mass, value, nutrition | unit_mass;market_value;nutrition | catalog stat table (statBases Mass, MarketValue, ThingDef.ingestible nutrition x stuff) |
| QuestSiteHomeRoute | message | 1137 | 4 | code | Route from site to a home map |  |  |
| QuestPeaceTalks | message | 1141 | 5 | state | Peace talks goodwill facts |  |  |
| QuestWorkload | message | 1146 | 4 | code | Monument workload recipe/stat/work/rate factor |  |  |
| QuestLodgerMood | message | 1147 | 2 | state | Lodger mood |  |  |
| QuestMonument | message | 1148 | 17 | state | Quest monument site state: placement, pieces, resources |  |  |
| QuestMonumentPiece | message | 1168 | 10 | mixed | Monument piece with build options and footprint | allowed_stuffs;footprint;build_options | Go rule over ThingDef rows: stuffCategories, size/rotation; build options per QuestMonumentBuildOption |
| QuestMonumentBuildOption | message | 1180 | 3 | static | Build option per stuff: costs and work | stuff;costs;work | Go rule over ThingDef.costList/costStuffCount and statBases WorkToBuild x stuff rows |
| QuestWorker | message | 1181 | 9 | code | Pawn quest suitability (healthy, can fight, work rates, carry) |  |  |
| QuestWorkRate | message | 1182 | 2 | code | Work rate for a stat |  |  |
| QuestMonumentResource | message | 1183 | 5 | state | Monument resource thing location |  |  |
| QuestShuttleState | message | 1190 | 10 | state | Quest shuttle loading state |  |  |
| QuestState | message | 1202 | 24 | state | Quest row |  |  |
| FactionState | message | 1203 | 7 | state | Faction relation row |  |  |
| WorldMap | message | 1204 | 7 | state | Map row with pawns and stored items |  |  |
| CaravanAssembly | message | 1205 | 5 | state | Caravan assembly state |  |  |
| WorldProgressionSnapshot | message | 1206 | 7 | state | World progression: maps, factions, caravans, quests, sites |  |  |
| WorldProgressionRequest | message | 1207 | 2 | state | Read request parameters |  |  |
| WorldProgressionReply | message | 1208 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| BillsSnapshot | message | 1211 | 2 | state | Bills per bench |  |  |
| BillsRequest | message | 1212 | 3 | state | Read request parameters |  |  |
| BillsReply | message | 1213 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| RecipesSnapshot | message | 1214 | 4 | state | Recipes available at a bench |  |  |
| RecipesRequest | message | 1215 | 2 | state | Read request parameters |  |  |
| RecipesReply | message | 1216 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| BuildingSettingsRequest | message | 1217 | 2 | state | Read request parameters |  |  |
| BuildingSettingsReply | message | 1218 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| PawnSettingsRequest | message | 1219 | 2 | state | Read request parameters |  |  |
| PawnSettingsReply | message | 1220 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| ResolveTargetSnapshot | message | 1221 | 3 | state | Target resolution candidates |  |  |
| ResolveTargetRequest | message | 1222 | 4 | state | Read request parameters |  |  |
| ResolveTargetReply | message | 1223 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| GearCandidate | message | 1224 | 4 | code | Gear candidate with computed gain and blocker |  |  |
| GearLoadout | message | 1232 | 12 | state | Pawn equipment, gear candidates and wear inputs |  |  |
| GearLoadoutOption | message | 1240 | 11 | mixed | Apparel option for the loadout model | research;ingredients;smokepop | RecipeDef.researchPrerequisite(s); RecipeDef.ingredients + ThingDef.costList/costStuffCount; smokepop = ThingDef verbs (VerbProperties) not in defs yet |
| GearLoadoutModel | message | 1251 | 3 | code | Loadout model inputs: traits, worn, options |  |  |
| GearSnapshot | message | 1254 | 7 | state | Gear per pawn, climate by twelfth, active weather, stored apparel |  |  |
| GearStock | message | 1265 | 5 | state | Aggregated stored apparel row |  |  |
| GearStorage | message | 1266 | 1 | state | Stored apparel aggregate |  |  |
| GearWeatherCondition | message | 1267 | 3 | state | Active cold snap or heat wave |  |  |
| GearRequest | message | 1272 | 2 | state | Read request parameters |  |  |
| GearReply | message | 1273 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| Trader | message | 1274 | 10 | state | Trader row |  |  |
| TradersSnapshot | message | 1275 | 3 | state | Traders and negotiators |  |  |
| TradersRequest | message | 1276 | 1 | state | Read request parameters |  |  |
| TradersReply | message | 1277 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| PriceType | enum | 1279 | 7 | static | Vanilla PriceType enum | all | defs.proto enum PriceType |
| TradeLine | message | 1280 | 35 | mixed | Trade sheet line: counts, prices, pawn/food facts | category;market_value;currency | ThingDef.thingCategories / statBases MarketValue x stuff x quality (catalog stat table); currency = Go rule over ThingDef (silver) |
| TradeCurrencyKind | enum | 1287 | 3 | static | Session currency tag (silver/favor) | all | defs.proto enum TradeCurrency |
| TradeSession | message | 1291 | 4 | state | Live trade session pair |  |  |
| TradeSessionRequest | message | 1292 | 1 | state | Read request parameters |  |  |
| TradeSessionReply | message | 1293 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| TradeSheet | message | 1294 | 13 | state | Open trade sheet |  |  |
| TradeSheetRequest | message | 1295 | 5 | state | Read request parameters |  |  |
| TradeSheetReply | message | 1296 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| TradeAcquisitionRequest | message | 1299 | 2 | state | Trade acquisition request with pack intent |  |  |
| TradeConsole | message | 1300 | 2 | state | Comms console with negotiators |  |  |
| PassingTradeShip | message | 1301 | 6 | state | Passing trade ship |  |  |
| TradeRequestOption | message | 1302 | 13 | code | Trade request option eligibility and cost |  |  |
| TradeAcquisition | message | 1317 | 8 | state | Trade acquisition prerequisites |  |  |
| TradeRequestArrival | message | 1327 | 6 | state | Pending trade request arrival |  |  |
| TradeCommsWork | message | 1328 | 4 | state | Comms work assignment |  |  |
| TradePackEstimate | message | 1329 | 8 | code | Caravan pack estimate |  |  |
| TradeAcquisitionReply | message | 1339 | 3 | state | Reply envelope (observed/unavailable/failure) |  |  |
| FoodConsumer | message | 1341 | 3 | code | Pawn nutrition per day and human-meat acceptance |  |  |
| FoodStock | message | 1347 | 4 | state | Food stock with nutrition and eaters |  |  |
| CorpseHandling | message | 1349 | 5 | state | Corpse in field or storage |  |  |
| FoodLarderFacts | message | 1350 | 4 | code | Raw meat and cook demand nutrition, cold sites |  |  |
| FoodSupplyFacts | message | 1351 | 3 | code | Food consumers, stocks and larder |  |  |
| CropForecast | message | 1352 | 9 | code | Per-zone crop work and yield forecast |  |  |
| PatientForecast | message | 1353 | 9 | code | Patient bleed and mood break forecast |  |  |
| ForecastFacts | message | 1354 | 4 | code | Forecast facts bundle |  |  |
| ComfortSurface | message | 1355 | 3 | state | Comfort surface cells |  |  |
| ComfortFacility | message | 1356 | 5 | code | Dining or recreation facility with users |  |  |
| JoyTolerance | message | 1359 | 3 | state | Per-pawn joy tolerance and boredom |  |  |
| RecreationCensus | message | 1360 | 2 | state | Recreation kind census |  |  |
| ComfortFacts | message | 1361 | 5 | state | Comfort facts bundle |  |  |
| UpkeepItem | message | 1362 | 11 | mixed | Upkeep item: hp, rot, deterioration, storage | max_hit_points;deterioration_rate | ThingDef.statBases MaxHitPoints and Deterioration x stuff (catalog stat table) |
| UpkeepBed | message | 1363 | 15 | mixed | Upkeep bed: slots, owners, users, quality | slots;humanlike;rest_effectiveness | ThingDef.size (slots), BuildingProperties.bed_humanlike, ThingDef.statBases BedRestEffectiveness x stuff |
| ItemStorageCapacity | message | 1364 | 3 | code | Unreserved covered storage capacity for an item |  |  |
| UpkeepStructure | message | 1365 | 6 | mixed | Upkeep structure: home, roof holder, flammability | holds_roof;flammability | ThingDef.holdsRoof; ThingDef.statBases Flammability x stuff |
| FireState | message | 1366 | 4 | state | Fire size and safe workers |  |  |
| FilthState | message | 1367 | 6 | state | Filth thickness and room |  |  |
| UpkeepPerson | message | 1373 | 8 | state | Pawn comfort temperature range, partners, title |  |  |
| BedroomThingRequirement | message | 1377 | 2 | static | Bedroom requirement: any-of defs and count | any_of;count | RoyalTitleDef.bedroomRequirements (Opt_RoomRequirementAny) |
| RoyalTitleFacts | message | 1378 | 6 | mixed | Holder royal title with bedroom requirements | def_name;seniority;bedroom_min_area;bedroom_min_impressiveness;bedroom_floored;bedroom_things | RoyalTitleDef.seniority and bedroomRequirements |
| PermitCooldown | message | 1383 | 3 | state | Permit last used tick and cooldown remaining |  |  |
| PawnRoyalHolding | message | 1384 | 6 | state | Pawn royal holding: title, favor, permits |  |  |
| PsycastTargetKind | enum | 1395 | 5 | static | Psycast target kind tag (self/pawn/thing/cell) | all | Go rule over AbilityDef.verbProperties targeting |
| PawnPsycast | message | 1396 | 7 | mixed | Known psycast with level, cost, cooldown | def_name;level;psyfocus_cost;entropy;target_kind;cooldown_ticks | AbilityDef.level, cooldownTicksRange, psyfocusCostRange, comps; target_kind = Go rule over AbilityDef.verbProperties |
| NeuroformerStock | message | 1397 | 5 | mixed | Neuroformer stock with held/craftable/tradeable | teaches_psycast | Go rule over ThingDef.comps (neurotrainer ability) rows |
| PawnRoyalty | message | 1399 | 5 | state | Pawn royalty holdings, psycasts, psyfocus, entropy |  |  |
| BestowingCeremony | message | 1409 | 10 | state | Pending bestowing ceremony |  |  |
| RoyalThrone | message | 1414 | 3 | state | Player throne and owner |  |  |
| RoyaltySection | message | 1418 | 2 | state | Royalty colony section envelope |  |  |
| RoyaltyColonyFacts | message | 1419 | 3 | state | Neuroformers, ceremonies, thrones |  |  |
| AnimalFeed | message | 1422 | 4 | mixed | Animal feed diet and pen requirement | diet | RaceProperties.foodType (ThingDef.race) |
| DevelopmentPower | message | 1423 | 5 | mixed | Power consumer/battery storage and turret dps | capacity_watt_days;turret_dps | capacity_watt_days = CompProperties_Battery.storedEnergyMax; turret_dps = Go rule over VerbProperties (burst, warmup, damage) |
| DevelopmentFurniture | message | 1426 | 3 | mixed | Furniture indoors flag and slots | slots | ThingDef.size / BuildingProperties bed slots |
| SteamGeyser | message | 1431 | 3 | state | Steam geyser cells and occupancy |  |  |
| DevelopmentFacts | message | 1432 | 5 | state | Development power, furniture, networks, geysers |  |  |
| EnvironmentCondition | message | 1433 | 6 | mixed | Active game condition with id, ticks left | def_name;implementation;label | GameConditionDef.defName, conditionClass, label |
| FoodClimate | message | 1444 | 8 | code | Growing-day calendar computed from biome temperatures and season |  |  |
| FarmFacts | message | 1452 | 19 | mixed | Per-growing-zone plant growth state plus the crop's static growth range and yield | edible_crop; nutrition_per_harvest_cell; min_growth_temperature; min_optimal_growth_temperature; max_optimal_growth_temperature; max_growth_temperature | PlantProperties.minGrowthTemperature, minOptimalGrowthTemperature, maxOptimalGrowthTemperature, maxGrowthTemperature; nutrition_per_harvest_cell = PlantProperties.harvestYield x harvestedThingDef nutrition stat (Go rule over crop row); edible_crop = harvestedThingDef ingestible (Go rule) |
| GrowLight | message | 1466 | 8 | mixed | Sun lamp state: powered, lit, grown cells, net | power_w | CompProperties_Power.basePowerConsumption on the building def |
| PlantGrower | message | 1467 | 11 | mixed | Hydroponics basin state: powered, fertility, cells, sow tag | power_w; sow_tag | power_w = CompProperties_Power.basePowerConsumption; sow_tag = building def sowTag (verify field in ThingDef) |
| GrowRoom | message | 1468 | 7 | state | Roofed grow room temperature, cell and lit counts |  |  |
| PowerHeadroom | message | 1469 | 8 | state | Per-network generation, consumption and battery state |  |  |
| ControlledEnvironment | message | 1470 | 7 | state | Controlled-environment lights, growers, rooms, networks, daylight, weather |  |  |
| EdibleCrop | message | 1473 | 3 | state | Per-map demand and diet allowance for an edible crop (static row is in the catalog) |  |  |
| PlanningFacts | message | 1478 | 4 | state | Planning bundle: gear, environment, crops |  |  |
| FoodProduct | message | 1483 | 5 | state | Product count, edibility, demand and storage room (static row is in the catalog) |  |  |
| FoodProduction | message | 1487 | 3 | state | Recipe availability and its products |  |  |
| CookingFacts | message | 1488 | 8 | state | Cooking bench usability, recipes, bills, production, refuel flag |  |  |
| AcquisitionFacts | message | 1493 | 22 | mixed | Per-source hunt or plant acquisition row | revenge_chance; body_size; predator; yield (plant rows); nutrition_yield (plant rows); tree | RaceProperties.manhunterOnDamageChance; RaceProperties.baseBodySize (animal.BodySize is age-scaled so keep if juveniles matter); RaceProperties.predator; PlantProperties.harvestYield; nutrition_yield = harvestYield x harvestedThingDef nutrition stat (Go rule); tree = PlantProperties.IsTree |
| HuntProjectileKind | enum | 1510 | 4 | code | Verb projectile class classification (Bullet/Arrow/Other) from ThingDef class and damage def |  |  |
| HuntVerbFacts | message | 1511 | 8 | static | Hunting weapon verb facts all read from VerbProperties and projectile defs | melee; ai_weapon; range; projectile_kind; explosion_radius; damage_def; damage_worker; warmup | VerbProperties.range; VerbProperties.warmupTime; VerbProperties.ai_IsWeapon; VerbProperties.defaultProjectile then ProjectileProperties.explosionRadius and damageDef; melee = verbClass rule; damage_worker via DamageDef.workerClass |
| HuntWeaponFacts | message | 1512 | 4 | static | Weapon def name with ranged/melee flags and its verbs | def_name; ranged; melee; verbs | ThingDef verbs for the equipped weapon def; ranged and melee = Go rule over verbs |
| HunterFacts | message | 1522 | 14 | mixed | Free colonist hunting eligibility, weapon and routes | weapon (nested HuntWeaponFacts) | ThingDef verbs via the equipped weapon def_name |
| HuntRoute | message | 1532 | 3 | code | Per colonist and prey route safety verdict |  |  |
| HuntButcherBill | message | 1536 | 7 | state | Butcher-flesh bill state on a bench |  |  |
| HuntButcherBench | message | 1541 | 3 | state | Butcher bench usability and its bills |  |  |
| HuntCensus | message | 1542 | 2 | state | Hunters and butcher benches |  |  |
| ButcheringFacts | message | 1543 | 9 | mixed | Butcher bench state plus human corpse yield | human_corpse_nutrition; human_corpse_def | human corpse nutrition = meat amount x RaceProperties.meatDef nutrition for the human race row (Go rule over catalog race row); human_corpse_def = human corpse ThingDef name (constant, GameConstants #2626) |
| HumanButcherCandidate | message | 1544 | 3 | state | Colonist able to butcher humans with traits |  |  |
| FoodCorpse | message | 1545 | 5 | mixed | Corpse stock row with freshness, reachability and meat yield | meat_def; nutrition_yield | RaceProperties.meatDef; yield = meat amount x meatDef nutrition (Go rule over race row) |
| ChoiceDialogOption | message | 1559 | 6 | state | One option of a force-pausing dialog |  |  |
| ChoiceDialog | message | 1560 | 6 | state | Open force-pausing dialog window |  |  |
| ComfortSection | message | 1563 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| FoodSupplySection | message | 1564 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| ForecastSection | message | 1565 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| DevelopmentSection | message | 1566 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| PlanningSection | message | 1567 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| WallRemovalRecord | message | 1568 | 7 | state | Wall removal job progress record |  |  |
| WallRemovalFacts | message | 1569 | 2 | state | Wall removal records |  |  |
| WallRemovalSection | message | 1570 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| HomeExtentGeometry | message | 1574 | 3 | state | Home extent cell sets |  |  |
| HomeCoverageTarget | message | 1575 | 8 | state | Home coverage target state |  |  |
| HomeCoverageFacts | message | 1576 | 2 | state | Home coverage targets and revision |  |  |
| HomeCoverageSection | message | 1577 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| WorkLightCell | message | 1586 | 6 | mixed | Measured glow at a bench work cell | light_sensitive (plant side) | PlantProperties.diesToLight over plants in the room (Go rule) |
| LampState | message | 1587 | 4 | mixed | Lamp lit state and glow radius | glow_radius | CompProperties_Glower.glowRadius on the building def |
| LightingFacts | message | 1588 | 2 | state | Work light cells and lamps |  |  |
| LightingSection | message | 1589 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| FloorCell | message | 1596 | 3 | state | Terrain under a room cell and pending floor |  |  |
| FloorRoom | message | 1598 | 3 | state | Room role and floor cells |  |  |
| FlooringFacts | message | 1600 | 1 | state | Rooms with floor cells |  |  |
| FlooringSection | message | 1601 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| TrafficLayer | enum | 1611 | 6 | state | Tags which pawn class a traffic sample counts |  |  |
| RouteTravel | message | 1622 | 5 | code | Per colonist reachability and path cost to a facility |  |  |
| RouteBreach | message | 1623 | 4 | state | Breach candidate wall cell |  |  |
| RouteFacilityKind | enum | 1624 | 7 | state | Tags the kind of facility a route targets |  |  |
| RouteFacility | message | 1625 | 6 | state | Facility with per colonist travel and breaches |  |  |
| TrafficCell | message | 1626 | 6 | state | Decayed traffic sample count per cell and layer |  |  |
| RoutesFacts | message | 1627 | 5 | state | Facility routes and traffic census |  |  |
| RoutesSection | message | 1628 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| UpkeepFacts | message | 1629 | 20 | state | Upkeep bundle: items, beds, structures, fires, filth, people, animals, sections, home area |  |  |
| UpkeepSection | message | 1649 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| ThreatFacts | message | 1657 | 10 | code | Colony wealth and raid points computed by the storyteller |  |  |
| ThreatSection | message | 1658 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| LootItem | message | 1665 | 6 | code | Haul safety verdict, path length and storage headroom for an item |  |  |
| LootCensus | message | 1666 | 3 | state | Loot items with free hauler count and storyteller quiet flag |  |  |
| LootSection | message | 1667 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| FishableCell | message | 1679 | 3 | excluded | Fishable water cell reachability (Odyssey fishing) |  |  excluded #2632 (FishableWater absent without Odyssey); state or code |
| FishingZoneFacts | message | 1680 | 4 | excluded | Fishing zone settings (Odyssey) |  |  excluded #2632; state |
| FishableRegion | message | 1681 | 14 | excluded | Water body fishing facts (Odyssey) | nutrition_per_fish; fish_per_batch (curve part) | nutrition_per_fish = nutrition stat of the fish ThingDefs; yield curve is a FishingUtility constant (GameConstants #2626) excluded #2632; flagged for the owner |
| FisherFacts | message | 1690 | 2 | excluded | Fisher stat values (Odyssey) |  |  excluded #2632; state or code |
| FishableWater | message | 1691 | 10 | excluded | Fishable water bodies, research and fishers (Odyssey) | research_base_cost; research_cost_factor; research_points_per_work_tick; base_fishing_duration_ticks | ResearchProjectDef.baseCost; research cost factor; ResearchManager.ResearchPointsPerWorkTick and FishingUtility.BaseFishingDurationTicks as GameConstants (#2626) excluded #2632; flagged for the owner; research_progress and difficulty factor are state |
| GatherableAnimal | message | 1698 | 9 | code | Tame animal milk or wool gather rates |  |  |
| EggLayerAnimal | message | 1699 | 8 | code | Tame egg layer production rates |  |  |
| PasteDispenser | message | 1700 | 4 | state | Nutrient paste dispenser power and hopper state |  |  |
| ForagePlant | message | 1703 | 3 | code | Climate growing twelfths for a forage plant |  |  |
| PenGrazing | message | 1704 | 4 | code | Pen grazing demand and pasture rates |  |  |
| FoodSlaughterAnimal | message | 1705 | 5 | mixed | Slaughter candidate meat and feed figures | meat_nutrition; feed_per_day; reproduction_days | meat_nutrition = meatAmount stat x RaceProperties.meatDef nutrition (Go rule over race row); feed_per_day = existing race adult_feed_per_day; reproduction_days = RaceProperties.gestationPeriodDays |
| FoodChannelsFacts | message | 1706 | 8 | state | Food channel census: water, animals, paste, forage, grazing, slaughter |  |  |
| FoodChannelsSection | message | 1707 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| DeepResourceLump | message | 1711 | 4 | state | Discovered deep resource lump |  |  |
| MineralScannerState | message | 1717 | 8 | mixed | Mineral scanner state | target_resource | building mineableThing config on the scanner def |
| DeepDrillState | message | 1724 | 8 | state | Deep drill state and next deposit |  |  |
| DeepResourcesFacts | message | 1727 | 4 | state | Lumps, scanners and drills |  |  |
| DeepResourcesSection | message | 1728 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| DeliverySourceKind | enum | 1736 | 5 | state | Tags the production source of a delivery row |  |  |
| DeliveryRow | message | 1737 | 6 | state | Cumulative delivery counter row |  |  |
| KillRecord | message | 1747 | 6 | state | Animal kill event record |  |  |
| ButcherRecord | message | 1752 | 7 | state | Butcher event record |  |  |
| DeliveryLedgerFacts | message | 1756 | 8 | state | Delivery counters epoch, kills, butchers |  |  |
| DeliveryLedgerSection | message | 1758 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| ColonyFactsSnapshot | message | 1759 | 52 | mixed | Colony facts root: counts, food, sections | policy_resources (dead); player_tech_level (faction def) | policy_resources is never written natively: delete; player_tech_level = FactionDef.techLevel of the player faction (def key, keep as state name) |
| OdysseySection | message | 1811 | 2 | excluded | Odyssey section wrapper |  |  excluded #2632 |
| OdysseyColonyFacts | message | 1812 | 4 | excluded | Odyssey colony facts |  |  excluded #2632 |
| ActiveCondition | message | 1820 | 7 | excluded | Active game condition (Odyssey section) |  |  excluded #2632 |
| HazardTerrain | message | 1824 | 6 | excluded | Hazard terrain defs on the map (Odyssey section) | dangerous; burn_damage; heat_per_tick; toxic_buildup_factor | TerrainDef fields in defs.proto excluded #2632; flagged for the owner |
| LavaEmergenceState | message | 1828 | 2 | excluded | Lava emergence vent (Odyssey) |  |  excluded #2632 |
| UndergroundSite | message | 1832 | 6 | excluded | Ancient hatch pocket map (Odyssey) |  |  excluded #2632 |
| UndergroundHackable | message | 1836 | 8 | excluded | Hackable thing on a pocket map (Odyssey) |  |  excluded #2632 |
| AnomalySection | message | 1849 | 2 | excluded | Anomaly section wrapper |  |  excluded #2631 |
| AnomalyColonyFacts | message | 1850 | 7 | excluded | Anomaly colony facts |  |  excluded #2631 |
| KnowledgeProgress | message | 1858 | 4 | excluded | Anomaly knowledge category progress |  |  excluded #2631 |
| CodexProgress | message | 1862 | 3 | excluded | Anomaly codex category progress |  |  excluded #2631; entries is a def count |
| HeldEntity | message | 1863 | 2 | excluded | Anomaly held entity |  |  excluded #2631 |
| AnomalyIncidentState | message | 1871 | 12 | excluded | Anomaly gating state |  |  excluded #2631 |
| MonolithState | message | 1902 | 15 | excluded | Void monolith state |  |  excluded #2631; next_level_* copy MonolithLevelDef fields (flagged for owner) |
| PolicyEntry | message | 1918 | 6 | state | Policy database row and holders |  |  |
| FoodKind | enum | 1926 | 14 | code | Tags a food ThingDef by game classification (meal tier, meat source, kibble) |  |  |
| MealIngredients | enum | 1933 | 4 | code | Tags meal ingredient restriction (meat-only, meat-free, any) |  |  |
| AllowedAreaEntry | message | 1936 | 3 | state | Allowed area and restricted pawns |  |  |
| PolicyFacts | message | 1937 | 7 | mixed | Policy databases, allowed areas and food eaters | biome_diseases | BiomeDef.diseases[].diseaseInc mapped to the IncidentDef hediff (Go rule over BiomeDef row of the colony biome) |
| FoodEaterKind | enum | 1949 | 3 | state | Tags prisoner or animal food policy holder |  |  |
| FoodEater | message | 1950 | 4 | state | Prisoner or animal food policy holder with traits and precepts |  |  |
| PolicySection | message | 1951 | 2 | state | Unavailable-or-observed oneof wrapper |  |  |
| BiotechSection | message | 1966 | 2 | excluded | Biotech section wrapper |  |  excluded #2630 |
| BiotechColonyFacts | message | 1967 | 14 | excluded | Biotech colony facts |  |  excluded #2630 |
| GeneBankState | message | 1985 | 7 | excluded | Gene bank building state |  |  excluded #2630; capacity is def data (flagged) |
| GeneAssemblerState | message | 1994 | 12 | excluded | Gene assembler state |  |  excluded #2630 |
| GeneExtractorState | message | 2002 | 9 | excluded | Gene extractor state |  |  excluded #2630 |
| GenepackState | message | 2009 | 11 | excluded | Genepack thing state |  |  excluded #2630; complexity, metabolism, archites are GeneDef sums (flagged) |
| XenogermState | message | 2016 | 10 | excluded | Xenogerm thing state |  |  excluded #2630 |
| XenogermImplantMetabolism | message | 2025 | 2 | excluded | Per pawn metabolism after implant |  |  excluded #2630 |
| PollutionTotals | message | 2026 | 6 | excluded | Pollution grid totals |  |  excluded #2630 |
| Polluter | message | 2035 | 5 | excluded | Polluting building |  |  excluded #2630; cells_per_day is def data (flagged) |
| Wastepack | message | 2041 | 10 | excluded | Wastepack stack |  |  excluded #2630 |
| WastepackAtomizer | message | 2047 | 8 | excluded | Wastepack atomizer state |  |  excluded #2630 |
| PollutionPump | message | 2051 | 5 | excluded | Pollution pump state |  |  excluded #2630 |
| MechGestatorState | message | 2057 | 12 | excluded | Mech gestator bill state |  |  excluded #2630 |
| MechChargerState | message | 2063 | 7 | excluded | Mech charger state |  |  excluded #2630 |
| BabyCare | message | 2070 | 6 | excluded | Baby care verdicts |  |  excluded #2630 |
| BabyAutofeeder | message | 2074 | 2 | excluded | Baby autofeeder mode |  |  excluded #2630 |
| JoinerLetter | message | 2076 | 7 | state | Wanderer or creepjoiner join offer letter |  |  |
| ColonyFactsRequest | message | 2081 | 4 | state | Request parameters, no game data |  |  |
| ColonyFactsReply | message | 2082 | 3 | state | Reply oneof wrapper |  |  |
| ThreatPawn | message | 2100 | 15 | mixed | Non-colonist threat pawn facts | predator | RaceProperties.predator via the pawn's race def (join pawn table race) |
| ThreatBuilding | message | 2112 | 9 | mixed | Hostile building threat facts | mortar; max_hit_points | mortar = Go rule over ThingDef building.IsMortar (turret verb); max_hit_points = stat MaxHitPoints on the def (stuff dependent so partly code) |
| ThreatsSnapshot | message | 2113 | 2 | state | Threat pawns and hostile buildings |  |  |
| StatusSnapshot | message | 2116 | 4 | state | Emergency status snapshot |  |  |
| StatusRequest | message | 2117 | 4 | state | Request parameters, no game data |  |  |
| StatusReply | message | 2118 | 3 | state | Reply oneof wrapper |  |  |
| BundleSnapshot | message | 2121 | 36 | state | Stream frame root: carries all per-frame sections and watermarks |  |  |
| SectionWatermark | message | 2218 | 5 | state | Per-section stream change counter, captured tick and delta base |  |  |
| ObservationBatchSnapshot | message | 2219 | 9 | state | Batch of status, pawns, supplies, buildings, rooms, zones reads |  |  |
| ObservationBatchRequest | message | 2224 | 7 | state | Request envelope for the batch read |  |  |
| ObservationBatchReply | message | 2225 | 3 | state | Outcome oneof of batch read |  |  |
| ArchitectCategory | message | 2227 | 5 | mixed | Architect menu category id, label and live visible/enabled flags | id,label,designator_count | DesignationCategoryDef.defName/label; designator_count = Go rule over designator rows |
| ArchitectDesignator | message | 2228 | 11 | mixed | Architect designator with buildable and per-frame enabled state | id,category_id,label,buildable_def_name,buildable_label,application_kind,supports_cell,supports_rectangle | ThingDef.defName/label for buildable; category from DesignationCategoryDef; supports_* a Go rule over application_kind (designator class via class_chains) |
| ArchitectCategoriesSnapshot | message | 2234 | 2 | state | Snapshot wrapper of architect categories |  |  |
| ArchitectCategoriesRequest | message | 2235 | 3 | state | Request envelope |  |  |
| ArchitectCategoriesReply | message | 2236 | 3 | state | Outcome oneof |  |  |
| ArchitectDesignatorsSnapshot | message | 2237 | 2 | state | Snapshot wrapper of architect designators |  |  |
| ArchitectDesignatorsRequest | message | 2238 | 3 | state | Request envelope |  |  |
| ArchitectDesignatorsReply | message | 2239 | 3 | state | Outcome oneof |  |  |
| SnapshotStreamRequest | message | 2247 | 5 | state | Stream subscription: resource sources, keyframe flag, budgets |  |  |
| DefinitionCatalog | message | 2265 | 12 | static | Catalog envelope holding def mirror, stat table, facts, constants | thing_defs,terrain_defs,defs,class_chains,research | already the def mirror itself (defs.proto ThingDef/TerrainDef/DefSets); research duplicates DefSets ResearchProjectDef rows |
| ClassChain | message | 2299 | 2 | static | CLR class and its bases for def class matching | name,bases | none in defs.proto; reflection over assemblies; keep as Go ClassIsA index |
| ThingDefFacts | message | 2310 | 7 | code | Game-computed ThingDef flags (food kind, meal ingredients, raw meat, medicine, room roles) |  |  |
| RaceFacts | message | 2329 | 15 | excluded | Game-computed race facts incl. husbandry ages and tameness |  | excluded owner owner Race |
| DefStatTable | message | 2370 | 3 | code | Game stat values per def and stuff (GetStatValueAbstract) |  |  |
| DefStatRow | message | 2384 | 5 | code | One def/stuff stat value row with adjusted costs |  |  |
| CatalogConstants | message | 2392 | 16 | static | Game constants read from assemblies | ticks_per_hour,ticks_per_day,days_per_year,bill_stack_max,skill_max_level,lit_glow_threshold,currency_def,full_rot_rate_c,roof_max_support_distance,wort_def,animal_interact_talk_ticks,animal_interact_feed_ticks,animal_interact_feeds,animal_feed_nutrition_fraction,animal_feed_nutrition_cap,min_train_interval_ticks | planned GameConstants (#2626) for all 16 fields |
| IdeologySnapshot | message | 2434 | 15 | state | Primary ideoligion: memes, precepts, roles, rituals, believers, reform state |  |  |
| IdeoPrecept | message | 2443 | 2 | state | Precept in force id and def name |  |  |
| IdeoRole | message | 2446 | 4 | state | Role precept active flag and holder pawns |  |  |
| IdeoRitual | message | 2451 | 7 | state | Ritual precept last finished tick, obligations, running |  |  |
| IdeoBuilding | message | 2453 | 3 | state | Building precept and ThingDef it takes |  |  |
| StatEffect | message | 2459 | 3 | excluded | Biotech stat modifier |  | excluded owner #2630 owner Biotech |
| SkillLevel | message | 2460 | 2 | excluded | Biotech skill level |  | excluded owner #2630 owner Biotech |
| CapacityEffect | message | 2461 | 4 | excluded | Biotech capacity effect |  | excluded owner #2630 owner Biotech |
| LifeStageRow | message | 2466 | 12 | excluded | Biotech LifeStageDef copy |  | excluded owner #2630 owner Biotech |
| LifeStageAgeRow | message | 2476 | 2 | excluded | Biotech life stage age |  | excluded owner #2630 owner Biotech |
| WorkMinAge | message | 2477 | 2 | excluded | Biotech work minimum age |  | excluded owner #2630 owner Biotech |
| RaceLifeStages | message | 2478 | 3 | excluded | Biotech race life stages |  | excluded owner #2630 owner Biotech |
| GeneRow | message | 2490 | 24 | excluded | Biotech GeneDef copy |  | excluded owner #2630 owner Biotech |
| PassionEffect | message | 2501 | 2 | excluded | Biotech passion effect |  | excluded owner #2630 owner Biotech |
| XenotypeRow | message | 2503 | 4 | excluded | Biotech XenotypeDef copy |  | excluded owner #2630 owner Biotech |
| MechWorkPriority | message | 2504 | 2 | excluded | Biotech mech work priority |  | excluded owner #2630 owner Biotech |
| MechKindRow | message | 2512 | 12 | excluded | Biotech mech kind |  | excluded owner #2630 owner Biotech |
| MechWorkModeRow | message | 2521 | 7 | excluded | Biotech MechWorkModeDef copy |  | excluded owner #2630 owner Biotech |
| CurvePointRow | message | 2526 | 2 | excluded | Biotech curve point |  | excluded owner #2630 owner Biotech |
| GeneTuningFacts | message | 2537 | 8 | excluded | Biotech gene tuning constants |  | excluded owner #2630 owner Biotech |
| BiotechCatalog | message | 2543 | 7 | excluded | Biotech catalog container |  | excluded owner #2630 owner Biotech |
| OdysseyCatalog | message | 2555 | 2 | excluded | Odyssey catalog container |  | excluded owner #2632 owner Odyssey |
| BiomeAnimal | message | 2559 | 2 | excluded | Odyssey biome animal commonality |  | excluded owner #2632 owner Odyssey |
| BiomeAnimals | message | 2562 | 4 | excluded | Odyssey biome animal tables |  | excluded owner #2632 owner Odyssey |
| StockpileTypeRow | message | 2568 | 2 | excluded | Odyssey stockpile type enum row |  | excluded owner #2632 owner Odyssey |
| OdysseyBuilding | message | 2573 | 3 | excluded | Odyssey building facts |  | excluded owner #2632 owner Odyssey |
| HackableState | message | 2575 | 5 | excluded | Odyssey hackable state |  | excluded owner #2632 owner Odyssey |
| PortalState | message | 2579 | 4 | excluded | Odyssey portal state |  | excluded owner #2632 owner Odyssey |
| AnomalyCatalog | message | 2586 | 8 | excluded | Anomaly catalog container |  | excluded owner #2631 owner Anomaly |
| CreepJoinerFormRow | message | 2595 | 7 | excluded | Anomaly creepjoiner form copy |  | excluded owner #2631 owner Anomaly |
| CreepJoinerBenefitRow | message | 2601 | 11 | excluded | Anomaly creepjoiner benefit copy |  | excluded owner #2631 owner Anomaly |
| CreepJoinerSkillRange | message | 2606 | 3 | excluded | Anomaly creepjoiner skill range |  | excluded owner #2631 owner Anomaly |
| CreepJoinerDownsideRow | message | 2611 | 21 | excluded | Anomaly creepjoiner downside copy |  | excluded owner #2631 owner Anomaly |
| EntityCategoryRow | message | 2621 | 3 | excluded | Anomaly EntityCategoryDef copy |  | excluded owner #2631 owner Anomaly |
| KnowledgeCategoryRow | message | 2624 | 3 | excluded | Anomaly KnowledgeCategoryDef copy |  | excluded owner #2631 owner Anomaly |
| EntityDiscoveryKind | enum | 2626 | 4 | excluded | Anomaly discovery type enum |  | excluded owner #2631 owner Anomaly |
| EntityCodexRow | message | 2630 | 10 | excluded | Anomaly codex entry copy |  | excluded owner #2631 owner Anomaly |
| AnomalyThingRow | message | 2642 | 10 | excluded | Anomaly thing def facts |  | excluded owner #2631 owner Anomaly |
| StudiableProps | message | 2650 | 12 | excluded | Anomaly CompProperties_Studiable copy |  | excluded owner #2631 owner Anomaly |
| HoldingTargetProps | message | 2657 | 5 | excluded | Anomaly holding target props copy |  | excluded owner #2631 owner Anomaly |
| EntityHolderProps | message | 2664 | 2 | excluded | Anomaly entity holder props copy |  | excluded owner #2631 owner Anomaly |
| AnomalyIncidentRow | message | 2669 | 12 | excluded | Anomaly IncidentDef copy |  | excluded owner #2631 owner Anomaly |
| EntityContainmentModeKind | enum | 2675 | 5 | excluded | Anomaly containment mode enum |  | excluded owner #2631 owner Anomaly |
| PawnAnomaly | message | 2691 | 11 | excluded | Anomaly pawn facts |  | excluded owner #2631 owner Anomaly |
| CreepJoinerState | message | 2703 | 3 | excluded | Anomaly creepjoiner state |  | excluded owner #2631 owner Anomaly |
| HeldState | message | 2715 | 10 | excluded | Anomaly held entity state |  | excluded owner #2631 owner Anomaly |
| StudyState | message | 2724 | 10 | excluded | Anomaly study state |  | excluded owner #2631 owner Anomaly |
| AnomalyBuilding | message | 2732 | 3 | excluded | Anomaly building facts |  | excluded owner #2631 owner Anomaly |
| EntityHolderState | message | 2739 | 4 | excluded | Anomaly holder state |  | excluded owner #2631 owner Anomaly |
| AnomalyDoor | message | 2745 | 5 | excluded | Anomaly holder door state |  | excluded owner #2631 owner Anomaly |
| DefinitionCatalogRequest | message | 2746 | 2 | state | Request envelope for the catalog read |  |  |
| CreationDefinitionCatalog | message | 2748 | 2 | static | Def mirror before a world exists | defs,class_chains | already the DefSets mirror itself |
| DefinitionCatalogReply | message | 2749 | 4 | state | Outcome oneof of catalog read |  |  |
| SnapshotStreamOpened | message | 2750 | 3 | state | Shared-memory ring name, slots and slot bytes |  |  |
| SnapshotStreamReply | message | 2755 | 3 | state | Outcome oneof of stream open |  |  |
| FlushSnapshotRequest | message | 2758 | 0 | state | Flush request (empty) |  |  |
| FlushSnapshotReply | message | 2759 | 2 | state | Outcome oneof of flush |  |  |
| TradeFoodFacts | message | 2806 | 1 | code | Nutrition of a food def for trade (game-classified) | nutrition | candidate: DefStatTable Nutrition stat or ThingDef.ingestible.nutrition |
| FoodRestriction | message | 2809 | 2 | state | Pawn food policy id and allowed defs |  |  |
| ApparelPolicyState | message | 2814 | 18 | state | Pawn apparel policy limits and requirements |  |  |
