# MCP capability mapping

Each canonical Protobuf RPC has one fixed MCP tool name: `rimgovernor/<family>_<snake_case_rpc>`. The family is the middle component of `rimgovernor.<family>.v1`; RPC names are converted from PascalCase to lowercase words separated by underscores. Service names remain part of the descriptor identity but are not repeated in tool names. Consequently, RPC names must remain unique across services within one family, including the three presentation services.

This table comes from the nine official compiled C# `FileDescriptor` objects, not a parallel schema parser or source generator. All 81 methods have unique MCP names, and the longest name is 51 characters, below the 64-character ceiling. `common.proto` contributes shared messages but no RPCs. The table fixes capability identity; it does not claim an adapter is installed or native acceptance has passed.

## Wrapper and dispatch

The SDK-facing request declares one argument, `request`, whose value is a string containing official ProtoJSON for the exact request type below. An empty request message still uses `request: "{}"`. Reject unknown outer argument keys, absent request and non-string request before invoking the method or causing any effects. The official parser handles the inner ProtoJSON; common and family validators enforce presence, bounds, unknown-field policy and domain constraints before admission.

The response contains a `payload` string holding official ProtoJSON for the exact reply type, alongside ordinary SDK metadata. SDK envelopes, text summaries and discovery descriptors are not domain effects or receipt evidence. Consumers parse `payload` with the registered generated reply parser and retain its typed outcome/identity/correlation. No `Struct`, `Any`, raw argument bag, arbitrary method/tool selector or string-based native dispatch is permitted inside a request.

Control request/reply size limits count the UTF-8 bytes of the ProtoJSON document
inside `request` or `payload`. SDK metadata and the enclosing transport's JSON
string escaping are outside that count; transport framing has its own limits.

The installed SDK's string binder can throw before the adapter inspects malformed
outer values. Native adapters therefore receive `request` as CLR `object`, inspect
the original outer arguments, require its actual value to be a string, and then
invoke the official parser. This SDK parameter may advertise `type: object` in
discovery. Go permits that single documented descriptor exception only for the
fixed owned methods below and the sole `request` property; it still sends and
requires an actual string. A canonical string descriptor is also valid when the
SDK can supply one. No object-valued request, broader unknown schema or generic
raw-object fallback is accepted. This is an SDK boundary constraint, not a second
wire format or an alias for historical tools.

Each adapter advertises support for a fixed descriptor method identity and its request/reply types. Unsupported methods report unavailable support; they never fall back to a similarly named historical tool. Original `home/*`, `rimworld/*` and `games_*` names belong to the source capability inventory. This active-development protocol requires no compatibility aliases. GABS remains the existing external discovery/transport/process owner; these descriptors do not create another server or process manager.

## Authorization boundaries

Tool discovery is not authorization. Apply these restrictions before evaluating the concrete request, and enforce the stricter per-operation native checks at admission:

| Service or methods | Allowed capability |
|---|---|
| Observations, Placement.Preview, Operations.Preview; Authority.ReadStatus; Clock.ReadStatus/ReadEvents/ReadAttempt; Attempts.Lookup/ObserveProgress; Lifecycle.ReadIdentity | Scoped reads/inspection. Read-only methods grant no execution authority and cannot turn unknown facts into absence. |
| Operations.Execute; Clock.Start/Renew/ChangeSpeed | Deterministic guarded execution under current authenticated player direction, native authority, exact identity and attempt correlation. Advisers cannot invoke them. Special policies such as surgery, pawn trading and persistent draft additionally require the approved current action and targets. |
| Operations.ReleaseOwnedDraft; Clock.Pause | Safe owned cleanup may remain available after automation revocation, but only for the exact original current-load claim/epoch. Never release a replacement/player draft or pause another owner. |
| Authority.Control | Acquire only from the trusted explicit player-direction path; renew only the active owner/lease; revoke through authorized Manual/direction/disconnect/cleanup policy. Caller-supplied direction numbers cannot authenticate themselves, and renewal cannot reacquire revoked authority. |
| Lifecycle.Save/Load/ReadSave/ReadLoad | Explicit session lifecycle with exact instance/request ownership and current player direction. Follow lifecycle preconditions; no automatic/model load or process management escape hatch. Completion reads grant no permission to retry uncertain writes. |
| PresentationReads | Scoped player-facing inspection with appropriate current game/capture identity. Captured targets do not grant input permission. |
| PlayerPresentation | Authenticated explicit player control only. Input ownership, verified pause/owned-draft cleanup, exact capture and direction checks apply. No adviser access or autonomous fallback to clicks. |
| PresentationMedia | Authorized viewer/media capability with independent rendering/video/frame leases, exact acknowledgements and byte limits. Media access grants no simulation-write or input authority. |

