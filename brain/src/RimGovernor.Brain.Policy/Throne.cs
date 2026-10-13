using System.Globalization;

namespace RimGovernor.Brain.Policy;

// Staging the throne room. A colonist who holds an Empire title, or has the favor to claim the next one, is owed
// the throne room of the next title that asks for one (RoyalRung.Throne*): the plan grows a ThroneRoomSizes room
// (GrowThroneRoom, layout_throne.go), the sleeping planner raises its shell, places one of the title's throne
// definitions at the template slot, and furnishes the room to the title's minimum impressiveness through the room
// quality levers (ThroneRoomTargets). The throne's footprint is the native catalog's, never a constant here.
//
// Once the throne stands and the holder holds the title, NextThroneStep reports Assign until the royalty read
// lists the holder as the throne's owner (RoyalThrone); the runtime sends it as the generic Assign intent, and
// the step holds MaintainHousing open until the read shows it done.

/// <summary>ThingAnyOfCount asks for Count buildings of any of Things.</summary>
public sealed record ThingAnyOfCount
{
    public EquatableArray<string> Things { get; init; }

    public int Count { get; init; }
}

/// <summary>ThingCount asks for Count buildings of Def.</summary>
public sealed record ThingCount
{
    public string Def { get; init; } = "";

    public int Count { get; init; }
}

/// <summary>
/// One title's throneRoomRequirements as the def mirror holds them (RoyalTitleDef, RoomRequirement_* messages),
/// one field per requirement kind. Names are the game's own, never a C# list: nothing here is a def-name constant.
/// </summary>
public record ThroneRequirements
{
    /// <summary>The throne definitions the title accepts; a title that asks for no throne has none.</summary>
    public EquatableArray<string> Things { get; init; }

    /// <summary>Whether one must be assigned to the holder (HasAssignedThroneAnyOf).</summary>
    public bool Assigned { get; init; }

    /// <summary>The room minimum area.</summary>
    public int MinArea { get; init; }

    /// <summary>The room minimum impressiveness; zero when the title sets none.</summary>
    public int MinImpressiveness { get; init; }

    /// <summary>The terrain tags whose floors satisfy the flooring requirement (TerrainWithTags: every cell's terrain carries one), empty when the title does not ask for floors.</summary>
    public EquatableArray<string> FloorTags { get; init; }

    /// <summary>The flooring requirement's labelKey ("RoomRequirementAllFloored", "RoomRequirementAllFineFloored").</summary>
    public string FloorLabel { get; init; } = "";

    /// <summary>ThingAnyOfCount requirements (two braziers).</summary>
    public EquatableArray<ThingAnyOfCount> AnyOfCounts { get; init; }

    /// <summary>ThingCount requirements (the columns).</summary>
    public EquatableArray<ThingCount> Counts { get; init; }

    /// <summary>ThingAnyOf requirements (one instrument).</summary>
    public EquatableArray<EquatableArray<string>> AnyOf { get; init; }

    /// <summary>The def sets every standing building of which must be lit (AllThingsAnyOfAreGlowing, AllThingsAreGlowing).</summary>
    public EquatableArray<EquatableArray<string>> Glowing { get; init; }

    /// <summary>The building tags no building in the room may carry (ForbiddenBuildings: Production, Bed, Biotech, Anomaly).</summary>
    public EquatableArray<string> ForbiddenBuildingTags { get; init; }

    /// <summary>Whether an ideology altar is forbidden too.</summary>
    public bool ForbidAltars { get; init; }

    /// <summary>
    /// The building definitions those tags (and altars, when forbidden) name in the def catalog
    /// (ThingDef.building.buildingTags, ThingDef.isAltar), sorted: the set the room may hold none of. Resolved
    /// with the ladder (bridge WithThroneRequirements), not read from the title row.
    /// </summary>
    public EquatableArray<string> ForbiddenDefs { get; init; }

    /// <summary>Whether def is a building the throne room may not hold.</summary>
    public bool Forbids(string def) => ForbiddenDefs.Contains(def);
}

/// <summary>
/// The throne room a colonist is owed: the first title above the holder's current one (the current one at the
/// top) whose requirement names a throne and a minimum area.
/// </summary>
public sealed record ThroneNeed : ThroneRequirements
{
    public ThroneNeed() { }

    public ThroneNeed(ThroneRequirements requirements)
        : base(requirements) { }

    /// <summary>The colonist the room is for.</summary>
    public PawnId Holder { get; init; }

    /// <summary>The rung the room is sized to.</summary>
    public string Title { get; init; } = "";

