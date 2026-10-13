namespace RimGovernor.Brain.Policy;

// Demand-grown core rooms. A colony starts with only the base rooms (DefaultOwedRooms) and the bedroom wings;
// every room below joins the plan once its need shows, at the nearest core slot like any other add-on, beside
// its anchor when BesideRoles names one (the butchery behind the freezer). Nothing is reserved ahead of the
// need, so no rock is dug and no ground held for a room that is not up for building. The tomb and the morgue
// are planned from the start in the outskirts cluster (layout_outskirts_nodes); a further tomb is grown through
// the replan's tomb count (RoomGrowth is not asked for it).
public static partial class PolicyConstants
{
    /// <summary>The rooms RoomGrowth.Core may ask for, in the order they are added.</summary>
    internal static readonly IReadOnlyList<PlannedRole> DemandCoreRooms =
        [PlannedRole.Hospital, PlannedRole.Lab, PlannedRole.Rec, PlannedRole.Butchery, PlannedRole.Prison, PlannedRole.Battery];

    /// <summary>
    /// The rooms that serve a capability by template rather than by catalog compatibility: a workshop could
    /// take a research bench, but only the shelter is planned to.
    /// </summary>
    internal static IReadOnlyDictionary<PlannedRole, IReadOnlyList<Capability>> DesignatedHosts =>
        new Dictionary<PlannedRole, IReadOnlyList<Capability>> { [PlannedRole.Shelter] = ShelterCapabilities };
}

public static partial class PolicyFunctions
{
    /// <summary>Reports a planned room other than except that is designated to host c.</summary>
    public static bool HostsCapability(this LayoutPlan p, Capability c, PlannedRole except) =>
        p.Rooms.Any(r => r.Role != except && DesignatedHosts.TryGetValue(r.Role, out var hosted) && hosted.Contains(c));
}
