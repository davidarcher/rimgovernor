namespace RimGovernor.Brain.Policy;

public static partial class PlannedRoomCredits
{
    /// <summary>The functions of the stations actually assigned to each saved room. Derived for one forecast, never stored beside the layout.</summary>
    public static IReadOnlyDictionary<int, EquatableArray<PlannedRole>> FunctionsOf(ReviewFacts facts)
    {
        var output = new Dictionary<int, EquatableArray<PlannedRole>>();
        IEnumerable<PlannedRoom> rooms = facts.LayoutPlan is { } layout ? layout.Rooms : facts.PlannedShelters;
        foreach (var room in rooms)
        {
            var functions = new HashSet<PlannedRole> { room.Role };
            foreach (var piece in facts.LayoutPlan?.Pieces.GetValueOrDefault(room.Id) ?? [])
            {
                var purpose = PurposeOf(piece);
                if (!facts.Stations.Stations.TryGetValue(piece.Def, out var station))
                {
                    if (purpose != StationPurpose.None) throw new RequiredFactException("station:" + piece.Def);
                    continue;
                }
                var role = RoomRoleRules.PredictRole(facts.Stations,
                    [new RoomPiece { Def = piece.Def, State = LayoutClusterRules.PieceState(purpose, station) }]).Role;
                var (planned, known) = InteriorRules.PlannedRoleOf(role);
                if (known && !(planned == PlannedRole.Bedroom && room.Role == PlannedRole.Barracks)) functions.Add(planned);
                if (purpose == StationPurpose.Dine) functions.Add(PlannedRole.Dining);
                if (purpose == StationPurpose.StoreCold) functions.Add(PlannedRole.Freezer);
                if (purpose == StationPurpose.Store) functions.Add(PlannedRole.Storage);
                if (purpose == StationPurpose.SleepMed) functions.Add(PlannedRole.Hospital);
                if (purpose == StationPurpose.SleepPrison) functions.Add(PlannedRole.Prison);
                if (purpose == StationPurpose.SleepBaby) functions.Add(PlannedRole.Nursery);
            }
            if (room.Meals > 0) functions.Add(PlannedRole.Kitchen);
            if (room.FilthRuns.Values.Any(r => r.Runs > 0)) functions.Add(PlannedRole.Butchery);
            if (facts.Sleeping.Beds.Any(b => b.Medical && b.Humanlike && !b.Prisoners && room.Region.Cells().Contains(b.Cell))) functions.Add(PlannedRole.Hospital);
            if (facts.Sleeping.Beds.Any(b => b.Prisoners && b.Humanlike && room.Region.Cells().Contains(b.Cell))) functions.Add(PlannedRole.Prison);
            if (room.Role != PlannedRole.Barracks && facts.Sleeping.Beds.Any(b => !b.Medical && !b.Prisoners && b.Humanlike && room.Region.Cells().Contains(b.Cell))) functions.Add(PlannedRole.Bedroom);
            output[room.Id] = EquatableArray.From(functions.OrderBy(role => Wire.Name(role), StringComparer.Ordinal));
        }
        return output;
    }

    /// <summary>The graph node which placed a piece is its existing Row, compared with the typed node identity constructor.</summary>
    internal static StationPurpose PurposeOf(InteriorPiece piece) => Enum.GetValues<StationPurpose>()
        .FirstOrDefault(purpose => piece.Row == LayoutGraphRules.NodeId(piece.Def, purpose)
            || piece.Row.StartsWith(LayoutGraphRules.NodeId(piece.Def, purpose) + "/", StringComparison.Ordinal));

    public static bool HasPurpose(LayoutPlan? layout, int room, StationPurpose purpose) =>
        (layout?.Pieces.GetValueOrDefault(room) ?? []).Any(piece => PurposeOf(piece) == purpose);

    public static bool Cold(LayoutPlan? layout, PlannedRoom room) => room.Role == PlannedRole.Freezer || HasPurpose(layout, room.Id, StationPurpose.StoreCold);

    /// <summary>Every function the room's actual stations serve, once each. Its primary layout role is always present.</summary>
    internal static IEnumerable<PlannedRole> Functions(ForecastCoupledContext context, int room) =>
        context.PlannedRoomFunctions.GetValueOrDefault(room).Items
            .Concat(context.PlannedRoomRoles.TryGetValue(room, out var role) ? [role] : []).Distinct();

    internal static bool Serves(ForecastCoupledContext context, int room, PlannedRole role) => Functions(context, room).Contains(role);

    /// <summary>The same station-flow sleep exposure for an already saved layout, recomputed with the current schedule.</summary>
    public static IReadOnlyDictionary<int, double> SleepDisturbanceOf(LayoutPlan layout, StationGraph graph, StationCatalog catalog, RestSchedule? rest)
    {
        var result = new Dictionary<int, double>();
        foreach (var room in layout.Rooms)
        {
            var pieces = layout.Pieces.GetValueOrDefault(room.Id) ?? [];
            if (!pieces.Any(p => PurposeOf(p) == StationPurpose.Sleep)) continue;
            var shares = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var node in graph.Nodes.Where(n => n.Count > 0 && n.Purpose is not (StationPurpose.Sleep or StationPurpose.SleepMed or StationPurpose.SleepPrison or StationPurpose.SleepBaby) && !n.Purpose.ClusterAdded()))
            {
                var count = pieces.Count(p => p.Def == node.Def && p.Row == node.Id);
                if (count > 0) shares[node.Id] = (double)count / node.Count;
            }
            var points = DisturbedMood(graph, catalog.Mood.SleepDisturbed, rest, shares);
            if (points > 0) result[room.Id] = points;
        }
        return result;
    }

    public static IReadOnlyDictionary<int, double> SleepDisturbanceOf(StationAdvice advice)
    {
        var tiled = advice.Tiled ?? throw new RequiredFactException("tiled room sharing candidate");
        return advice.Clustering.Groups.Where(g => tiled.Rooms.ContainsKey(g.Id))
            .Select(g => (Room: tiled.Rooms[g.Id], Points: g.LostEffects.Where(e => e.Effect == RoleEffect.SleepDisturbed).Sum(e => e.Points)))
            .Where(p => p.Points > 0).ToDictionary(p => p.Room, p => p.Points);
    }

    internal static double DisturbedMood(StationGraph graph, MemoryThought? thought, RestSchedule? rest, IReadOnlyDictionary<string, double> shares)
    {
        var visits = graph.Edges.Sum(edge => edge.Weight * Math.Min(1, shares.GetValueOrDefault(edge.A) + shares.GetValueOrDefault(edge.B)));
        if (!(visits > 0)) return 0;
        var perDay = visits * (rest ?? throw new RequiredFactException("rest_schedule")).DisturbedShare();
        return perDay > 0 ? -(thought ?? throw new RequiredFactException("sleep_disturbed_thought")).Mood(perDay) : 0;
    }
}