    /// <summary>Whether the holder holds a title already: native lets only a titled colonist own a throne, so assignment waits for it.</summary>
    public bool Titled { get; init; }
}

/// <summary>A throne definition as the native catalog describes it.</summary>
public sealed record FurnitureDefinition
{
    public string Name { get; init; } = "";

    public Fact<bool> Available { get; init; }

    public Fact<Bounds> Size { get; init; }

    /// <summary>The room-role furniture roles the catalog's def rows give the definition (the PlanningDefinition view's RoomRoles).</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>The next throne step.</summary>
public enum ThroneStepKind
{
    /// <summary>Nothing is due, or a fact is unknown.</summary>
    [WireName("")]
    None,

    /// <summary>
    /// The room differs from the plan and the title's template (Template: the throne and the required
    /// furniture): the build side reconciles it (ReconcileRoom). The room's state is whatever the diff leaves;
    /// there is no shell, place or blocked step.
    /// </summary>
    [WireName("reconcile")]
    Reconcile,

    /// <summary>The throne stands unowned; assign Throne to Need.Holder, replacing PreviousThrone (empty: none).</summary>
    [WireName("assign")]
    Assign,

    /// <summary>The room's unlit light Lamp is out of fuel; order Pawn to refuel it.</summary>
    [WireName("refuel")]
    Refuel,

    /// <summary>
    /// A requirement still unmet has no definition the catalog makes available with a known size (Missing). The
    /// step names the failure (Detail) and is not Owed.
    /// </summary>
    [WireName("unavailable")]
    Unavailable,
}

/// <summary>One bounded step towards the title's throne room.</summary>
public sealed record ThroneStep
{
    public ThroneStepKind Kind { get; init; }

    public PlannedRoom Room { get; init; } = new();

    public ThroneNeed Need { get; init; } = new();

    /// <summary>A Reconcile's wanted furniture: the throne and each required piece, standing ones where they stand, missing ones in their template slot.</summary>
    public IReadOnlyList<WantedPiece> Template { get; init; } = [];

    /// <summary>The standing throne to assign.</summary>
    public string Throne { get; init; } = "";

    /// <summary>The throne the holder owns already, empty when none.</summary>
    public string PreviousThrone { get; init; } = "";

    /// <summary>A Refuel's light.</summary>
    public string Lamp { get; init; } = "";

    /// <summary>A Refuel's hauler.</summary>
    public PawnId Pawn { get; init; }

    /// <summary>The unmet requirements no available definition serves, each the any-of list of definition names, for Unavailable.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>Whether the step is a named failure planning cannot clear.</summary>
    public bool Failed => Kind == ThroneStepKind.Unavailable;

    /// <summary>Names a failed step: the title, the room and each requirement without an available definition.</summary>
    public string Detail()
    {
        var bounds = Room.Region.Bounds();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"the {Need.Title} throne room at ({bounds.X},{bounds.Z}) needs definitions the catalog does not make available with a known size: {string.Join("; ", Missing)}"
        );
    }

    /// <summary>Whether the planner can act on the step now.</summary>
    public bool Owed => Kind is ThroneStepKind.Reconcile or ThroneStepKind.Assign or ThroneStepKind.Refuel;
}

/// <summary>One counted piece requirement of the title: Count buildings of any of Things, planned in slots named Key.0, Key.1 and so on.</summary>
internal sealed record ThroneWant(string Key, IReadOnlyList<string> Things, int Count);

public static partial class PolicyFunctions
{
    /// <summary>The throne room's slot name for the throne itself (throneWants).</summary>
    internal const string ThroneSlot = "throne";

    /// <summary>The rung's throne requirement, false for a title that asks for no throne or whose requirement is not read.</summary>
    private static (ThroneNeed Need, bool Ok) RungRequirement(RoyalRung rung)
    {
        if (!rung.Throne.TryGetValue(out var req) || req.Things.Count == 0 || req.MinArea <= 0)
        {
            return (new ThroneNeed(), false);
        }
        req = req with { MinImpressiveness = Math.Max(req.MinImpressiveness, 0) };
        return (new ThroneNeed(req) { Title = rung.Title }, true);
    }

