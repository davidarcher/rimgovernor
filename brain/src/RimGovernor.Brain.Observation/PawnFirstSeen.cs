namespace RimGovernor.Brain.Observation;

/// <summary>
/// What the pawn table said of a humanlike pawn the first time it appeared in a world: when, and its faction,
/// royal title and guest standing then. Later refreshes never change it.
/// </summary>
public sealed record PawnFirstSeen
{
    public Tick Tick { get; init; }

    /// <summary>FactionDef defName; empty without a faction.</summary>
    public string Faction { get; init; } = "";

    /// <summary>Most senior royal title defName; empty without one.</summary>
    public string Title { get; init; } = "";

    /// <summary>The holding faction's load id; empty when none.</summary>
    public string HostFaction { get; init; } = "";

    /// <summary>GuestStatus name while a host faction holds the pawn.</summary>
    public string Guest { get; init; } = "";

    public bool QuestLodger { get; init; }
}

/// <summary>
/// The per-world first-seen record: derived, held in memory only and never saved. A pawn present at a reload
/// looks new once. A world change (another colony, map or load) starts it empty.
/// </summary>
public sealed class FirstSeenRecord
{
    private readonly object _gate = new();
    private (ColonyId Colony, MapId Map, LoadId Load) _world;
    private Dictionary<string, PawnFirstSeen>? _seen;

    /// <summary>Records every humanlike pawn of <paramref name="pawns"/> not yet in the world of <paramref name="id"/>, at its tick, from its standing facts. A pawn already recorded is untouched.</summary>
    public void Observe(Identity id, Pawns pawns)
    {
        var world = (id.Colony, id.Map, id.Load);
        lock (_gate)
        {
            if (_seen is null || _world != world)
            {
                _world = world;
                _seen = new Dictionary<string, PawnFirstSeen>(StringComparer.Ordinal);
            }
            foreach (var (pawnId, row) in pawns.Table.All())
            {
                var standing = row.Standing;
                if (standing is null || !row.Humanlike.GetValueOrDefault())
                {
                    continue;
                }
                if (_seen.ContainsKey(pawnId))
                {
                    continue;
                }
                _seen[pawnId] = new PawnFirstSeen
                {
                    Tick = id.Tick,
                    Faction = standing.FactionDefName ?? "",
                    Title = standing.RoyalTitle ?? "",
                    HostFaction = standing.HostFaction?.Id ?? "",
                    Guest = standing.GuestStatus ?? "",
                    QuestLodger = standing.QuestLodger.GetValueOrDefault(),
                };
            }
        }
    }

    /// <summary>The first-seen facts of <paramref name="pawnId"/> in the world of <paramref name="id"/>, false when it has not been seen there.</summary>
    public bool TryGet(Identity id, string pawnId, out PawnFirstSeen seen)
    {
        lock (_gate)
        {
            if (_world != (id.Colony, id.Map, id.Load) || _seen is null || !_seen.TryGetValue(pawnId, out var found))
            {
                seen = new PawnFirstSeen();
                return false;
            }
            seen = found;
            return true;
        }
    }
}
