namespace RimGovernor.Brain.Policy;

// The worship room. An ideoligion that requires buildings (Ideoligion.RequiredBuildings: the
// building precepts' ThingDefs and the held rituals' required buildings, all read from the game) is owed one
// worship room holding one of each. It is staged exactly like the child rooms (ChildRooms, LayoutChildRooms): the
// layout review grows a core room sized to the footprints, shelter/sleeping shells it and places the buildings at the
// shared template's slots. The room is planned to meet each building precept's presence demand (#3808): at least the
// largest area any demand asks, the things they count (columns), floor carrying their terrain tags, none of the
// buildings they forbid, and the room quality target of the largest impressiveness they ask. Names come from the
// ideoligion and footprints from the native catalog, never constants here.

/// <summary>
/// The worship facts the planner and the worship credit read (#3808), resolved at the frame's read from the ideology
/// section: the worship room the ideoligion owes (null none), its believers on the map, its building precepts' presence
/// demands, the held rituals the room lets the colony hold, and the moods a demand gives. Empty without an ideoligion.
/// </summary>
public sealed record WorshipFacts
{
    public ChildRoomNeed? Need { get; init; }

    public IReadOnlyList<PawnId> Believers { get; init; } = [];

    public IReadOnlyList<BuildingDemand> Demands { get; init; } = [];

    public IReadOnlyList<RitualDef> Rituals { get; init; } = [];

    public BuildingDemandMoods? Moods { get; init; }

    /// <summary>The definitions the worship room places: its buildings and the things its demands count.</summary>
    public IReadOnlyList<string> Definitions =>
        Need is null ? [] : [.. Need.Furniture.Items.SelectMany(f => f.Defs.Items).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The terrain tags the room's floor must carry; none for any floor.</summary>
    public IReadOnlyList<string> FloorTags => Need is null ? [] : [.. Need.FloorTags.Items];
}

public static partial class WorshipRules
{
    /// <summary>
    /// The worship facts of the ideology mode (none without the expansion). The rituals are the held rituals the room lets the
    /// colony hold on its own (RitualRules.RitualDue's free start): a pattern the catalog has, naming a required building,
    /// startable anytime, on a positive cadence and with an outcome effect.
    /// </summary>
    public static WorshipFacts FactsOf(IdeologyMode mode)
    {
        if (mode is not IdeologyMode.Installed { Primary: var ideo })
        {
            return new WorshipFacts();
        }
        var (need, owed) = WorshipRoomNeed(ideo);
        var rituals = new List<RitualDef>();
        foreach (var held in ideo.Facts.Rituals.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (ideo.Defs.Rituals.TryGetValue(held.Pattern, out var def) && def.RequiredBuildings.Count > 0
                && (def.CanStartAnytime || def.AlwaysStartAnytime) && def.IntervalDaysMin > 0 && def.Outcome is not null)
            {
                rituals.Add(def);
            }
        }
        return new WorshipFacts
        {
            Need = owed ? need : null,
            Believers = ideo.Facts.BelieverPawns,
            Demands = [.. ideo.Facts.Buildings.Where(b => b.Building.Length != 0).Select(b => b.Demand)],
            Rituals = rituals,
            Moods = ideo.Defs.BuildingMoods,
        };
    }

    /// <summary>
    /// The worship room the ideoligion owes; Ok is false when it requires no building. Its furniture is one of each
    /// required building and, per thing a presence demand counts, the most any demand asks; its requirements the largest
    /// area and impressiveness any demand asks, the terrain tags every demand's floor requirement accepts (the tags common
    /// to them all) and every building any demand forbids.
    /// </summary>
    public static (ChildRoomNeed Need, bool Ok) WorshipRoomNeed(Ideoligion ideo)
    {
        var required = ideo.RequiredBuildings();
        if (required.Count == 0)
        {
            return (default!, false);
        }
        var demands = ideo.Facts.Buildings.Where(b => b.Building.Length != 0).Select(b => b.Demand).ToList();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in required)
        {
            counts[name] = 1;
        }
        foreach (var count in demands.SelectMany(d => d.Counts))
        {
            counts[count.Def] = Math.Max(counts.GetValueOrDefault(count.Def), count.Count);
        }
        IEnumerable<string>? tags = null;
        foreach (var d in demands.Where(d => d.FloorTags.Count > 0))
        {
            tags = tags is null ? d.FloorTags : tags.Intersect(d.FloorTags, StringComparer.Ordinal);
        }
        var need = new ChildRoomNeed
        {
            Role = RoomRole.WorshipRoom,
            Module = PlannedRole.Worship,
            Furniture = EquatableArray.From(counts.Select(c => new ChildFurniture { Defs = EquatableArray.From([c.Key]), Count = c.Value }).ToList()),
            MinArea = demands.Select(d => d.MinArea).DefaultIfEmpty(0).Max(),
            MinImpressiveness = demands.Select(d => d.MinImpressiveness).DefaultIfEmpty(0).Max(),
            FloorTags = EquatableArray.From((tags ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()),
            ForbiddenDefs = EquatableArray.From(demands.SelectMany(d => d.ForbiddenDefs).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()),
        };
        return (need, true);
    }

    /// <summary>
    /// The room quality target of the standing worship room: the largest impressiveness its presence demands ask, keyed
    /// by the census room's id. None while no demand asks one or the plan's worship room does not stand.
    /// </summary>
    public static IReadOnlyDictionary<string, RoomTarget> WorshipRoomTargets(LayoutPlan plan, RoomObservation rooms, WorshipFacts worship)
    {
        var empty = new Dictionary<string, RoomTarget>();
        if (worship.Need is not { MinImpressiveness: > 0 } need)
        {
            return empty;
        }
        foreach (var room in plan.RoomsOf(PlannedRole.Worship))
        {
            var (standing, ok) = PlannedRoom.CensusRoomIn(room, rooms);
            if (ok && standing is not null)
            {
                return new Dictionary<string, RoomTarget> { [standing.Id] = new RoomTarget { Min = need.MinImpressiveness, Reasons = ["worship"] } };
            }
        }
        return empty;
    }
}
