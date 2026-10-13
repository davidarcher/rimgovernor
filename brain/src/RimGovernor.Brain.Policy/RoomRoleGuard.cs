namespace RimGovernor.Brain.Policy;

/// <summary>What stands in one planned room: the stations the role workers see (PredictRole's input).</summary>
public sealed record RoomContents
{
    /// <summary>PlannedRoom.Id.</summary>
    public int Room { get; init; }

    public EquatableArray<RoomPiece> Pieces { get; init; }
}

/// <summary>
/// Marks a built room whose native role is not the one PredictRole gives its contents; the planner re-prices
/// or splits a marked room. Its reason is RoleMismatchReason.
/// </summary>
public sealed record RoleMismatch
{
    public int Room { get; init; }

    public RoomRole Native { get; init; }

    public RoomRole Predicted { get; init; }

    public IReadOnlyDictionary<RoomRole, double> Scores { get; init; } = new Dictionary<RoomRole, double>();
}

public static partial class PolicyFunctions
{
    /// <summary>The stable reason a mismatch is recorded under (telemetry.Decide reason).</summary>
    public const string RoleMismatchReason = "native_role_differs_from_predicted";

    /// <summary>
    /// Compares each standing room's native role (the census) with PredictRole for its contents and returns
    /// the rooms that differ, by room id. A room that does not stand enclosed on its region, or whose native
    /// role is unknown, is skipped: there is nothing to compare. Contents naming no planned room, or a piece
    /// outside the catalog, throw.
    /// </summary>
    public static List<RoleMismatch> GuardNativeRoles(
        StationCatalog catalog, LayoutPlan plan, RoomObservation rooms, IReadOnlyList<RoomContents> contents)
    {
        var planned = plan.Rooms;
        var output = new List<RoleMismatch>();
        foreach (var c in contents)
        {
            var idx = -1;
            for (var i = 0; i < planned.Count; i++)
            {
                if (planned[i].Id == c.Room)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
            {
                throw new DomainException($"contents name room {c.Room}, which the plan does not hold");
            }
            var (census, standing) = CensusRoomIn(planned[idx], rooms);
            if (!standing || !census!.Role.TryGetValue(out var native))
            {
                continue;
            }
            RolePrediction pred;
            try
            {
                pred = PredictRole(catalog, c.Pieces);
            }
            catch (DomainException e)
            {
                throw new DomainException($"room {c.Room}: {e.Message}");
            }
            if (pred.Role != native)
            {
                output.Add(new RoleMismatch { Room = c.Room, Native = native, Predicted = pred.Role, Scores = pred.Scores });
            }
        }
        // Stable, by room id.
        return output.OrderBy(m => m.Room).ToList();
    }
}
