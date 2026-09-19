# Observation contracts and coverage

`observations.proto` defines simulation reads and reusable settings snapshots.
It contains no command selector, mutation branch, arbitrary JSON field, `Any`, or
`Struct`. RPC descriptors identify fixed request/reply pairs at the shared MCP
ProtoJSON boundary; they do not require another server. Implementation and native
acceptance follow the complete contract review.

Source audit baseline: native/Python source at `916dad3a`, carried into foundation
`b8ce4538`. All 55 production home exports are accounted for below. The installed
SDK details used for external request coverage are historical discovery evidence,
not output-type authority. The Go team's consolidated consumer requirements cover
97 native/SDK boundaries, including privileged presentation separately.

## Food source channels

`ColonyFactsSnapshot.food_channels` is an independent census, with unavailable
outcomes for failed or oversized reads. It does not select food policy.
`NativeFoodChannels` reads it; `observation.ColonyProjection.FoodChannels` retains
optional values as `domain.Fact` after bridge validation.

Every tame animal has gatherable and egg rows; missing components leave production
values unknown and mark activity false. Unreadable activity stays unknown. Multiple
gatherable components produce one row per resource.
Handler reachability requires an available colonist with Handling enabled.
Milk and egg rows retain native activity, nutrition/day and remaining lead time;
milk also reports gathering work/day. Inactive comps contribute no food. Pen
rows carry demand, worst-quadrum pasture production and stored nutrition, once
per enclosed pen. Slaughter rows carry native meat nutrition, grazing demand
and reproduction interval; policy owns the removal opt-in and safety selection.
Paste dispensers report power, eligible hopper nutrition and the interaction
cell's room. Pollution counts the map-clipped center +/-22 planning window.
Forage lists biome wild plants with human-edible harvest products and native
average-temperature growing twelfths (0..11); these are seasonal potential,
not harvestable instances. Acquisition retains ownership of plant instances.

Fishing is absent without Odyssey. With Odyssey, each fish-bearing water body
has one row for its visible passable water cells, body-wide population/capacity,
whether any such cell belongs to a fishing zone, and reachability from the colony
anchor. The section also reports Fishing research. Each collection is bounded
by the requested page limit, at most 256; no per-cell fishing payload is emitted.
`tools/foodchannels` checks Core-only baseline forage and a cow's milk fullness;
`tools/saveheadroom-*` checks the envelope budget.

## Required semantic validation

- Every reply selects exactly one observed, unavailable, or request failure case.
  Observed snapshots have actual identity/tick/generation context. ReadScope checks
  expected identity only; advancing ticks or revoked authority never blocks
  reconciliation reads. Identity mismatch is stale, never silently rebound.
- Optional facts distinguish unavailable from observed zero/false. An omitted
  requested fact or repeated section requires a `ReadIssue` naming its proto field
  path. Omitted unrequested detail is not an empty observed section. Known-empty
  collections have explicit complete metadata and zero counts. The helper section
  oneofs preserve whole-section failure independently of sibling facts.
- Page limit defaults to 256 and must be 1..256. Each nested non-cell repeated
  collection is bounded to 256; geometry across the reply is at most 4096 cells.
  A larger exact entity/geometry returns LIMIT_EXCEEDED, or is exposed through a
  dedicated stable page before it can be considered complete. No sampled geometry,
  stock count, candidate, or diagnostic target can claim completeness.
- A whole serialized reply is at most 1 MiB. Overflow returns explicit unavailable,
  never success containing silently removed fields. Text follows common bounds.
  Stock/count quantities are nonnegative; trade transfer/minimum/maximum counts
  are signed (negative sells). Ratios and all measurements are finite. Units are named
  on facts; a percentage/fraction conversion is an adapter responsibility.
- Completeness is scoped to the exact query. `matched`, `returned`, `filtered`, and
  `unreadable` have distinct meanings. `complete=true` requires every matched row,
  no unreadable required facts, and no next cursor. Multi-page results bind the
  query and context to an opaque snapshot token; stale cursors are rejected.
  For multi-collection snapshots the cursor must cover the complete combined
  snapshot, not independently advancing nested collections without a common scope.
