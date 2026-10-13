namespace RimGovernor.Brain.Policy;

// Staging the tomb. A dead colonist in a sarcophagus gives every colonist KnowBuriedInSarcophagus (+4 mood, 8
// days, stacking), so once ComplexFurniture makes the sarcophagus available, a colonist corpse with no empty
// grave waiting raises a planned tomb's shell and then reconciles its room (ReconcileRoom) to the next free
// template sarcophagus: the ring, the floor and the piece, installed from packed stock first. The memory comes
// only from a sarcophagus's first burial, so a filled one is never reused: when every planned tomb is full the
// plan grows another. Where no sarcophagus can be had (research or stuff) a plain Grave takes the body instead,
// placed only in the next free slot of a planned graveyard (GraveyardSlots); the graveyard's fence and gate are
// raised with the grave. When the graveyard has no slot left the body waits in the morgue and the department
// asks layout for a further graveyard (GraveyardsWanted). Vanilla haulers inter colonist corpses in any empty
// grave on their own; nothing here hauls.
//
// Stranger corpses (raiders, visitors, prisoners) feed the tomb too, for the mood memory alone: a stranger is
// owed a fresh sarcophagus only while fewer than StrangerTombStackCap KnowBuriedInSarcophagus stacks are live
// across the colonists and the next sarcophagus is funded from stock (StrangerTomb). Over the cap, unfunded or
// with the mood thoughts unread, a stranger keeps the morgue path; the plain grave never takes one.
//
// A stranger's burial in a fresh sarcophagus is its first body ever, so the memory has fired: while the memory
// is live the filled sarcophagus is deconstructed (TombDispose, StrangerDisposals). Native ejects the corpse
// next to the cell, where the incinerator's burnable filter takes it, and the cell is a free tomb slot again.
// Only a sarcophagus in a planned tomb that holds nothing but strangers is touched; a colonist's sarcophagus and
// every grave stay.

/// <summary>
/// What the tomb reads to count stranger corpses: the live memory stacks and whether the next sarcophagus is
/// funded from stock. The default value (unread thoughts, or unfunded) stages no stranger.
/// </summary>
public sealed record StrangerTomb
{
    public int Live { get; init; }

    public bool Funded { get; init; }

    /// <summary>How many stranger corpses are owed a sarcophagus.</summary>
    public int Allowed() => Funded ? Math.Max(PolicyFunctions.StrangerTombStackCap - Live, 0) : 0;
}

/// <summary>The next tomb step.</summary>
public enum TombStepKind
{
    /// <summary>No unburied colonist corpse, enough empty graves, no planned tomb, or a fact is unknown.</summary>
    [WireName("")]
    None,

    /// <summary>
    /// Reconcile Room, the planned tomb, to Template, its next free sarcophagus, or Room, the planned
    /// graveyard, to Template, its next free grave (ReconcileRoom): the ring, the floor and the piece, whatever
    /// the diff still owes. There is no shell or place step.
    /// </summary>
    [WireName("reconcile")]
    Reconcile,

    /// <summary>Disposal are filled stranger sarcophagi whose memory has fired; the planner deconstructs one.</summary>
    [WireName("dispose")]
    Dispose,

    /// <summary>Every planned tomb's slots are taken; the plan owes another tomb room.</summary>
    [WireName("full")]
    Full,
}

/// <summary>One bounded step towards a grave for every dead colonist.</summary>
public sealed record TombStep
{
    public TombStepKind Kind { get; init; }

    public PlannedRoom Room { get; init; } = new();

    /// <summary>A Reconcile's wanted furniture: the next free sarcophagus or grave in its slot.</summary>
    public IReadOnlyList<WantedPiece> Template { get; init; } = [];

    /// <summary>The unburied colonist corpses.</summary>
    public int Dead { get; init; }

    /// <summary>The empty graves and sarcophagi.</summary>
    public int Empty { get; init; }

    /// <summary>The plain graves standing.</summary>
    public int Graves { get; init; }

