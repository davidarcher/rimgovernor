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

## Required semantic validation

- Every reply selects exactly one observed, unavailable, or request failure case.
  Observed snapshots have actual identity/tick context. Expected scope is checked
  before reading; mismatch is stale, never silently rebound to the current map.
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
  Counts are nonnegative; ratios and all measurements are finite. Units are named
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
| SpatialAccessTool.cs / spatial_access | ReadSpatialAccess; PawnAccess and AccessTarget | spatial, spatial_site, construction_preflight |
| RoofSupportTool.cs / roof_support | ReadRoofSupport; exact target, RoofSupportCell | wall/room upkeep and roof safety |
| WallUpgradeTool.cs / wall_upgrade_sites | ListWallUpgradeSites; WallUpgradeSite, material and worker evidence | wall_upgrade, colony_upkeep |
| ResourceAcquisitionTool.cs / resource_sources | ListResourceSources; ResourceSource, StorageCapacity, ExtractionDevelopment | resource_control, extraction_development, mining |
| HusbandryTool.cs / husbandry_facts | ReadHusbandry; HusbandryAnimal, TrainingEntry, HandlerState | husbandry, animal_feed |
| WasteTools.cs / waste_state | ReadWaste; WasteItem with exposed/relocated/buried state | waste, waste_outcome |
| RecoveryTools.cs / recovery_state | ReadRecovery; RecoveryArea, RecoveryRestriction, BuildingState | recovery, service_recovery |
| PopulationTool.cs / population without interaction | ReadPopulation; PopulationPerson, supported interactions | population, sleeping_upkeep |
| WorldTool.cs / world without show | ReadWorld; WorldTile, Settlement | world_progression, expedition_policy |
| WorldProgressionTool.cs / world_progression | ReadWorldProgression; WorldMap, FactionState, CaravanState, QuestState, CaravanAssembly | world_progression, expedition_policy, trade_policy |
| PlacementPreviewsTool.cs / placement_previews | placement.proto; exact ordered candidate request and evaluated/failure result | placement_previews, construction_preflight |
| PawnImageTool.cs / pawn_image | presentation.proto; scoped render capture and unavailable | player presentation |
| BillsTool.cs / bills list and recipes | ReadBills / ReadRecipes; BillStack, BillState, RecipeState, IngredientRequirement | colony_skills, production_policy, player inspections |
| BuildingConfigTool.cs / building_config read | ReadBuildingSettings; BuildingSettings, scoped token; gizmos in presentation | building_config, thermal_control, player inspections |
| PawnConfigTool.cs / pawn_config read | ReadPawnSettings; PawnSettings, scoped token | pawn_config, medical/work/settings readers |
| OrderTool.cs / order resolve | ResolveTarget; exact typed target or explicit ambiguity | hands, target resolution, player inspections |
| TradeTool.cs / list_traders, sheet, status | ListTraders / ReadTradeSheet / ReadTradeStatus; TradeLine absolute index, session snapshot token | trade_policy, trade_outcome, player inspections |
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
  and pen eligibility, dining/recreation/surface access. Helper errors identify
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
  forbidden supply cells, and planning definitions/cells. Source's hardcoded
  starter definition list is replaced by explicit requested open definition names.
- NeedReliefTool.cs consumers require current needs, queued/current job identity,
  player-forced/interruptibility/native priority, timetable, carry/fire/draft/
  mental/dead/downed and medical rest facts. PawnState/Settings/JobEvidence carries
  these; operation admission does not stand in for later recovered need levels.
- BillStack, BuildingSettings, PawnSettings, ZoneState, GearLoadout, MedicalCatalog,
  ResearchSnapshot, TradeSheet and CaravanCatalog expose SnapshotRef for exact
  compare-and-set. Read tokens cannot revive authority or prove successful writes.

## External reads and deliberate projections

- rimworld/get_cell_info and get_cells_info map to GetCells with exact cell/rectangle
  selection. get_map_target_info and list_colonists map to ResolveTarget/ListPawns;
  future adapters must validate the installed producer output, not cast empty SDK
  output schemas. Target details use the corresponding entity read after resolution.
- get_camera_state, get_selection_semantics, list_selected_gizmos,
  get_screen_targets, get_ui_state, get_ui_layout, list_main_tabs,
  list_inspect_tabs, list_architect_categories, list_architect_designators,
  list_letters, list_messages, list_alerts, take_screenshot are presentation-owned.
  Its schema covers exact selected IDs, targets and capture context for readback.
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