- `SnapshotRef` binds context, exact entity ID, and an opaque token for settings
  CAS. Tokens never mean permission or completed effects. Bill-stack tokens bind
  the bench and ordered stack, not only a bill index; trade tokens bind the session
  and absolute row identities. A label is never an identity.
- Definition IDs and native state names remain open strings. Protocol outcomes
  such as waste location are closed enums and unknown values are rejected.
  Native enums projected as strings are observations, not unchecked command modes.
- Collection cell coordinates come from current map dimensions; no map-size
  constants or sentinel coordinates. Missing world-map scope remains absent.
- Batch sections are sequential, not atomic. All nested scopes must match the
  outer expected identity; start/end context makes tick drift visible. A successful
  batch does not turn an unavailable child into complete data.

## Production source to typed boundary

Sources in this table are under
`integrations/rimgovernor-native/src/Bridge/`; consumer modules are under
`controller/rimgovernor/`. Each RPC has its individually named Request and Reply.

| Production source / export | Typed boundary and principal messages | Current consumers |
|---|---|---|
| ColonyIdentity.cs / colony_identity | lifecycle.ReadIdentity; no duplicate here | bridge_runtime, bridge_observation |
| StatusTool.cs / status | ReadStatus; StatusSnapshot, PawnState, ThreatsSnapshot. Clock and notification/UI blocks belong to clock/presentation | bridge_observation, combat_outcome, hunting_outcome, supervisor |
| ObservationBatchTool.cs / observation_batch | ReadObservationBatch; concrete before/pawns/supplies/buildings/rooms/zones/after replies | bridge_observation:74-136 |
| ListPawnsTool.cs / list_pawns | ListPawns; PawnState, Needs, Health, Hediff, Capacity, Equipment, Biography, Settings, Social, AnimalState, JobEvidence | medical_outcome, medical_management, medical_recovery, hunting, gear, animal_feed |
| ListThingsTool.cs / list_things | ListSupplies; ResourceStock, HeldStock, CorpseState | resource_accounting, food_forecast, waste, bridge_observation |
| ListBuildingsTool.cs / list_buildings | ListBuildings; BuildingState, ConstructionState, PowerNetwork, ThermalSide, BillStack | construction_preflight, development, thermal_control, service_recovery, spatial |
| ListRoomsTool.cs / list_rooms | ListRooms; RoomState, RoomStat, exact cells/beds/contents | spatial_site, room_actions, shelter_handoff, room_adoption |
| ZonesTool.cs / list_zones | ListZones; ZoneState, StockpileFilter, ZoneAnomaly | zone_settings, zone_reconciliation, spatial_site, hands |
| CellsPlusTool.cs / get_cells_plus | GetCells; CellState, CellThing, DesignationState | bridge_game, spatial, construction_preflight, hands |
| ResearchTool.cs / research without set | ReadResearch; ResearchSnapshot, ResearchProject, Researcher, ResearchSlot | research_control, research_intent, development |
| ColonyFactsTool.cs / colony_facts | ReadColonyFacts; concrete composition described below | colony_controller, food_forecast, colony_upkeep, development |
| SpatialAccessTool.cs / spatial_access; NativeSpatialAccessTool.cs / observations_read_spatial_access | ReadSpatialAccess; PawnAccess and AccessTarget (proto port registered for the defense layout access audit) | spatial, spatial_site, construction_preflight, defense layout (B06c) |
| NativeDefenseObservationTools.cs / observations_read_defense_site | ReadDefenseSite; DefenseCell cover fill, sight blocking, natural rock, door, home area and native map-edge reachability over at most 2048 cells | defense layout (B06c) |
| NativeDefenseObservationTools.cs / observations_read_lines_of_fire | ReadLinesOfFire; native GenSight line of sight and CoverUtility block chance per (firing, approach) pair, at most 64×64 | defense layout, defensive positioning (B06c) |
| RoofSupportTool.cs / roof_support | ReadRoofSupport; exact target, RoofSupportCell | wall/room upkeep and roof safety |
| ExcavationTool.cs / read_excavation_site | ReadExcavationSite; ExcavationCell per requested cell (fogged = unknown), site-level ExcavationSupport after counterfactual removal, worker/access evidence | staged room/corridor excavation (B06f) |
| WallUpgradeTool.cs / wall_upgrade_sites | ListWallUpgradeSites; WallUpgradeSite, material and worker evidence (native handler `NativeWallUpgradeObservationTools.cs`: a `target_id` lists that wall's replacement candidates per normal, no target lists cleanup rows naming the next same-stuff backup; the `wall/upgrade` case covers it) | wall_upgrade, colony_upkeep |
| ResourceAcquisitionTool.cs / resource_sources | ListResourceSources; ResourceSource, StorageCapacity, ExtractionDevelopment | resource_control, extraction_development, mining |
| HusbandryTool.cs / husbandry_facts | ReadHusbandry; HusbandryAnimal, TrainingEntry, HandlerState | husbandry, animal_feed |
| WasteTools.cs / waste_state | ReadWaste; WasteItem with exposed/relocated/buried state | waste, waste_outcome |
| RecoveryTools.cs / recovery_state | ReadRecovery; RecoveryArea, RecoveryRestriction, BuildingState | recovery, service_recovery |
| PopulationTool.cs / population without interaction | ReadPopulation; PopulationPerson, supported interactions | population, sleeping_upkeep |
| WorldTool.cs / world without show | ReadWorld; WorldTile, Settlement | world_progression, expedition_policy |
| WorldProgressionTool.cs / world_progression | ReadWorldProgression; WorldMap, FactionState, CaravanState, QuestState, CaravanAssembly | world_progression, expedition_policy, trade_policy |
| PlacementPreviewsTool.cs / placement_previews | placement.proto; exact ordered candidate request and evaluated/failure result | placement_previews, construction_preflight |
| PawnImageTool.cs / pawn_image | presentation.proto; scoped render capture and unavailable | player presentation |
| BillsTool.cs / bills list and recipes | ReadBills / ReadRecipes; BillStack, BillState, RecipeState, IngredientRequirement (native handler `NativeBillsObservationTools.cs`; recipe ingredient rows carry required counts only, no stock scan; the `bills/census` acceptance case covers it) | colony_skills, production_policy, gear/medical/resource benches, player inspections |
| BuildingConfigTool.cs / building_config read | ReadBuildingSettings; BuildingSettings, scoped token; gizmos in presentation | building_config, thermal_control, player inspections |
| PawnConfigTool.cs / pawn_config read | ReadPawnSettings; PawnSettings, scoped token | pawn_config, medical/work/settings readers |
| OrderTool.cs / order resolve | ResolveTarget; exact typed target or explicit ambiguity | hands, target resolution, player inspections |
| TradeTool.cs / list_traders, sheet, status | ListTraders / ReadTradeSheet / ReadTradeStatus; TradeLine absolute index, session snapshot token, validated native food nutrition/class/preparation/perishability/crop facts | trade_policy, trade_outcome, player inspections |
| CaravanTool.cs / caravan catalog | ReadCaravanCatalog; PawnEligibility, stock/routes/return storage, scoped token | expedition_policy, caravan outcomes |
| SupervisedPlayTool.cs / supervised_play status/events | clock.proto; lease status, journal, gaps and typed events | supervisor, native_scenario |
| InstallTool.cs / install without coordinates | ReadInstallStatus; PackedFurnitureState preserving inner identity | install outcome and player inspections |
| GearUpkeepTool.cs / gear_upkeep inspection | ReadGear; GearLoadout, GearCandidate, snapshot token | gear_upkeep, colony_facts planning |
| MedicalOperationsTool.cs / medical_operations catalog | ReadMedicalCatalog; MedicalCatalog, MedicalRecipe, exact body-part/medicine/practitioner facts | medical_operations, medical_outcome |

The remaining 22 production exports are operations/presentation-owned:
accept_quest, acquire_resource, cancel_construction, caravan_gift,
confirm_colony_names, dialog_text, fulfill_quest, husbandry_config, manage_waste,
place_building, play_until_event, player_input, production_policy, recover_service,
recovery_area, relieve_need, render_demand, upkeep_bed, upkeep_home, upkeep_wall,
video_stream, zone_cells. Trade preview is operation preview, not a mutation branch
on ReadTradeSheet. These operations reuse exact entity/settings observations or
their own narrow typed receipts, without an import cycle.

## Colony-fact and operation-readback graph

- FoodSupplyFacts.cs: per-consumer fed nutrition demand; each FoodStock preserves
  exact item, holder, count, eligible eaters, minimum-eater nutrition, rot deadline,
  temperature and roof. Held food does not become common stock. Future harvest,
  grazing, thaw, hauling and continued access are not promised.
- ForecastFacts.cs: combined human/animal stock, animal IDs, per-zone raw sow and
  harvest work, mature/stalled plants and current yield, patient blood-loss/mood
  target/break thresholds. Unknown native numbers remain absent with read issues;
  this is not a predicted medical outcome or labor schedule.
- UpkeepFacts.cs / ComfortFacts.cs: exact items/rot/deterioration/storage, beds and
  owners/users/access, storage cells and item-specific unreserved covered capacity,
  structures/repair/fire/filth/home protection, people/thermal comfort, animal feed
  and pen eligibility, dining/recreation/surface access with each indoor
  facility's host `room_id` (joins to the typed room census and its native
  `Room.Role`). Helper errors identify
  missing sections rather than producing healthy empty lists.