    /// <summary>A Dispose's filled stranger sarcophagi, in census order.</summary>
    public IReadOnlyList<CurrentBuilding> Disposal { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>The memory a colonist gains when the first body ever is hauled into a sarcophagus; it stacks (4, 2, 1, 0.5) for 8 days.</summary>
    public const string KnowBuriedInSarcophagusThought = "KnowBuriedInSarcophagus";

    /// <summary>
    /// The most live KnowBuriedInSarcophagus stacks the tomb feeds strangers for. Kept: the memory's stack depth
    /// is the game's (docs/developers/contracts/kept-constants.md).
    /// </summary>
    public const int StrangerTombStackCap = 4;

    /// <summary>A colonist's summed memory offset at 1, 2, 3 and 4 live stacks.</summary>
    private static readonly double[] KnowBuriedStackTotals = [4, 6, 7, 7.5];

    /// <summary>
    /// The live KnowBuriedInSarcophagus stacks across the colonists: the most any colonist carries, read from the
    /// summed memory offset. Unknown while any colonist's thoughts, or the census, are unread.
    /// </summary>
    public static (int Live, bool Ok) KnowBuriedStacks(Fact<EquatableArray<MoodPawn>> pawns)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return (0, false);
        }
        var live = 0;
        foreach (var p in rows)
        {
            if (!p.Thoughts.TryGetValue(out var thoughts))
            {
                return (0, false);
            }
            foreach (var t in thoughts)
            {
                if (t.Def != KnowBuriedInSarcophagusThought)
                {
                    continue;
                }
                var n = 0;
                foreach (var total in KnowBuriedStackTotals)
                {
                    if (t.Offset >= total - 0.01)
                    {
                        n++;
                    }
                }
                live = Math.Max(live, n);
            }
        }
        return (live, true);
    }

    /// <summary>Core's plain grave: 1x2, no stuff, no research.</summary>
    public const string GraveDefinition = "Grave";

    /// <summary>
    /// The standing sarcophagi to deconstruct: in a planned tomb, holding a buried stranger and no colonist,
    /// while the memory is live (strangers.Live &gt; 0; unread thoughts leave it zero). Graves and any
    /// sarcophagus outside the plan are never listed.
    /// </summary>
    public static IReadOnlyList<CurrentBuilding> StrangerDisposals(
        LayoutPlan plan,
        IReadOnlyList<WasteItem> waste,
        IReadOnlyList<CurrentBuilding> built,
        string sarcophagus,
        StrangerTomb strangers
    )
    {
        if (strangers.Live <= 0)
        {
            return [];
        }
        var tomb = new HashSet<Cell>();
        foreach (var r in plan.RoomsOf(PlannedRole.Tomb))
        {
            foreach (var c in RectCells(r.Region.Bounds()))
            {
                tomb.Add(c);
            }
        }
        // holds[grave] is true while every buried corpse in it is a stranger.
        var holds = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in waste)
        {
            if (item.State != WasteState.Buried || item.Grave.Length == 0)
            {
                continue;
            }
            var stranger = item.CorpseOf == CorpseOf.Stranger;
            holds[item.Grave] = holds.TryGetValue(item.Grave, out var prior) ? prior && stranger : stranger;
        }
        var output = new List<CurrentBuilding>();
        foreach (var b in built)
        {
            if (b.Building!.Definition != sarcophagus || !holds.GetValueOrDefault(b.Id) || b.Cells.Count == 0)
            {
                continue;
            }
            if (b.Cells.All(tomb.Contains))
            {
                output.Add(b);
            }
        }
        return output;
    }

    /// <summary>
    /// Counts the dead, the empty graves and the plain graves, and returns the cells graves stand on. Strangers
    /// count as dead only up to strangers (StrangerTomb.Allowed); a buried body of either kind fills its grave.
    /// </summary>
    private static (TombStep Step, HashSet<Cell> Taken) TombCensus(IReadOnlyList<WasteItem> waste, IReadOnlyList<CurrentBuilding> built, string sarcophagus, int strangers)
    {
        var dead = 0;
        var empty = 0;
        var graves = 0;
        var filled = new HashSet<string>(StringComparer.Ordinal);
        var stranded = 0;
        foreach (var item in waste)
        {
            if (item.CorpseOf != CorpseOf.Colonist && item.CorpseOf != CorpseOf.Stranger)
            {
                continue;
            }
            if (item.State == WasteState.Buried)
            {
                filled.Add(item.Grave);
            }
            else if (item.CorpseOf == CorpseOf.Colonist)
            {
                dead++;
            }
            else if (item.EverBuriedInSarcophagus)
            {
                // Vanilla fires no memory on a re-burial: the morgue's.
            }
            else
            {
                stranded++;
            }
        }
        dead += Math.Min(stranded, strangers);
        var taken = new HashSet<Cell>();
        foreach (var b in built)
        {
            var def = b.Building!.Definition;
            if (def != sarcophagus && def != GraveDefinition)
            {
                continue;
            }
            if (def == GraveDefinition)
            {
                graves++;
            }
            if (!filled.Contains(b.Id))
            {
                empty++;
            }
            foreach (var c in b.Cells)
            {
                taken.Add(c);
            }
        }
        return (new TombStep { Dead = dead, Empty = empty, Graves = graves }, taken);
    }

    /// <summary>The room's first template sarcophagus slot no grave stands on.</summary>
    private static (InteriorPiece? Piece, bool Ok) TombSlot(PlannedRoom r, PieceShapes shapes, HashSet<Cell> taken)
    {
        var (inRoom, ok) = InteriorRoomFromLayout(r, shapes);
        if (!ok)
        {
            return (null, false);
        }
        var (sarcophagus, sok) = inRoom!.Piece(shapes.Furniture.Sarcophagus);
        if (!sok)
        {
            return (null, false);
        }
        var (interior, pok) = PlanRoomInterior(inRoom, sarcophagus);
        if (!pok)
        {
            return (null, false);
        }
        foreach (var p in interior.Pieces)
        {
            if (RectCells(p.Rect).Any(taken.Contains))
            {
                continue;
            }
            return (p, true);
        }
        return (null, false);
    }

    /// <summary>
    /// Picks the next tomb step from the plan, the waste census and the colony's built buildings. sarcophagus is
    /// false when none can be had (unresearched, no stuff), which leaves a grave in the planned graveyard; with
    /// no free grave slot there is no step, the body waits and the graveyard is asked for again
    /// (GraveyardsWanted).
    /// </summary>
    public static TombStep NextTombStep(
        LayoutPlan plan,
        IReadOnlyList<WasteItem> waste,
        IReadOnlyList<CurrentBuilding> built,
        PieceShapes shapes,
        bool sarcophagus,
        StrangerTomb strangers
    )
    {
        var dispose = StrangerDisposals(plan, waste, built, shapes.Furniture.Sarcophagus, strangers);
        if (dispose.Count > 0)
        {
            return new TombStep { Kind = TombStepKind.Dispose, Disposal = dispose };
        }
        var allowed = sarcophagus ? strangers.Allowed() : 0;
        var (step, taken) = TombCensus(waste, built, shapes.Furniture.Sarcophagus, allowed);
        if (step.Dead == 0 || step.Empty >= step.Dead)
        {
            return new TombStep();
        }
        if (!sarcophagus)
        {
            foreach (var r in plan.RoomsOf(PlannedRole.Graveyard))
            {
                var (piece, ok) = GraveSlot(r, taken);
                if (ok)
                {
                    return step with { Room = r, Kind = TombStepKind.Reconcile, Template = [piece!.Wanted()] };
                }
            }
            return new TombStep();
        }
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Tomb)
            {
                continue;
            }
            var (piece, ok) = TombSlot(r, shapes, taken);
            if (!ok)
            {
                continue;
            }
            return step with { Room = r, Kind = TombStepKind.Reconcile, Template = [piece!.Wanted()] };
        }
        return step with { Kind = TombStepKind.Full };
    }

    /// <summary>The plan's tomb rooms that a sarcophagus can stand in.</summary>
    public static int TombRooms(this LayoutPlan p)
    {
        var n = 0;
        foreach (var r in p.Rooms)
        {
            if (r.Role == PlannedRole.Tomb)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>The review's tomb deficit: known true while a tomb step is due, unknown while a fact the step reads is.</summary>
    public static Fact<bool> TombOwed(
        PieceShapes shapes,
        Fact<bool> available,
        Fact<LayoutPlan> plan,
        Fact<RoomObservation> rooms,
        Fact<EquatableArray<WasteItem>> waste,
        Fact<CurrentConstruction> built,
        StrangerTomb strangers
    )
    {
        var ak = available.TryGetValue(out var a);
        var pk = plan.TryGetValue(out var p);
        var rk = rooms.TryGetValue(out _);
        var wk = waste.TryGetValue(out var w);
        var bk = built.TryGetValue(out var b);
        if (!wk || !bk || !b!.Colony)
        {
            return Unknown<bool>();
        }
        if (!pk)
        {
            return Unknown<bool>();
        }
        if (ak && !a)
        {
            return Known(NextTombStep(p!, w!, b.Buildings, shapes, false, strangers).Kind != TombStepKind.None);
        }
        if (!ak || !rk)
        {
            return Unknown<bool>();
        }
        return Known(NextTombStep(p!, w!, b.Buildings, shapes, true, strangers).Kind != TombStepKind.None);
    }
}