    /// <summary>
    /// The largest throne room any colonist is owed, false when none holds or can claim a title that asks for
    /// one. A colonist holds a title or can claim the next rung when its favor reaches the rung's FavorNeeded;
    /// the room sized is the first rung above its current title with a throne requirement, else the current
    /// title's own.
    /// </summary>
    public static (ThroneNeed Need, bool Ok) NextThroneNeed(RoyaltyFacts f)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < f.Ladder.Count; i++)
        {
            index[f.Ladder[i].Title] = i;
        }
        var holders = f.Holders.Keys.OrderBy(id => id).ToList();
        var best = new ThroneNeed();
        var found = false;
        foreach (var id in holders)
        {
            foreach (var h in f.Holders[id])
            {
                int current;
                if (h.Title.Length != 0)
                {
                    if (!index.TryGetValue(h.Title, out current))
                    {
                        continue;
                    }
                }
                else
                {
                    // No title yet: owed only once the first rung is within reach of the favor held.
                    current = -1;
                    var fk = h.Favor.TryGetValue(out var favor);
                    if (f.Ladder.Count == 0)
                    {
                        continue;
                    }
                    var nk = f.Ladder[0].FavorNeeded.TryGetValue(out var needed);
                    if (!fk || !nk || favor < needed)
                    {
                        continue;
                    }
                }
                var need = new ThroneNeed();
                var ok = false;
                for (var i = current + 1; i < f.Ladder.Count && !ok; i++)
                {
                    (need, ok) = RungRequirement(f.Ladder[i]);
                }
                if (!ok && current >= 0)
                {
                    (need, ok) = RungRequirement(f.Ladder[current]);
                }
                if (!ok)
                {
                    continue;
                }
                need = need with { Holder = id, Titled = HoldsTitle(f, id) };
                if (!found || need.MinArea > best.MinArea || (need.MinArea == best.MinArea && need.MinImpressiveness > best.MinImpressiveness))
                {
                    best = need;
                    found = true;
                }
            }
        }
        return (best, found);
    }

    /// <summary>The standing buildings of a class need forbids with a cell inside room's interior, in census order.</summary>
    private static List<CurrentBuilding> ThroneIntruders(PlannedRoom room, ThroneNeed need, IReadOnlyList<CurrentBuilding> built)
    {
        var output = new List<CurrentBuilding>();
        if (need.ForbiddenDefs.Count == 0)
        {
            return output;
        }
        var inside = new HashSet<Cell>(RectCells(room.Region.Bounds()));
        foreach (var b in built)
        {
            if (!need.Forbids(b.Building!.Definition()))
            {
                continue;
            }
            if (b.Cells.Any(inside.Contains))
            {
                output.Add(b);
            }
        }
        return output;
    }

    /// <summary>The first of need's throne definitions the catalog makes available with a known footprint.</summary>
    private static (InteriorPieceDef Def, bool Ok) ThroneDefinition(ThroneNeed need, IReadOnlyList<FurnitureDefinition> defs) =>
        AvailableDefinition(need.Things, defs);

    /// <summary>The title's counted piece requirements (the braziers, the columns, the instrument), in the mirror's order, besides the throne.</summary>
    internal static List<ThroneWant> Wants(this ThroneNeed n)
    {
        var output = new List<ThroneWant>();
        for (var i = 0; i < n.AnyOfCounts.Count; i++)
        {
            output.Add(new ThroneWant(FormattableString.Invariant($"anyofcount{i}"), n.AnyOfCounts[i].Things, n.AnyOfCounts[i].Count));
        }
        for (var i = 0; i < n.Counts.Count; i++)
        {
            output.Add(new ThroneWant(FormattableString.Invariant($"count{i}"), [n.Counts[i].Def], n.Counts[i].Count));
        }
        for (var i = 0; i < n.AnyOf.Count; i++)
        {
            output.Add(new ThroneWant(FormattableString.Invariant($"anyof{i}"), n.AnyOf[i], 1));
        }
        return output;
    }

    /// <summary>Every definition the title's piece requirements name, the throne's included: the definitions whose catalog rows the step needs.</summary>
    public static IReadOnlyList<string> DefNames(this ThroneNeed n)
    {
        var names = new List<string>(n.Things);
        foreach (var w in n.Wants())
        {
            names.AddRange(w.Things);
        }
        return names;
    }

    /// <summary>The first of things the catalog makes available with a known footprint.</summary>
    private static (InteriorPieceDef Def, bool Ok) AvailableDefinition(IReadOnlyList<string> things, IReadOnlyList<FurnitureDefinition> defs)
    {
        foreach (var thing in things)
        {
            foreach (var d in defs)
            {
                if (d.Name != thing)
                {
                    continue;
                }
                var ak = d.Available.TryGetValue(out var available);
                var sk = d.Size.TryGetValue(out var size);
                if (ak && available && sk && size.Width > 0 && size.Height > 0)
                {
                    return (new InteriorPieceDef(d.Name, new Cell(size.Width, size.Height), default, null), true);
                }
            }
        }
        return (new InteriorPieceDef("", default, default, null), false);
    }

    /// <summary>The standing throne of need inside r's interior.</summary>
    private static (CurrentBuilding? Building, bool Ok) StandingThroneIn(PlannedRoom r, ThroneNeed need, IReadOnlyList<CurrentBuilding> built)
    {
        foreach (var b in built)
        {
            if (b.Cells.Count == 0 || !RectInside(r.Region.Bounds(), CellsRectangle(b.Cells)))
            {
                continue;
            }
            foreach (var thing in need.Things)
            {
                if (b.Building!.Definition() == thing)
                {
                    return (b, true);
                }
            }
        }
        return (null, false);
    }

    /// <summary>Whether id holds an Empire title now.</summary>
    private static bool HoldsTitle(RoyaltyFacts f, PawnId id)
    {
        if (!f.Holders.TryGetValue(id, out var holdings))
        {
            return false;
        }
        foreach (var h in holdings)
        {
            if (h.Title.Length != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The assignment still due for the standing throne: it must be listed by the royalty read (a throne built
    /// after the read waits for the next one) and unowned, and the holder must hold the title. A throne another
    /// colonist owns is left alone.
    /// </summary>
    private static ThroneStep ThroneAssignment(ThroneStep step, CurrentBuilding throne, IReadOnlyList<RoyalThrone> thrones)
    {
        if (!step.Need.Assigned || !step.Need.Titled)
        {
            return new ThroneStep();
        }
        var listed = false;
        var previous = step.PreviousThrone;
        foreach (var t in thrones)
        {
            if (t.Id == throne.Id)
            {
                if (!t.Owner.IsEmpty)
                {
                    return new ThroneStep();
                }
                listed = true;
            }
            else if (t.Owner == step.Need.Holder)
            {
                previous = t.Id;
            }
        }
        if (!listed)
        {
            return new ThroneStep();
        }
        return step with { PreviousThrone = previous, Kind = ThroneStepKind.Assign, Throne = throne.Id };
    }

    /// <summary>
    /// Picks the next throne step for need from the plan, the room census, the colony's walls and doors,
    /// buildings and the throne definitions. None while the plan holds no room of the title's area (the layout
    /// review owes it), and once the room matches, the throne stands and is assigned (or cannot be yet). An
    /// unmet requirement with no available definition is Unavailable. Otherwise the room is reconciled whenever
    /// its ring, doors or furniture differ from the plan and the template, or it holds a forbidden building
    /// (packed, not blocked); the throne's assignment comes first.
    /// </summary>
    public static ThroneStep NextThroneStep(
        LayoutPlan plan,
        RoomObservation rooms,
        GroundCensus ground,
        IReadOnlyList<CurrentBuilding> built,
        ThroneNeed need,
        IReadOnlyList<FurnitureDefinition> allDefs,
        IReadOnlyList<RoyalThrone> thrones
    )
    {
        var (room, ok) = plan.ThroneRoomFor(need.MinArea);
        if (!ok)
        {
            return new ThroneStep();
        }
        // A forbidden class is never planned: its definitions are not offered.
        var defs = allDefs.Where(d => !need.Forbids(d.Name)).ToList();
        var step = new ThroneStep { Room = room, Need = need };
        var (standing, stands) = StandingThroneIn(room, need, built);
        if (stands)
        {
            var assigned = ThroneAssignment(step, standing!, thrones);
            if (assigned.Kind != ThroneStepKind.None)
            {
                return assigned;
            }
        }
        var (def, dok) = ThroneDefinition(need, defs);
        // An unmet requirement no available definition serves is a named failure, not a silent skip.
        var missing = new List<string>();
        if (!stands && !dok && need.Things.Count > 0)
        {
            missing.Add(string.Join(" or ", need.Things));
        }
        foreach (var w in need.Wants())
        {
            if (!AvailableDefinition(w.Things, defs).Ok && StandingCount(room, w.Things, built) < w.Count)
            {
                missing.Add(string.Join(" or ", w.Things));
            }
        }
        if (missing.Count > 0)
        {
            return step with { Kind = ThroneStepKind.Unavailable, Missing = missing };
        }
        if (!stands && !dok)
        {
            return new ThroneStep();
        }
        var (inRoom, rok) = InteriorRoomFromLayout(room, rooms.Shapes);
        if (!rok)
        {
            return new ThroneStep();
        }
        // The template plans every required piece in its slot whether or not it stands; a piece that stands is
        // wanted where it stands, each missing one from the first available definition of its any-of list (a
        // requirement no available definition serves plans nothing).
        var required = new List<RequiredPiece>(inRoom!.Required);
        foreach (var w in need.Wants())
        {
            var (d, dokw) = AvailableDefinition(w.Things, defs);
            if (!dokw)
            {
                continue;
            }
            for (var i = 0; i < w.Count; i++)
            {
                required.Add(new RequiredPiece(FormattableString.Invariant($"{w.Key}.{i}"), d));
            }
        }
        var (interior, pok) = PlanRoomInterior(inRoom with { Required = required }, def);
        if (!pok)
        {
            return new ThroneStep();
        }
        var taken = new HashSet<Cell>();
        foreach (var b in built)
        {
            foreach (var c in b.Cells)
            {
                taken.Add(c);
            }
        }
        bool Free(InteriorPiece p) => RectCells(p.Rect).All(c => !taken.Contains(c));
        WantedPiece StandingPiece(CurrentBuilding b)
        {
            var r = CellsRectangle(b.Cells);
            return new WantedPiece
            {
                DefName = b.Building!.Definition(),
                Minimum = new Cell(r.X, r.Z),
                Maximum = new Cell(r.X + r.Width - 1, r.Z + r.Height - 1),
            };
        }
        var template = new List<WantedPiece>();
        var absent = false;
        if (stands)
        {
            template.Add(StandingPiece(standing!));
        }
        else
        {
            absent = true;
            foreach (var p in interior.Pieces)
            {
                if (p.Slot == ThroneSlot && p.Def == def.Def)
                {
                    if (!Free(p))
                    {
                        return new ThroneStep();
                    }
                    template.Add(p.Wanted());
                    break;
                }
            }
        }
        foreach (var w in need.Wants())
        {
            var count = 0;
            foreach (var b in built)
            {
                if (count < w.Count && b.Cells.Count > 0 && RectInside(room.Region.Bounds(), CellsRectangle(b.Cells)) && w.Things.Contains(b.Building!.Definition()))
                {
                    template.Add(StandingPiece(b));
                    count++;
                }
            }
            foreach (var p in interior.Pieces)
            {
                var dot = p.Slot.IndexOf('.', StringComparison.Ordinal);
                var key = dot < 0 ? p.Slot : p.Slot[..dot];
                if (count < w.Count && key == w.Key && p.Slot != ThroneSlot && Free(p))
                {
                    template.Add(p.Wanted());
                    absent = true;
                    count++;
                }
            }
        }
        step = step with { Template = template };
        if (absent || !plan.GroundMatches(room, ground) || ThroneIntruders(room, need, built).Count > 0)
        {
            return step with { Kind = ThroneStepKind.Reconcile };
        }
        return new ThroneStep();
    }

    /// <summary>The number of buildings of any of things standing inside r's interior.</summary>
    private static int StandingCount(PlannedRoom r, IReadOnlyList<string> things, IReadOnlyList<CurrentBuilding> built)
    {
        var n = 0;
        foreach (var b in built)
        {
            if (b.Cells.Count > 0 && RectInside(r.Region.Bounds(), CellsRectangle(b.Cells)) && things.Contains(b.Building!.Definition()))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// The impressiveness target of the standing throne room, keyed by census room id, for the room quality
    /// levers (the room upgrade and the beauty upgrade): the title's minimum, reason "title". Empty when the plan
    /// holds no standing room of the title's area or the title asks for no impressiveness.
    /// </summary>
    public static IReadOnlyDictionary<string, RoomTarget> ThroneRoomTargets(LayoutPlan plan, RoomObservation rooms, ThroneNeed need)
    {
        var empty = new Dictionary<string, RoomTarget>();
        var (room, ok) = plan.ThroneRoomFor(need.MinArea);
        if (!ok || need.MinImpressiveness <= 0)
        {
            return empty;
        }
        // Census: the target is keyed by the room's id.
        var (standing, sok) = CensusRoomIn(room, rooms);
        if (!sok)
        {
            return empty;
        }
        return new Dictionary<string, RoomTarget>
        {
            [standing.Id] = new RoomTarget { Room = standing.Id, Min = need.MinImpressiveness, Reasons = ["title"] },
        };
    }

    /// <summary>The room area the plan must grow for need, zero when it already holds one (or nobody is owed a throne).</summary>
    public static int ThroneAreaOwed(LayoutPlan plan, ThroneNeed need, bool owed)
    {
        if (!owed)
        {
            return 0;
        }
        if (plan.ThroneRoomFor(need.MinArea).Ok)
        {
            return 0;
        }
        return need.MinArea;
    }
}