- ConstructionLineage.cs / HaulTracking.cs / WallUpgradeTool.cs /
  HomeCoverageTool.cs: explicit construction/haul/wall-removal records and Home
  revision/shape/exclusions. Completed native events and surviving identities are
  required; disappearance or a building at equal coordinates does not prove work.
- DevelopmentFacts.cs: actual power/base consumption/network/occupied geometry,
  furniture indoor/bed slots, open native research prerequisites and current work.
- ColonyFactsTool.cs: scalar colonist/worker counts, open resource quantities and
  policy definition labels, environment conditions, climate/growing inputs,
  farm productivity, cooking products/nutrition/rot and bills, butchering bills,
  harvestable acquisition items, food corpses, Boolean qualifying food storage,
  forbidden supply cells, the bounded `blighted_plants` census (up to 64
  blighted plants in growing zones or the home area with position, zone and
  designation state, #245), the player faction's tech level (`player_tech_level`,
  which selects the starter shelter's shape), and planning definitions/cells
  including per-cell `doorway` (a door, door blueprint or door frame) so indoor
  furnishing keeps entrance aisles clear. Source's hardcoded starter definition
  list is replaced by explicit requested open definition names.
- ChoiceDialogTool.cs (`dialog` section, #156): the topmost open force-pausing
  `Verse.Dialog_NodeTree` (window id, type, title, text, interactive) with its
  current node's options in native order, each carrying its index, label,
  `selectable`/`disabled_reason` and whether activating it resolves (closes or
  advances) the dialog rather than opening a hyperlink. Absent when no such
  dialog is open; the initial naming dialog stays under `naming`.
- NeedReliefTool.cs consumers require current needs, queued/current job identity,
  player-forced/interruptibility/native priority, timetable, carry/fire/draft/
  mental/dead/downed and medical rest facts. PawnState/Settings/JobEvidence carries
  these; operation admission does not stand in for later recovered need levels.
- BillStack, BuildingSettings, PawnSettings, ZoneState, GearLoadout, MedicalCatalog,
  ResearchSnapshot, TradeSheet and CaravanCatalog expose SnapshotRef for exact
  compare-and-set. Read tokens cannot revive authority or prove successful writes.
- Gear loadouts carry an explicitly present native deficit and an optional blocker;
  blocked pawns can still have equipment needs. Complete candidate and replacement
  lists belong to that exact loadout token. Planning read issues distinguish an
  unavailable gear census from a complete census with no eligible replacements.
- PlanningFacts.environment is the controlled-growing census inside the 45x45
  planning region: sun lamps with the native growth cells (specialDisplayRadius,
  not glow radius), power draw, schedule-aware lit_now and network; plant growers
  with fertility, sow tag, current crop and can_sow; proper indoor rooms with
  temperature, open-roof and lit cell counts; and every power network's current
  generation split into solar and wind, consumption, stored and capacity
  watt-days. Planning definitions add sow_tags and grow_min_glow for plants and
  power_w, glow_radius, sow_tag and grower_fertility for buildings. An
  `environment` section issue withholds the census; row counts are bounded and
  a lit cell count never exceeds the room's cells.
- The current colony upkeep projection includes independent complete visible item,
  structure, fire and filth censuses, each bounded to 256 rows. A section issue
  requires no rows and prevents recovery; missing required row fields remain
  unknown. Item deterioration uses the native base rate, so covered items still
  need valid storage. Home flags limit fire, repair and cleaning needs. Native
  playing clearance is required before recreation capacity is accepted.

## External reads and deliberate projections

- rimworld/get_cell_info and get_cells_info map to GetCells with exact cell/rectangle
  selection. get_map_target_info and list_colonists map to ResolveTarget/ListPawns;
  future adapters must validate the installed producer output, not cast empty SDK
  output schemas. Target details use the corresponding entity read after resolution.
- get_camera_state, get_selection_semantics, list_selected_gizmos,
  get_screen_targets, get_ui_state, get_ui_layout, list_main_tabs,
  list_inspect_tabs,
  list_letters, list_messages, list_alerts, take_screenshot are presentation-owned.
  Its schema covers exact selected IDs, targets and capture context for readback.
- list_architect_categories and list_architect_designators map to this package's
  ListArchitectCategories/ListArchitectDesignators. bridge_game.py:137-151 consumes
  exact designator ID, category, buildable definition/label, application kind and
  cell/rectangle support. Visibility/availability are explicit optional facts.
  Empty SDK output schemas still require producer verification; no generic
  architect execution capability is introduced.
- games_status/names/detail/start/stop/connect and save/load are lifecycle/host
  ownership; no second process manager. Clock journal readback is clock-owned.
- Legacy aggregate rows, sparse-default encodings, room-grid indexes, CSV geometry,
  aliases, timing/watch objects, unknownArguments and explanatory boilerplate are
  not separate domain types. Exact typed rows permit aggregation/presentation by
  consumers. Room cell queries use GetCells plus ListRooms by native room ID.
  Semantic diagnostics (inspect text, lock/refusal reasons, hidden/read failures,
  thermal sides and ingredients) remain typed facts or bounded diagnostic strings.

## Adapter and native acceptance gates

The shared contract decisions are fixed; the following are implementation work,
not permission to substitute generic payloads:

1. Common unavailable reasons distinguish NOT_REQUESTED, NOT_APPLICABLE, HIDDEN,
   and NATIVE_COMPONENT_MISSING from failed/stale/limited observations. Every
   requested absent fact names its field and reason.
2. One exact opaque source entity ID is reused across reads and writes, with no
   fuzzy or suffix matching. An unverified producer mapping makes the capability
   unavailable until its adapter is verified. Hediff identity is scoped to a health
   snapshot plus definition and body-part index; no stable ID is invented. Duplicate
   indistinguishable conditions remain ambiguous and cannot authorize an exact write.
3. Cursors bind a frozen snapshot, context, query and collection. A collection may
   span pages of at most 256 rows; snapshots may not be mixed. Expired/capacity-limited
   collections are explicitly unavailable. No continuation reads changed live data.
   Storage/TTL tuning belongs to native implementation. Oversized single-entity
   geometry must be unavailable or fetched completely through bounded exact pages.
4. Reads must not mutate game or bookkeeping state. Move identity/progress
   initialization and hook setup to lifecycle; move haul/wall completion updates
   into native event hooks before implementing these readers.
5. Native external SDK outputs are not described by its empty output schemas.
   Producer-backed adapter tests must cover retained GetCells/ResolveTarget fields
   and selection readback; no generic fallback is authorized.
6. Remove silent source caps (40 harvest items, 80 forbidden supplies, 400 buildings,
   60 room contents and position samples). Prove paging, oversized-result refusal,
   missing/zero/false distinction, modded definitions, stale context, and consumer
   projections against actual native producers.

Validation for this slice is official protoc 30 descriptor/C# generation and
net472 generated-source compilation, not adapter or gameplay acceptance.

## Obtainable operation preconditions

Every `EntityRef.snapshot` used for a write is required, context-scoped and bound
to the exact ID. Lightweight label references may omit it, but then cannot supply
an EntityPrecondition. Missing producer support yields unavailable; tokens are
never fabricated from a label, position, tick alone, or public protobuf bytes.
Tokens cover the relevant native facts and domain-specific settings, not authority.

| Operations precondition | Read path |
|---|---|
| CancelConstruction.target | ListBuildings.building.snapshot or GetCells.thing.snapshot |
| InstallBuilding.packed_or_inner | ReadInstallStatus.packed_snapshot or inner_snapshot, matched to selected ID |
| AcquireResource.source | ListResourceSources.source.snapshot |
| ExcavateCell.expected_snapshot_token | ReadExcavationSite.cells[].snapshot (cell + rock def + hit points + designation; never a Mineable ThingID) |
| DesignateThing.target | GetCells.thing.snapshot / ListPawns.pawn.snapshot / ListBuildings.building.snapshot |
| PatchBuilding.building | ReadBuildingSettings.snapshot (same building ID) |
| PatchPawn.pawn | ReadPawnSettings.snapshot (same pawn ID) |
| AddBill.bench; Patch/Delete/MoveBill.bill | ReadBills.bench.snapshot and BillState.id; whole ordered stack token |
| SelectResearch.expected_snapshot_token | ReadResearch.snapshot |
| SetProductionPolicy.expected_snapshot_token | ReadProductionPolicy.snapshot |
| CreateZone.expected_map_snapshot_token | GetCells.map_snapshot, bound to exact inspected map/geometry query |
| DeleteZone/EditZoneCells/RepairZone/PatchStockpile/PatchGrowing.zone | ListZones.zone.snapshot |
| ExtendHome.target/shape/revision | ReadColonyFacts.upkeep.home_coverage.target.snapshot/shape_token and revision |
| AssignBed.pawn/bed/expected_previous_bed | ListPawns.pawn.snapshot; ListBuildings.building.snapshot; pawn settings/owned bed readback |
| RemoveWall.wall/expected_site_snapshot_token | ListWallUpgradeSites.target.snapshot and site.snapshot, exact geometry below |
| ReleaseWallRemovals.expected_snapshot_token | ReadColonyFacts.upkeep.wall_removal.snapshot |
| RecoveryArea.pawn/area | ListPawns.pawn.snapshot; ReadRecovery.area.snapshot |
| RecoverService.target/pawn | ReadRecovery.building.snapshot; ListPawns.pawn.snapshot |
| ManageWaste.target/pawn | ReadWaste.thing.snapshot; ListPawns.pawn.snapshot |
| RelieveNeed.pawn/job/schedule | ListPawns.pawn.snapshot, JobEvidence, PawnSettings.schedule |
| ImproveGear.pawn/target/loadout | ReadGear.pawn.snapshot, candidate.item.thing.snapshot, GearLoadout.snapshot |
| QueueSurgery.patient/health/care | ListPawns.pawn.snapshot, PawnHealth.snapshot, PawnSettings.medical_care; ReadMedicalCatalog snapshot for preparation |
| SetAnimalTraining/SlaughterAnimal.animal/census | ReadHusbandry.pawn.snapshot, animal.census_snapshot; settings snapshot separate |
| SetPrisonerInteraction.pawn | ReadPopulation.pawn.snapshot and current interaction |
| SetDrafted/MovePawn/AttackTarget/PawnTargetOrder | ListPawns.pawn.snapshot; exact target snapshot from ResolveTarget/GetCells/entity reads |
| OpenTrade.trader/negotiator | ListTraders.trader.snapshot/negotiator.snapshot |
| SetTradeLines/AcceptTrade/EndTrade.session | ReadTradeSheet.snapshot or ReadTradeStatus.snapshot; exact session ID |
| SetTradeLines.line_id | ReadTradeSheet.lines.line_id, scoped to frozen sheet; not an inferred DefName/index |
| FormCaravan.catalog/pawns/cargo | ReadCaravanCatalog.snapshot; pawn IDs; cargo_groups.group_id |
| TravelCaravan/GiftCaravanSilver/FulfillQuest.caravan | ReadWorldProgression.caravan.snapshot |
| GiftCaravanSilver.faction | ReadWorldProgression.faction.snapshot or ReadWorld.settlement.faction_snapshot |
| AcceptQuest/FulfillQuest.quest | ReadWorldProgression.quest.snapshot; exact eligible accepter/reward choice |
| ReleaseOwnedDraft | ListPawns.draft_claim.owned claim_id/Owner and pawn_snapshot; known unowned differs from unavailable |

PlaceBuilding uses its placement preview and write authority precondition; it has
no separate EntityPrecondition. Preview preparation return-storage/catalog tokens
are also available from CaravanCatalog.return_storage_snapshot/MedicalCatalog.

New producer obligations are explicit:

- ProductionPolicyState.cs stores map-scoped Floors, Commitments and Stopped;
  MiningState.cs:61-74 stores DrillingRecord definition/resource/IDs/cell/recovered/
  Target. ReadProductionPolicy reads these without invoking the replacement
  Policy command or lazily creating state. Snapshot covers all four replacement
  collections; commitments_active reflects native supervision semantics.
- ExtractionDevelopment.cs:57-60 provides site definition/cell/rotation/resource,
  power/spare power and work types. ResourceAcquisitionTool.cs:83-89 and
  MiningState.cs supply owned/pending/current drill IDs, recovered units, target,
  missing/depleted state. ExtractionDevelopment now carries these concrete facts;
  existing native eight-site/40-deposit caps need explicit paging.
- WallUpgradeTool.cs:232-262 provides wall anchor/normal/left/right supports and
  backup cells, plus allowed replacement material costs. Site snapshots must also
  expose original/support/backups/replacement BuildingState (old/new stuff and
  exact occupied cells), cell terrain/roof/enclosure and RoofSupportSnapshot.
  Some of these facts are currently private admission checks; the new read producer
  must project them, never substitute a token alone for verifiable geometry.
- Existing DraftOwnership supplies an opaque claim but not typed owner/direction.
  The new native authority-aware claim producer must record and expose exact
  claim_id, original Owner(controller session, player direction), and pawn scope.
  Boolean drafted does not prove ownership; unowned and unavailable are distinct.
  Cleanup compares the unchanged claim even after authority is revoked.
- The existing trade/catalog producers use positional rows and transferable groups.
  The new snapshot producer allocates opaque sheet-scoped line IDs and catalog-scoped
  group IDs attached to exact native rows. IDs cannot be derived from labels or
  definition names. Entity and map snapshot production is likewise new adapter
  work backed by actual native facts, not claimed existing wire behavior.

Clearance: `GetClearanceTargets` reads visible, deconstructible non-player buildings touching Home. It retains partial Home overlap, sealed ancient-danger membership, counterfactual roof blockers, faction and designation ownership. The same read lists the chunk stacks standing in Home (`chunks`: forbidden, stored, hauling destination) and, while an allowed unstored chunk has no destination, a free outdoor Home footprint for a dumping stockpile (`dump_sites`). `ClearHomeObstructions` consumes both.