Use [shared rules](README.md) and the family coverage documents for exact validation and uncertainty behavior. A guarded receipt acknowledges only its observed effect; later pawn work requires correlated observation. Missing attempt records do not prove no effect. Returning a successful SDK envelope never overrides a typed refusal, unavailable result or admitted uncertainty.

## Fixed tool descriptors

| MCP tool name | Full service/RPC | Request message | Reply message |
|---|---|---|---|
| `rimgovernor/authority_control` | `rimgovernor.authority.v1.Authority/Control` | `rimgovernor.authority.v1.ControlRequest` | `rimgovernor.authority.v1.ControlReply` |
| `rimgovernor/authority_read_status` | `rimgovernor.authority.v1.Authority/ReadStatus` | `rimgovernor.authority.v1.StatusRequest` | `rimgovernor.authority.v1.StatusReply` |
| `rimgovernor/clock_change_speed` | `rimgovernor.clock.v1.Clock/ChangeSpeed` | `rimgovernor.clock.v1.SpeedRequest` | `rimgovernor.clock.v1.ControlReply` |
| `rimgovernor/clock_pause` | `rimgovernor.clock.v1.Clock/Pause` | `rimgovernor.clock.v1.OwnedRequest` | `rimgovernor.clock.v1.StatusReply` |
| `rimgovernor/clock_read_attempt` | `rimgovernor.clock.v1.Clock/ReadAttempt` | `rimgovernor.clock.v1.AttemptRequest` | `rimgovernor.clock.v1.AttemptReply` |
| `rimgovernor/clock_read_events` | `rimgovernor.clock.v1.Clock/ReadEvents` | `rimgovernor.clock.v1.EventsRequest` | `rimgovernor.clock.v1.EventsReply` |
| `rimgovernor/clock_read_status` | `rimgovernor.clock.v1.Clock/ReadStatus` | `rimgovernor.clock.v1.StatusRequest` | `rimgovernor.clock.v1.StatusReply` |
| `rimgovernor/clock_renew` | `rimgovernor.clock.v1.Clock/Renew` | `rimgovernor.clock.v1.RenewRequest` | `rimgovernor.clock.v1.ControlReply` |
| `rimgovernor/clock_start` | `rimgovernor.clock.v1.Clock/Start` | `rimgovernor.clock.v1.StartRequest` | `rimgovernor.clock.v1.ControlReply` |
| `rimgovernor/lifecycle_load` | `rimgovernor.lifecycle.v1.Lifecycle/Load` | `rimgovernor.lifecycle.v1.LoadRequest` | `rimgovernor.lifecycle.v1.LoadReply` |
| `rimgovernor/lifecycle_read_identity` | `rimgovernor.lifecycle.v1.Lifecycle/ReadIdentity` | `rimgovernor.lifecycle.v1.IdentityRequest` | `rimgovernor.lifecycle.v1.IdentityReply` |
| `rimgovernor/lifecycle_read_tick` | `rimgovernor.lifecycle.v1.Lifecycle/ReadTick` | `rimgovernor.lifecycle.v1.TickRequest` | `rimgovernor.lifecycle.v1.TickReply` |
| `rimgovernor/lifecycle_read_load` | `rimgovernor.lifecycle.v1.Lifecycle/ReadLoad` | `rimgovernor.lifecycle.v1.RequestStatus` | `rimgovernor.lifecycle.v1.LoadReply` |
| `rimgovernor/lifecycle_read_save` | `rimgovernor.lifecycle.v1.Lifecycle/ReadSave` | `rimgovernor.lifecycle.v1.RequestStatus` | `rimgovernor.lifecycle.v1.SaveReply` |
| `rimgovernor/lifecycle_save` | `rimgovernor.lifecycle.v1.Lifecycle/Save` | `rimgovernor.lifecycle.v1.SaveRequest` | `rimgovernor.lifecycle.v1.SaveReply` |
| `rimgovernor/observations_get_cells` | `rimgovernor.observations.v1.Observations/GetCells` | `rimgovernor.observations.v1.GetCellsRequest` | `rimgovernor.observations.v1.GetCellsReply` |
| `rimgovernor/observations_list_architect_categories` | `rimgovernor.observations.v1.Observations/ListArchitectCategories` | `rimgovernor.observations.v1.ArchitectCategoriesRequest` | `rimgovernor.observations.v1.ArchitectCategoriesReply` |
| `rimgovernor/observations_list_architect_designators` | `rimgovernor.observations.v1.Observations/ListArchitectDesignators` | `rimgovernor.observations.v1.ArchitectDesignatorsRequest` | `rimgovernor.observations.v1.ArchitectDesignatorsReply` |
| `rimgovernor/observations_list_buildings` | `rimgovernor.observations.v1.Observations/ListBuildings` | `rimgovernor.observations.v1.ListBuildingsRequest` | `rimgovernor.observations.v1.ListBuildingsReply` |
| `rimgovernor/observations_list_pawns` | `rimgovernor.observations.v1.Observations/ListPawns` | `rimgovernor.observations.v1.ListPawnsRequest` | `rimgovernor.observations.v1.ListPawnsReply` |
| `rimgovernor/observations_list_resource_sources` | `rimgovernor.observations.v1.Observations/ListResourceSources` | `rimgovernor.observations.v1.ResourceSourcesRequest` | `rimgovernor.observations.v1.ResourceSourcesReply` |
| `rimgovernor/observations_list_rooms` | `rimgovernor.observations.v1.Observations/ListRooms` | `rimgovernor.observations.v1.ListRoomsRequest` | `rimgovernor.observations.v1.ListRoomsReply` |
| `rimgovernor/observations_list_supplies` | `rimgovernor.observations.v1.Observations/ListSupplies` | `rimgovernor.observations.v1.ListSuppliesRequest` | `rimgovernor.observations.v1.ListSuppliesReply` |
| `rimgovernor/observations_list_traders` | `rimgovernor.observations.v1.Observations/ListTraders` | `rimgovernor.observations.v1.TradersRequest` | `rimgovernor.observations.v1.TradersReply` |
| `rimgovernor/observations_list_wall_upgrade_sites` | `rimgovernor.observations.v1.Observations/ListWallUpgradeSites` | `rimgovernor.observations.v1.WallUpgradeSitesRequest` | `rimgovernor.observations.v1.WallUpgradeSitesReply` |
| `rimgovernor/observations_list_zones` | `rimgovernor.observations.v1.Observations/ListZones` | `rimgovernor.observations.v1.ListZonesRequest` | `rimgovernor.observations.v1.ListZonesReply` |
| `rimgovernor/observations_read_excavation_site` | `rimgovernor.observations.v1.Observations/ReadExcavationSite` | `rimgovernor.observations.v1.ExcavationSiteRequest` | `rimgovernor.observations.v1.ExcavationSiteReply` |
| `rimgovernor/observations_read_bundle` | `rimgovernor.observations.v1.Observations/ReadBundle` | `rimgovernor.observations.v1.BundleRequest` | `rimgovernor.observations.v1.BundleReply` |
| `rimgovernor/observations_read_bills` | `rimgovernor.observations.v1.Observations/ReadBills` | `rimgovernor.observations.v1.BillsRequest` | `rimgovernor.observations.v1.BillsReply` |
| `rimgovernor/observations_read_building_settings` | `rimgovernor.observations.v1.Observations/ReadBuildingSettings` | `rimgovernor.observations.v1.BuildingSettingsRequest` | `rimgovernor.observations.v1.BuildingSettingsReply` |
| `rimgovernor/observations_read_caravan_catalog` | `rimgovernor.observations.v1.Observations/ReadCaravanCatalog` | `rimgovernor.observations.v1.CaravanCatalogRequest` | `rimgovernor.observations.v1.CaravanCatalogReply` |
| `rimgovernor/observations_read_colony_facts` | `rimgovernor.observations.v1.Observations/ReadColonyFacts` | `rimgovernor.observations.v1.ColonyFactsRequest` | `rimgovernor.observations.v1.ColonyFactsReply` |
| `rimgovernor/observations_read_defense_site` | `rimgovernor.observations.v1.Observations/ReadDefenseSite` | `rimgovernor.observations.v1.DefenseSiteRequest` | `rimgovernor.observations.v1.DefenseSiteReply` |
| `rimgovernor/observations_read_gear` | `rimgovernor.observations.v1.Observations/ReadGear` | `rimgovernor.observations.v1.GearRequest` | `rimgovernor.observations.v1.GearReply` |
| `rimgovernor/observations_read_husbandry` | `rimgovernor.observations.v1.Observations/ReadHusbandry` | `rimgovernor.observations.v1.HusbandryRequest` | `rimgovernor.observations.v1.HusbandryReply` |
| `rimgovernor/observations_read_install_status` | `rimgovernor.observations.v1.Observations/ReadInstallStatus` | `rimgovernor.observations.v1.InstallStatusRequest` | `rimgovernor.observations.v1.InstallStatusReply` |
| `rimgovernor/observations_read_lines_of_fire` | `rimgovernor.observations.v1.Observations/ReadLinesOfFire` | `rimgovernor.observations.v1.LinesOfFireRequest` | `rimgovernor.observations.v1.LinesOfFireReply` |
| `rimgovernor/observations_read_medical_catalog` | `rimgovernor.observations.v1.Observations/ReadMedicalCatalog` | `rimgovernor.observations.v1.MedicalCatalogRequest` | `rimgovernor.observations.v1.MedicalCatalogReply` |
| `rimgovernor/observations_read_observation_batch` | `rimgovernor.observations.v1.Observations/ReadObservationBatch` | `rimgovernor.observations.v1.ObservationBatchRequest` | `rimgovernor.observations.v1.ObservationBatchReply` |
| `rimgovernor/observations_read_pawn_settings` | `rimgovernor.observations.v1.Observations/ReadPawnSettings` | `rimgovernor.observations.v1.PawnSettingsRequest` | `rimgovernor.observations.v1.PawnSettingsReply` |
| `rimgovernor/observations_read_population` | `rimgovernor.observations.v1.Observations/ReadPopulation` | `rimgovernor.observations.v1.PopulationRequest` | `rimgovernor.observations.v1.PopulationReply` |
| `rimgovernor/observations_read_production_policy` | `rimgovernor.observations.v1.Observations/ReadProductionPolicy` | `rimgovernor.observations.v1.ProductionPolicyRequest` | `rimgovernor.observations.v1.ProductionPolicyReply` |
| `rimgovernor/observations_read_recipes` | `rimgovernor.observations.v1.Observations/ReadRecipes` | `rimgovernor.observations.v1.RecipesRequest` | `rimgovernor.observations.v1.RecipesReply` |
| `rimgovernor/observations_read_recovery` | `rimgovernor.observations.v1.Observations/ReadRecovery` | `rimgovernor.observations.v1.RecoveryRequest` | `rimgovernor.observations.v1.RecoveryReply` |
| `rimgovernor/observations_read_research` | `rimgovernor.observations.v1.Observations/ReadResearch` | `rimgovernor.observations.v1.ResearchRequest` | `rimgovernor.observations.v1.ResearchReply` |
| `rimgovernor/observations_read_roof_support` | `rimgovernor.observations.v1.Observations/ReadRoofSupport` | `rimgovernor.observations.v1.RoofSupportRequest` | `rimgovernor.observations.v1.RoofSupportReply` |
| `rimgovernor/observations_read_spatial_access` | `rimgovernor.observations.v1.Observations/ReadSpatialAccess` | `rimgovernor.observations.v1.SpatialAccessRequest` | `rimgovernor.observations.v1.SpatialAccessReply` |
| `rimgovernor/observations_read_status` | `rimgovernor.observations.v1.Observations/ReadStatus` | `rimgovernor.observations.v1.StatusRequest` | `rimgovernor.observations.v1.StatusReply` |
| `rimgovernor/observations_read_trade_sheet` | `rimgovernor.observations.v1.Observations/ReadTradeSheet` | `rimgovernor.observations.v1.TradeSheetRequest` | `rimgovernor.observations.v1.TradeSheetReply` |
| `rimgovernor/observations_read_trade_status` | `rimgovernor.observations.v1.Observations/ReadTradeStatus` | `rimgovernor.observations.v1.TradeStatusRequest` | `rimgovernor.observations.v1.TradeStatusReply` |
| `rimgovernor/observations_read_waste` | `rimgovernor.observations.v1.Observations/ReadWaste` | `rimgovernor.observations.v1.WasteRequest` | `rimgovernor.observations.v1.WasteReply` |
| `rimgovernor/observations_read_world` | `rimgovernor.observations.v1.Observations/ReadWorld` | `rimgovernor.observations.v1.WorldRequest` | `rimgovernor.observations.v1.WorldReply` |
| `rimgovernor/observations_read_world_progression` | `rimgovernor.observations.v1.Observations/ReadWorldProgression` | `rimgovernor.observations.v1.WorldProgressionRequest` | `rimgovernor.observations.v1.WorldProgressionReply` |
| `rimgovernor/observations_resolve_target` | `rimgovernor.observations.v1.Observations/ResolveTarget` | `rimgovernor.observations.v1.ResolveTargetRequest` | `rimgovernor.observations.v1.ResolveTargetReply` |
| `rimgovernor/operations_execute` | `rimgovernor.operations.v1.Operations/Execute` | `rimgovernor.operations.v1.ExecuteRequest` | `rimgovernor.operations.v1.ExecuteReply` |
| `rimgovernor/operations_preview` | `rimgovernor.operations.v1.Operations/Preview` | `rimgovernor.operations.v1.PreviewRequest` | `rimgovernor.operations.v1.PreviewReply` |
| `rimgovernor/operations_release_owned_draft` | `rimgovernor.operations.v1.Operations/ReleaseOwnedDraft` | `rimgovernor.operations.v1.ReleaseOwnedDraftRequest` | `rimgovernor.operations.v1.ReleaseOwnedDraftReply` |
| `rimgovernor/placement_preview` | `rimgovernor.placement.v1.Placement/Preview` | `rimgovernor.placement.v1.PlacementRequest` | `rimgovernor.placement.v1.PlacementReply` |
| `rimgovernor/presentation_acknowledge_frame` | `rimgovernor.presentation.v1.PresentationMedia/AcknowledgeFrame` | `rimgovernor.presentation.v1.FrameAcknowledgement` | `rimgovernor.presentation.v1.FrameAcknowledgementReply` |
| `rimgovernor/presentation_apply` | `rimgovernor.presentation.v1.PlayerPresentation/Apply` | `rimgovernor.presentation.v1.PlayerCommand` | `rimgovernor.presentation.v1.PlayerCommandReply` |
| `rimgovernor/presentation_camera` | `rimgovernor.presentation.v1.PresentationReads/Camera` | `rimgovernor.presentation.v1.ReadRequest` | `rimgovernor.presentation.v1.CameraReply` |
| `rimgovernor/presentation_capture_pawn` | `rimgovernor.presentation.v1.PresentationMedia/CapturePawn` | `rimgovernor.presentation.v1.PawnImageRequest` | `rimgovernor.presentation.v1.PawnImageReply` |
| `rimgovernor/presentation_capture_screenshot` | `rimgovernor.presentation.v1.PresentationMedia/CaptureScreenshot` | `rimgovernor.presentation.v1.ScreenshotRequest` | `rimgovernor.presentation.v1.ScreenshotReply` |
| `rimgovernor/presentation_capture_ui` | `rimgovernor.presentation.v1.PresentationReads/CaptureUi` | `rimgovernor.presentation.v1.UiReadRequest` | `rimgovernor.presentation.v1.UiReply` |
| `rimgovernor/presentation_colonists` | `rimgovernor.presentation.v1.PresentationReads/Colonists` | `rimgovernor.presentation.v1.ColonistRosterRequest` | `rimgovernor.presentation.v1.ColonistRosterReply` |
| `rimgovernor/presentation_demand_rendering` | `rimgovernor.presentation.v1.PresentationMedia/DemandRendering` | `rimgovernor.presentation.v1.RenderDemand` | `rimgovernor.presentation.v1.RenderReply` |
| `rimgovernor/presentation_dialog_fields` | `rimgovernor.presentation.v1.PresentationReads/DialogFields` | `rimgovernor.presentation.v1.ReadRequest` | `rimgovernor.presentation.v1.DialogReply` |
| `rimgovernor/presentation_gizmos` | `rimgovernor.presentation.v1.PresentationReads/Gizmos` | `rimgovernor.presentation.v1.ReadRequest` | `rimgovernor.presentation.v1.GizmosReply` |
| `rimgovernor/presentation_input_state_read` | `rimgovernor.presentation.v1.PresentationReads/InputStateRead` | `rimgovernor.presentation.v1.ReadRequest` | `rimgovernor.presentation.v1.InputStateReply` |
| `rimgovernor/presentation_inspect_tabs` | `rimgovernor.presentation.v1.PresentationReads/InspectTabs` | `rimgovernor.presentation.v1.TabsRequest` | `rimgovernor.presentation.v1.TabsReply` |
| `rimgovernor/presentation_lease_input` | `rimgovernor.presentation.v1.PlayerPresentation/LeaseInput` | `rimgovernor.presentation.v1.InputLeaseRequest` | `rimgovernor.presentation.v1.InputLeaseReply` |
| `rimgovernor/presentation_lease_video` | `rimgovernor.presentation.v1.PresentationMedia/LeaseVideo` | `rimgovernor.presentation.v1.VideoLeaseRequest` | `rimgovernor.presentation.v1.VideoReply` |
| `rimgovernor/presentation_main_tabs` | `rimgovernor.presentation.v1.PresentationReads/MainTabs` | `rimgovernor.presentation.v1.TabsRequest` | `rimgovernor.presentation.v1.TabsReply` |
| `rimgovernor/presentation_notifications` | `rimgovernor.presentation.v1.PresentationReads/Notifications` | `rimgovernor.presentation.v1.NotificationsRequest` | `rimgovernor.presentation.v1.NotificationsReply` |
| `rimgovernor/presentation_preview_dialog_text` | `rimgovernor.presentation.v1.PresentationReads/PreviewDialogText` | `rimgovernor.presentation.v1.DialogTextPreviewRequest` | `rimgovernor.presentation.v1.DialogTextPreviewReply` |
| `rimgovernor/presentation_preview_naming` | `rimgovernor.presentation.v1.PresentationReads/PreviewNaming` | `rimgovernor.presentation.v1.NamingPreviewRequest` | `rimgovernor.presentation.v1.NamingPreviewReply` |
| `rimgovernor/presentation_read_frame` | `rimgovernor.presentation.v1.PresentationMedia/ReadFrame` | `rimgovernor.presentation.v1.FrameRequest` | `rimgovernor.presentation.v1.FrameReply` |
| `rimgovernor/presentation_render_state` | `rimgovernor.presentation.v1.PresentationReads/RenderState` | `rimgovernor.presentation.v1.ReadRequest` | `rimgovernor.presentation.v1.RenderReply` |
| `rimgovernor/presentation_screen_targets_read` | `rimgovernor.presentation.v1.PresentationReads/ScreenTargetsRead` | `rimgovernor.presentation.v1.ReadRequest` | `rimgovernor.presentation.v1.ScreenTargetsReply` |
| `rimgovernor/presentation_selection` | `rimgovernor.presentation.v1.PresentationReads/Selection` | `rimgovernor.presentation.v1.ReadRequest` | `rimgovernor.presentation.v1.SelectionReply` |
| `rimgovernor/presentation_send_input` | `rimgovernor.presentation.v1.PlayerPresentation/SendInput` | `rimgovernor.presentation.v1.InputEvent` | `rimgovernor.presentation.v1.InputEventReply` |
| `rimgovernor/receipts_lookup` | `rimgovernor.receipts.v1.Attempts/Lookup` | `rimgovernor.receipts.v1.LookupRequest` | `rimgovernor.receipts.v1.LookupReply` |
| `rimgovernor/receipts_observe_progress` | `rimgovernor.receipts.v1.Attempts/ObserveProgress` | `rimgovernor.receipts.v1.ProgressRequest` | `rimgovernor.receipts.v1.ProgressReply` |
| `rimgovernor/observations_get_clearance_targets` | `rimgovernor.observations.v1.Observations/GetClearanceTargets` | `rimgovernor.observations.v1.ClearanceTargetsRequest` | `rimgovernor.observations.v1.ClearanceTargetsReply` |
