namespace RimGovernor.Brain.Policy;

partial record LayoutPlan
{
    /// <summary>
    /// The site rooms of the station graph (habitat 3.3): a room node for each new site room the ask's candidate holds
    /// (ActivitySiteAsk.NewRooms), at least the floor the plan's site room of that activity already has, and one for each
    /// standing site room of the plan no new room asks for, so a built site room is a node to pin. Each is a station-less
    /// recreation room keyed to its activity (RoomNode.Activity), matched only to the site room of the same activity.
    /// </summary>
    public static IReadOnlyList<RoomNode> ActivityRoomNodes(LayoutPlan plan, IReadOnlySet<int>? built, ActivitySiteAsk ask)
    {
        var nodes = new List<RoomNode>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        int Area(PlannedRoom r)
        {
            var b = r.Region.Bounds();
            return b.Width * b.Height;
        }
        RoomNode Node(string activity, int area) => new()
        {
            Id = "activity/" + activity, Role = ActivitySiteRules.SiteRoomRole, Activity = activity, Area = area, Reason = ActivitySiteRules.ReasonSiteRoom,
        };
        foreach (var room in ask.NewRooms())
        {
            var key = ActivitySiteRules.ActivityKey(room.Kind);
            if (!emitted.Add(key))
            {
                continue;
            }
            var held = plan.Rooms.Where(r => r.Activity == key).Select(Area).DefaultIfEmpty(0).Max();
            nodes.Add(Node(key, Math.Max(held, room.Area)));
        }
        foreach (var r in plan.Rooms.Where(r => r.Activity.Length != 0 && built is not null && built.Contains(r.Id)).OrderBy(r => r.Id))
        {
            if (emitted.Add(r.Activity))
            {
                nodes.Add(Node(r.Activity, Area(r)));
            }
        }
        return nodes;
    }
}

partial record LayoutPlan
{
    /// <summary>The plan holding the activity sites its grouping chose (StationAdvice.ActivitySites), resolved over its numbered rooms.</summary>
    public LayoutPlan WithActivitySites(IReadOnlyList<ActivityPlacement> placements) =>
        this with { ActivitySites = ActivitySiteRules.Resolve(this, placements) };
}
