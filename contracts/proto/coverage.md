# Shared native contract coverage

All 97 porting-baseline native-tool inventory rows: 54 owned home calls, 33 upstream SDK calls, eight controller host boundaries and two dispatch/discovery mechanisms. The baseline 55-export production census additionally contains home/play_until_event, mapped below. See [current native capabilities](../native-protobuf-cutover.md) for implemented exports. Family coverage does not prove adapter behavior or field completeness.

`controller/rimgovernor/*.py` citations below are frozen historical provenance from
the Python baseline this mapping was captured against; that source tree was removed
in [G01.13](https://github.com/davidarcher/rimgovernor/issues/33) and the paths no
longer resolve. They are retained to show where each capability was originally
discovered/mapped, not as live source links.

| Baseline boundary | Canonical destination | Disposition | Inventory source |
|---|---|---|---|
| `games_start` | Go controller gamehost process adapter | External host | `controller/rimgovernor/bridge.py:106` |
| `games_connect` | Go controller gamehost process adapter | External host | `controller/rimgovernor/bridge.py:147` |
| `games_tool_names` | Go bridge game backend discovery + canonical RPC descriptors | External discovery | `controller/rimgovernor/bridge.py:161` |
| `games_tool_detail` | Go bridge game backend discovery + canonical RPC descriptors | External discovery | `controller/rimgovernor/bridge.py:164` |
| `games_call_tool` | Go bridge GABP canonical RPC transport; arbitrary dispatch excluded | Transport | `controller/rimgovernor/bridge.py:167` |
| `home/observation_batch` | Observations.ReadObservationBatch | Typed | `controller/rimgovernor/bridge.py:102` |
| `home/placement_previews` | Placement.Preview | Typed | `controller/rimgovernor/bridge.py:102` |
| `home/colony_identity` | Lifecycle.ReadIdentity | Typed | `controller/rimgovernor/bridge.py:108` |
| `games_status` | Go controller gamehost process adapter | External host | `controller/rimgovernor/bridge.py:54` |
| `rimworld/load_game_ready` | Lifecycle.Load / ReadLoad | Typed | `controller/rimgovernor/bridge.py:153` |
| `home/upkeep_home` | Actions.Apply HomeIntent | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/upkeep_wall` | Actions.Apply: RemoveWallIntent | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/upkeep_bed` | Actions.Apply BedAssignIntent | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/recover_service` | Actions.Apply: RecoverIntent | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/husbandry_config` | Actions.Apply HusbandryIntent | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/relieve_need` | Actions.Apply: NeedReliefIntent (#939) | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/medical_operations` | Observations.ReadMedicalCatalog | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/caravan` | Actions.Apply: FormCaravanIntent | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/manage_waste` | Actions.Apply: WasteIntent | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/gear_upkeep` | Actions.Apply: PawnOrderIntent WEAR (#939); Observations.ReadGear | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/population` | Actions.Apply PrisonerInteractionIntent; Observations.ReadPopulation | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/acquire_resource` | Operations.Preview / Execute: AcquireResource | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/cancel_construction` | Operations.Preview / Execute: CancelConstruction | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/confirm_colony_names` | Actions.Apply NamingIntent (autopilot); PresentationReads.PreviewNaming/PlayerPresentation.Apply naming (future player affordance, unregistered) | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/zone_cells` | Actions.Apply CreateZone / DeleteZoneIntent / ZoneCellsIntent / StockpileIntent; Operations.Preview CreateZone (planner siting); Operations RepairZone / PatchGrowing; Observations.ListZones | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/place_building` | Operations.Preview / Execute: PlaceBuilding; Placement.Preview | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/pawn_config` | Actions.Apply WorkSettingsIntent; Observations.ReadPawnSettings | Typed | `controller/rimgovernor/bridge_game.py:15` |
| `home/building_config` | Actions.Apply: BuildingPatchIntent; Observations.ReadBuildingSettings / PresentationReads.Gizmos | Typed | `controller/rimgovernor/bridge_game.py:16` |
| `home/bills` | Actions.Apply ProductionBillIntent; Observations.ReadBills / ReadRecipes | Typed | `controller/rimgovernor/bridge_game.py:16` |
| `home/order` | Operations.Preview / Execute: SetDrafted / AttackTarget; Actions.Apply MoveIntent / PawnOrderIntent; Observations.ResolveTarget; Operations.ReleaseOwnedDraft | Typed | `controller/rimgovernor/bridge_game.py:16` |
| `home/trade` | Actions.Apply: TradeIntent (open / set_lines / accept / end); Observations.ListTraders / ReadTradeSheet / ReadTradeSession | Typed | `controller/rimgovernor/bridge_game.py:16` |
| `home/research` | Actions.Apply ResearchIntent; Observations.ReadResearch | Typed | `controller/rimgovernor/bridge_game.py:16` |
| `home/dialog_text` | PresentationReads.DialogFields / PreviewDialogText; PlayerPresentation.Apply text | Typed | `controller/rimgovernor/bridge_game.py:16` |
| (none: game-opened `Verse.Dialog_NodeTree`) | Observations.ReadColonyFacts `dialog` (ChoiceDialog, indexed options); Actions.Apply DialogIntent; Clock `STOP_REASON_DIALOG_PAUSE` | Typed (#156) | none: the legacy controller never read or answered force-pausing choice dialogs |
| `rimworld/set_time_speed` | Clock.Start / Pause / ChangeSpeed; unsupervised autonomous bypass removed | Typed replacement | `controller/rimgovernor/bridge_game.py:17` |
| `rimworld/apply_architect_designator` | Actions.Apply DesignateIntent (Allow/Forbid/CutPlant) | Closed replacement | `controller/rimgovernor/bridge_game.py:17` |
| `rimworld/open_letter` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/bridge_game.py:18` |
| `rimworld/dismiss_letter` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/bridge_game.py:18` |
| `rimworld/click_screen_target` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/bridge_game.py:18` |
| `rimworld/click_ui_target` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/bridge_game.py:19` |
| `rimworld/scroll_ui_target` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/bridge_game.py:19` |
| `rimworld/open_main_tab` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/bridge_game.py:20` |
| `rimworld/close_main_tab` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/bridge_game.py:20` |
| `home/wall_upgrade_sites` | Observations.ListWallUpgradeSites | Typed | `controller/rimgovernor/bridge_game.py:7` |
| `home/roof_support` | Observations.ReadRoofSupport | Typed | `controller/rimgovernor/bridge_game.py:7` |
| (new, Go-era) excavation site | Observations.ReadExcavationSite; Actions.Apply: ExcavateIntent | Typed | [#8](https://github.com/davidarcher/rimgovernor/issues/8) |
| `home/recovery_state` | Observations.ReadRecovery | Typed | `controller/rimgovernor/bridge_game.py:7` |
| `home/husbandry_facts` | Observations.ReadHusbandry | Typed | `controller/rimgovernor/bridge_game.py:7` |
| `home/waste_state` | Observations.ReadWaste | Typed | `controller/rimgovernor/bridge_game.py:7` |
| `home/resource_sources` | Observations.ListResourceSources | Typed | `controller/rimgovernor/bridge_game.py:7` |
| `rimworld/get_cells_info` | Observations.GetCells | Typed | `controller/rimgovernor/bridge_game.py:8` |
| `rimworld/get_cell_info` | Observations.GetCells | Typed | `controller/rimgovernor/bridge_game.py:8` |
| `rimworld/list_architect_categories` | Observations.ListArchitectCategories / ListArchitectDesignators | Typed | `controller/rimgovernor/bridge_game.py:9` |
| `rimworld/list_architect_designators` | Observations.ListArchitectCategories / ListArchitectDesignators | Typed | `controller/rimgovernor/bridge_game.py:9` |
| `rimworld/list_selected_gizmos` | PresentationReads.Gizmos | Typed | `controller/rimgovernor/bridge_game.py:10` |
| `rimworld/get_selection_semantics` | PresentationReads.Selection | Typed | `controller/rimgovernor/bridge_game.py:10` |
| `rimworld/list_letters` | PresentationReads.Notifications | Typed | `controller/rimgovernor/bridge_game.py:11` |
| `rimworld/get_ui_state` | PresentationReads.CaptureUi | Typed | `controller/rimgovernor/bridge_game.py:11` |
| `rimworld/get_screen_targets` | PresentationReads.ScreenTargetsRead | Typed | `controller/rimgovernor/bridge_game.py:11` |
| `rimworld/get_ui_layout` | PresentationReads.CaptureUi | Typed | `controller/rimgovernor/bridge_game.py:12` |
| `rimworld/list_main_tabs` | PresentationReads.MainTabs | Typed | `controller/rimgovernor/bridge_game.py:12` |
| `rimworld/list_inspect_tabs` | PresentationReads.InspectTabs | Typed | `controller/rimgovernor/bridge_game.py:12` |
| `rimworld/list_messages` | PresentationReads.Notifications | Typed | `controller/rimgovernor/bridge_game.py:13` |
| `rimworld/list_alerts` | PresentationReads.Notifications | Typed | `controller/rimgovernor/bridge_game.py:13` |
| `rimworld/get_map_target_info` | Observations.ResolveTarget + entity/cell read | Typed | `controller/rimgovernor/bridge_game.py:13` |
| `home/get_cells_plus` | Observations.GetCells | Typed | `controller/rimgovernor/bridge_game.py:122` |
| `home/world` | Observations.ReadWorld; PlayerPresentation.Apply world view | Typed | `controller/rimgovernor/bridge_game.py:53` |
| `home/colony_facts` | Observations.ReadColonyFacts | Typed | `controller/rimgovernor/bridge_observation.py:22` |
| `home/status` | Observations.ReadStatus + Clock.ReadStatus + PresentationReads.Notifications | Typed | `controller/rimgovernor/bridge_observation.py:23` |
| `home/list_pawns` | Observations.ListPawns | Typed | `controller/rimgovernor/bridge_observation.py:23` |
| `home/list_things` | Observations.ListSupplies | Typed | `controller/rimgovernor/bridge_observation.py:23` |
| `home/list_buildings` | Observations.ListBuildings | Typed | `controller/rimgovernor/bridge_observation.py:24` |
| `home/list_rooms` | Observations.ListRooms | Typed | `controller/rimgovernor/bridge_observation.py:24` |
| `home/list_zones` | Observations.ListZones | Typed | `controller/rimgovernor/bridge_observation.py:24` |
| `home/world_progression` | Observations.ReadWorldProgression | Typed | `controller/rimgovernor/bridge_observation.py:25` |
| `home/spatial_access` | Observations.ReadSpatialAccess | Typed | `controller/rimgovernor/bridge_observation.py:27` |
| `home/render_demand` | PresentationMedia.DemandRendering; PresentationReads.RenderState | Typed | `controller/rimgovernor/bridge_runtime.py:250` |
| `rimworld/take_screenshot` | PresentationMedia.CaptureScreenshot | Typed | `controller/rimgovernor/bridge_runtime.py:256` |
| `rimworld/get_camera_state` | PresentationReads.Camera | Typed | `controller/rimgovernor/bridge_runtime.py:252` |
| `rimworld/list_colonists` | PresentationReads.Colonists; Observations.ListPawns | Typed | `controller/rimgovernor/dashboard_controls.py:184` |
| `rimworld/select_pawn` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/dashboard_controls.py:194` |
| `rimworld/clear_selection` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/dashboard_controls.py:194` |
| `rimworld/set_camera_zoom` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/dashboard_controls.py:271` |
| `rimworld/move_camera` | PlayerPresentation.Apply exact closed captured command | Typed | `controller/rimgovernor/dashboard_controls.py:275` |
| `home/player_input` | PlayerPresentation.LeaseInput / SendInput; PresentationReads.InputStateRead | Typed | `controller/rimgovernor/dashboard_controls.py:73` |
| `games_kill` | Go controller gamehost process adapter | External host | `controller/rimgovernor/native_trials.py:112` |
| `rimworld/save_game` | Lifecycle.Save / ReadSave | Typed | `controller/rimgovernor/session_checkpoint.py:48` |
| `games_stop` | Go controller gamehost process adapter | External host | `controller/rimgovernor/session_checkpoint.py:92` |
| `dynamic-discovery` | Generated capability descriptors and closed read/preview/write branches | Inventory mechanism | `controller/rimgovernor/bridge.py` |
| `argument-sensitive-dispatch` | Generated capability descriptors and closed read/preview/write branches | Inventory mechanism | `controller/rimgovernor/bridge_game.py` |

Additional production export: `home/play_until_event` -> Clock.Start/ReadStatus/ReadEvents/Pause with native bounded ticks; no second autonomous runner.

All inventoried boundaries have a typed destination or an explicit external/retired responsibility. Native snapshot/CAS producers, typed UI semantic details and adapter/gameplay acceptance remain separate work in N01. Family coverage does not claim those facts already exist in current native replies. Fixture/editor/debug operations and arbitrary execution are excluded. Pawn nickname/direct gear drop are not autonomous operations.

## Protocol ownership

The nine canonical schemas divide ownership as follows:

| Schema | Responsibility |
|---|---|
| [common.proto](common.proto) | Identity, observation context, correlation, failures, availability and page metadata. |
| [authority.proto](authority.proto) | Explicit acquisition/renewal/revocation and native single-writer authority status. |
| [clock.proto](clock.proto) | Bounded supervised ticks, epoch ownership, safety stops, journal and admitted clock receipt lookup. |
| [lifecycle.proto](lifecycle.proto) | Native identity, save/load and correlated completion reads; the Go controller gamehost owns process lifetime. |
| [placement.proto](placement.proto) | Ordinary native placement candidate evaluation and complete bounded site/cost facts. |
| [operations.proto](operations.proto) | One concrete ordinary mutation per guarded attempt, previews and owned draft cleanup. |
| [receipts.proto](receipts.proto) | Attributed admitted effects, attempt lookup and subsequent correlated progress. |
| [observations.proto](observations.proto) | Simulation reads, definition catalogs, complete/paged observations and scoped mutation preconditions. |
| [presentation.proto](presentation.proto) | Explicit player camera/selection/UI/input and separately bounded media capture. |

Authority, guarded attempt lookup and progress are new shared capabilities rather than additional legacy SDK calls. Their typed schemas do not create a second goal system, process manager or native simulator. Go bridge discovery and games_call_tool carry only reviewed canonical RPC payloads; arbitrary names/argument dictionaries are not an execution fallback. Generated method descriptors are capability metadata, not permission.

For branch-level semantics and remaining implementation requirements, see [operation coverage](operation-coverage.md), [observation coverage](observation-coverage.md), [clock/lifecycle coverage](clock-lifecycle-coverage.md), [placement coverage](placement-coverage.md), [presentation coverage](presentation-coverage.md) and the [shared validation rules](README.md). Preview/write separation, unknown versus empty facts, native rule checks, explicit player authority and observation before uncertain-write retry remain required regardless of which historical call supplied a capability.
