namespace RimGovernor.Brain.Policy;

// Staging the tomb. Once ComplexFurniture makes the sarcophagus available, a colonist corpse with no empty grave
// waiting raises a planned tomb's shell and then reconciles its room (ReconcileRoom) to the next free template
// sarcophagus: the ring, the floor and the piece, installed from packed stock first. A filled sarcophagus is never
// reused: when every planned tomb is full the plan grows another. Where no sarcophagus can be had (research or
// stuff) a plain Grave takes the body instead, one grave per unburied dead owed, placed on open ground near the
// base (GraveSites), not in a room. Once a sarcophagus can be had new burials go to the tomb; standing graves stay.
// Vanilla haulers inter colonist corpses in any empty grave on their own; nothing here hauls.
//
// No sarcophagus burial gives a mood memory: vanilla's KnowBuriedInSarcophagus fires only for a body placed by
// Building_Sarcophagus.TryAcceptThing, which no haul path calls (burial/stranger-sarcophagus, #3242). Strangers
// keep the morgue path; the tomb and the plain grave count colonists only.

/// <summary>The next tomb step.</summary>
public enum TombStepKind
{
    /// <summary>No unburied colonist corpse, enough empty graves, no planned tomb, or a fact is unknown.</summary>
    [WireName("")]
    None,

    /// <summary>
    /// Reconcile Room, the planned tomb, to Template, its next free sarcophagus (ReconcileRoom): the ring, the
    /// floor and the piece, whatever the diff still owes. There is no shell or place step.
    /// </summary>
    [WireName("reconcile")]
    Reconcile,

    /// <summary>No sarcophagus can be had and the dead outnumber the empty graves: place one plain grave near the base (GraveSites).</summary>
    [WireName("grave")]
    Grave,

    /// <summary>Every planned tomb's slots are taken; the plan owes another tomb room.</summary>
    [WireName("full")]
    Full,
}

/// <summary>One bounded step towards a grave for every dead colonist.</summary>
public sealed record TombStep
{
    public TombStepKind Kind { get; init; }

    public PlannedRoom Room { get; init; } = new();

    /// <summary>A Reconcile's wanted furniture: the next free sarcophagus in its slot.</summary>
    public IReadOnlyList<WantedPiece> Template { get; init; } = [];

    /// <summary>The unburied colonist corpses.</summary>
    public int Dead { get; init; }

    /// <summary>The empty graves and sarcophagi.</summary>
    public int Empty { get; init; }

    /// <summary>The plain graves standing.</summary>
    public int Graves { get; init; }
}

public static partial class TombRules
{
    /// <summary>
    /// The memory vanilla gives every colonist when a sarcophagus takes its first body through TryAcceptThing; no haul
    /// path calls that, so a hauled burial never gives it (burial/stranger-sarcophagus reads it).
    /// </summary>
    public const string KnowBuriedInSarcophagusThought = "KnowBuriedInSarcophagus";

    /// <summary>Core's plain grave: 1x2, no stuff, no research.</summary>
    public const string GraveDefinition = "Grave";

    /// <summary>
    /// Counts the unburied colonist dead, the empty graves and the plain graves, and returns the cells graves stand
    /// on. A buried body, colonist or stranger, fills its grave.
    /// </summary>
    private static (TombStep Step, HashSet<Cell> Taken) TombCensus(IReadOnlyList<WasteItem> waste, IReadOnlyList<CurrentBuilding> built, string sarcophagus)
    {
        var dead = 0;
        var empty = 0;
        var graves = 0;
        var filled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in waste)
        {
            if (item.CorpseOf != CorpseOf.Colonist && item.CorpseOf != CorpseOf.Stranger)
            {
                continue;
            }
            if (item.Buried)
            {
                filled.Add(item.Grave);
            }
            else if (item.CorpseOf == CorpseOf.Colonist)
            {
                dead++;
            }
        }
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
            if (PlannedRules.RectCells(p.Rect).Any(taken.Contains))
            {
                continue;
            }
            return (p, true);
        }
        return (null, false);
    }

    /// <summary>
    /// Picks the next tomb step from the plan, the waste census and the colony's built buildings. sarcophagus is
    /// false when none can be had (unresearched, no stuff), which owes a plain grave near the base (Grave): the
    /// unburied dead owed one are the demand, never a room's fill.
    /// </summary>
    public static TombStep NextTombStep(
        LayoutPlan plan,
        IReadOnlyList<WasteItem> waste,
        IReadOnlyList<CurrentBuilding> built,
        PieceShapes shapes,
        bool sarcophagus
    )
    {
        var (step, taken) = TombCensus(waste, built, shapes.Furniture.Sarcophagus);
        if (step.Dead == 0 || step.Empty >= step.Dead)
        {
            return new TombStep();
        }
        if (!sarcophagus)
        {
            return step with { Kind = TombStepKind.Grave };
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
        Fact<CurrentConstruction> built
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
            return Known(NextTombStep(p!, w!, b.Buildings, shapes, false).Kind != TombStepKind.None);
        }
        if (!ak || !rk)
        {
            return Unknown<bool>();
        }
        return Known(NextTombStep(p!, w!, b.Buildings, shapes, true).Kind != TombStepKind.None);
    }
}
