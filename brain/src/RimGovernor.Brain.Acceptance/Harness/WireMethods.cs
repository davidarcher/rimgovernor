using W = RimGovernor.Contracts.Wire;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The request and reply record of every <c>rimgovernor/*</c> wire method: the Contracts type the in-process host
/// deserializes the request into and serializes the reply from. <see cref="WireCodec"/> translates by these types, so a
/// method missing here is a harness error, never a request sent in a guessed dialect. It mirrors the host's native
/// facade, one entry per facade method; the codec test pins every entry to a registered Contracts type.
/// </summary>
public static class WireMethods
{
    private const string Prefix = "rimgovernor/";

    private static readonly Dictionary<string, (Type Request, Type Reply)> Table = new(StringComparer.Ordinal)
    {
        ["lifecycle_read_identity"] = (typeof(W.Lifecycle.IdentityRequest), typeof(W.Lifecycle.IdentityReply)),
        ["lifecycle_read_tick"] = (typeof(W.Lifecycle.TickRequest), typeof(W.Lifecycle.TickReply)),
        ["lifecycle_read_governor_state"] = (typeof(W.Lifecycle.GovernorStateRequest), typeof(W.Lifecycle.GovernorStateReply)),
        ["lifecycle_put_governor_state_batch"] = (typeof(W.Lifecycle.PutGovernorStateBatchRequest), typeof(W.Lifecycle.GovernorStateReply)),
        ["lifecycle_wait_save_signal"] = (typeof(W.Lifecycle.WaitSaveSignalRequest), typeof(W.Lifecycle.WaitSaveSignalReply)),
        ["lifecycle_flush_done"] = (typeof(W.Lifecycle.FlushDoneRequest), typeof(W.Lifecycle.FlushDoneReply)),
        ["authority_read_status"] = (typeof(W.Authority.StatusRequest), typeof(W.Authority.StatusReply)),
        ["authority_control"] = (typeof(W.Authority.ControlRequest), typeof(W.Authority.ControlReply)),
        ["placement_preview"] = (typeof(W.Placement.PlacementRequest), typeof(W.Placement.PlacementReply)),
        ["zones_preview"] = (typeof(W.Operations.ZonePreviewRequest), typeof(W.Operations.ZonePreviewReply)),
        ["observations_read_status"] = (typeof(W.Observations.StatusRequest), typeof(W.Observations.StatusReply)),
        ["observations_read_colony_facts"] = (typeof(W.Observations.ColonyFactsRequest), typeof(W.Observations.ColonyFactsReply)),
        ["observations_get_cells"] = (typeof(W.Observations.GetCellsRequest), typeof(W.Observations.GetCellsReply)),
        ["observations_list_buildings"] = (typeof(W.Observations.ListBuildingsRequest), typeof(W.Observations.ListBuildingsReply)),
        ["observations_list_pawns"] = (typeof(W.Observations.ListPawnsRequest), typeof(W.Observations.ListPawnsReply)),
        ["observations_list_supplies"] = (typeof(W.Observations.ListSuppliesRequest), typeof(W.Observations.ListSuppliesReply)),
        ["observations_list_rooms"] = (typeof(W.Observations.ListRoomsRequest), typeof(W.Observations.ListRoomsReply)),
        ["observations_read_research"] = (typeof(W.Observations.ResearchRequest), typeof(W.Observations.ResearchReply)),
        ["observations_read_definition_catalog"] = (typeof(W.Observations.DefinitionCatalogRequest), typeof(W.Observations.DefinitionCatalogReply)),
        ["observations_read_bills"] = (typeof(W.Observations.BillsRequest), typeof(W.Observations.BillsReply)),
        ["observations_read_recipes"] = (typeof(W.Observations.RecipesRequest), typeof(W.Observations.RecipesReply)),
        ["operations_apply"] = (typeof(W.Operations.ApplyRequest), typeof(W.Operations.ApplyReply)),
        ["receipts_lookup"] = (typeof(W.Receipts.LookupRequest), typeof(W.Receipts.LookupReply)),
        ["clock_run"] = (typeof(W.Clock.RunRequest), typeof(W.Clock.StatusReply)),
        ["clock_hold"] = (typeof(W.Clock.HoldRequest), typeof(W.Clock.StatusReply)),
        ["clock_heartbeat"] = (typeof(W.Clock.HeartbeatRequest), typeof(W.Clock.HeartbeatReply)),
        ["clock_read_status"] = (typeof(W.Clock.StatusRequest), typeof(W.Clock.StatusReply)),
        ["clock_read_events"] = (typeof(W.Clock.EventsRequest), typeof(W.Clock.EventsReply)),
        ["presentation_camera"] = (typeof(W.Presentation.ReadRequest), typeof(W.Presentation.CameraReply)),
        ["presentation_selection"] = (typeof(W.Presentation.ReadRequest), typeof(W.Presentation.SelectionReply)),
        ["presentation_colonists"] = (typeof(W.Presentation.ColonistRosterRequest), typeof(W.Presentation.ColonistRosterReply)),
        ["observations_get_clearance_targets"] = (typeof(W.Observations.ClearanceTargetsRequest), typeof(W.Observations.ClearanceTargetsReply)),
        ["combat_geometry"] = (typeof(W.Mirror.CombatGeometryRequest), typeof(W.Mirror.CombatGeometryReply)),
        ["observations_read_consumption"] = (typeof(W.Observations.ConsumptionRequest), typeof(W.Observations.ConsumptionReply)),
        ["observations_read_defense_site"] = (typeof(W.Observations.DefenseSiteRequest), typeof(W.Observations.DefenseSiteReply)),
        ["observations_read_lines_of_fire"] = (typeof(W.Observations.LinesOfFireRequest), typeof(W.Observations.LinesOfFireReply)),
        ["observations_read_husbandry"] = (typeof(W.Observations.HusbandryRequest), typeof(W.Observations.HusbandryReply)),
        ["presentation_notifications"] = (typeof(W.Presentation.NotificationsRequest), typeof(W.Presentation.NotificationsReply)),
        ["observations_read_population"] = (typeof(W.Observations.PopulationRequest), typeof(W.Observations.PopulationReply)),
        ["observations_list_resource_sources"] = (typeof(W.Observations.ResourceSourcesRequest), typeof(W.Observations.ResourceSourcesReply)),
        ["observations_read_spatial_access"] = (typeof(W.Observations.SpatialAccessRequest), typeof(W.Observations.SpatialAccessReply)),
        ["observations_evaluate_stat"] = (typeof(W.Observations.EvaluateStatRequest), typeof(W.Observations.EvaluateStatReply)),
        ["observations_read_trade_acquisition"] = (typeof(W.Observations.TradeAcquisitionRequest), typeof(W.Observations.TradeAcquisitionReply)),
        ["observations_list_traders"] = (typeof(W.Observations.TradersRequest), typeof(W.Observations.TradersReply)),
        ["observations_read_trade_session"] = (typeof(W.Observations.TradeSessionRequest), typeof(W.Observations.TradeSessionReply)),
        ["observations_read_trade_sheet"] = (typeof(W.Observations.TradeSheetRequest), typeof(W.Observations.TradeSheetReply)),
        ["observations_list_wall_upgrade_sites"] = (typeof(W.Observations.WallUpgradeSitesRequest), typeof(W.Observations.WallUpgradeSitesReply)),
        ["observations_read_world"] = (typeof(W.Observations.WorldRequest), typeof(W.Observations.WorldReply)),
        ["observations_read_world_progression"] = (typeof(W.Observations.WorldProgressionRequest), typeof(W.Observations.WorldProgressionReply)),
        ["observations_list_zones"] = (typeof(W.Observations.ListZonesRequest), typeof(W.Observations.ListZonesReply)),
        ["observations_read_excavation_site"] = (typeof(W.Observations.ExcavationSiteRequest), typeof(W.Observations.ExcavationSiteReply)),
        ["lifecycle_load"] = (typeof(W.Lifecycle.LoadRequest), typeof(W.Lifecycle.LoadReply)),
        ["lifecycle_read_load"] = (typeof(W.Lifecycle.RequestStatus), typeof(W.Lifecycle.LoadReply)),
        ["lifecycle_new_colony"] = (typeof(W.Lifecycle.NewColonyRequest), typeof(W.Lifecycle.NewColonyReply)),
        ["lifecycle_read_new_colony"] = (typeof(W.Lifecycle.RequestStatus), typeof(W.Lifecycle.NewColonyReply)),
        ["lifecycle_save"] = (typeof(W.Lifecycle.SaveRequest), typeof(W.Lifecycle.SaveReply)),
        ["lifecycle_read_save"] = (typeof(W.Lifecycle.RequestStatus), typeof(W.Lifecycle.SaveReply)),
        ["presentation_overlay"] = (typeof(W.Presentation.OverlayRequest), typeof(W.Presentation.OverlayReply)),
        ["presentation_render_state"] = (typeof(W.Presentation.ReadRequest), typeof(W.Presentation.RenderReply)),
        ["presentation_render_demand"] = (typeof(W.Presentation.RenderDemand), typeof(W.Presentation.RenderReply)),
        ["observations_read_snapshot"] = (typeof(W.Observations.SnapshotReadRequest), typeof(W.Observations.SnapshotReadReply)),
        ["observations_flush_snapshot"] = (typeof(W.Observations.FlushSnapshotRequest), typeof(W.Observations.FlushSnapshotReply)),
    };

    /// <summary>Every wire method name (without the <c>rimgovernor/</c> prefix) and its record types.</summary>
    public static IReadOnlyDictionary<string, (Type Request, Type Reply)> All => Table;

    /// <summary>The record types of <paramref name="method"/>, named with or without the <c>rimgovernor/</c> prefix.</summary>
    public static bool TryGet(string method, out (Type Request, Type Reply) types) =>
        Table.TryGetValue(method.StartsWith(Prefix, StringComparison.Ordinal) ? method[Prefix.Length..] : method, out types);
}
