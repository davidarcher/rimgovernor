using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>
    /// ManagePollution keeps wastepacks out of the open and the cleanup crew's area on the polluted ground.
    /// Wastepacks that are neither frozen nor inside an atomizer deteriorate into pollution (Biotech
    /// CompDissolution); a forbidden pack cannot be hauled at all. The goal is in deficit while any wastepack
    /// is exposed or forbidden, or the game counts a polluted cell outside the pollution-clear area; it
    /// settles when none is. The methods are designations and area edits, never the outcome (the pack frozen
    /// or atomized, the cell inside the area). It is a Standard whose target is no outstanding work, like
    /// RemoveBlight. The existing toxic-fallout shelter and crop-pollution handling are separate.
    /// </summary>
    public static readonly ConcernId ManagePollution = new("ManagePollution");
}

/// <summary>
/// One spawned wastepack stack with the game's own verdicts (CompDissolution.IsFrozen, InAtomizer, the item's
/// forbidden flag). An unknown verdict is never read as false.
/// </summary>
public sealed record Wastepack
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public Fact<bool> Frozen { get; init; }

    public Fact<bool> InAtomizer { get; init; }

    public Fact<bool> Forbidden { get; init; }

    /// <summary>
    /// Reports a pack known to be neither frozen nor in an atomizer; Known is false when either verdict is
    /// unknown and the pack is not known protected.
    /// </summary>
    public (bool Exposed, bool Known) Exposure()
    {
        var fk = Frozen.TryGetValue(out var frozen);
        var ak = InAtomizer.TryGetValue(out var atomized);
        if ((fk && frozen) || (ak && atomized))
        {
            return (false, true);
        }
        return (true, fk && ak);
    }

    /// <summary>Reports a pack known to be forbidden.</summary>
    public bool Held() => Forbidden.TryGetValue(out var forbidden) && forbidden;
}

/// <summary>
/// The Biotech pollution read the goal works from. UncoveredCells counts the polluted cells outside the
/// pollution-clear area (the game's grid, whole map); unknown when native did not say.
/// </summary>
public sealed record PollutionFacts
{
    public IReadOnlyList<Wastepack> Wastepacks { get; init; } = [];

    public Fact<uint> UncoveredCells { get; init; }
}

/// <summary>
/// What ManagePollution orders this cycle: packs to allow (a forbidden pack cannot be hauled), packs to haul
/// to storage, and the polluted cells to put in the pollution-clear area.
/// </summary>
public sealed record PollutionWork
{
    public IReadOnlyList<Wastepack> Allow { get; init; } = [];

    public IReadOnlyList<Wastepack> Haul { get; init; } = [];

    public IReadOnlyList<Cell> Area { get; init; } = [];

    public bool Empty => Allow.Count == 0 && Haul.Count == 0 && Area.Count == 0;
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The binary ManagePollution signal. Any known exposed or forbidden pack, or polluted cell outside the
    /// area, is a deficit; otherwise the answer is no deficit only when every verdict is known.
    /// </summary>
    public static Fact<bool> PollutionDeficit(Fact<PollutionFacts> facts)
    {
        if (!facts.TryGetValue(out var f))
        {
            return Unknown<bool>();
        }
        var complete = true;
        foreach (var w in f.Wastepacks)
        {
            if (w.Held())
            {
                return Known(true);
            }
            var (exposed, ok) = w.Exposure();
            if (exposed && ok)
            {
                return Known(true);
            }
            if (!ok || !w.Forbidden.TryGetValue(out _))
            {
                complete = false;
            }
        }
        var uncoveredKnown = f.UncoveredCells.TryGetValue(out var uncovered);
        if (uncoveredKnown && uncovered > 0)
        {
            return Known(true);
        }
        if (!uncoveredKnown || !complete)
        {
            return Unknown<bool>();
        }
        return Known(false);
    }

    /// <summary>
    /// Derives the work. A forbidden pack is allowed first and hauled on a later cycle, once it stands
    /// allowed. A pack in claimed (an earlier haul of it ended unsuccessful, so no stockpile took it) is not
    /// re-ordered; packs are ordered by id. polluted is the observed window's polluted cells
    /// (PollutedWindowCells); the area edit is offered only while the game counts polluted cells outside the
    /// area, and setting a cell already in the area changes nothing.
    /// </summary>
    public static PollutionWork SelectPollutionWork(PollutionFacts facts, IReadOnlyDictionary<string, bool>? claimed, IReadOnlyList<Cell> polluted)
    {
        var allow = new List<Wastepack>();
        var haul = new List<Wastepack>();
        foreach (var w in facts.Wastepacks)
        {
            if (w.Id.Length == 0)
            {
                continue;
            }
            if (w.Held())
            {
                allow.Add(w);
                continue;
            }
            var (exposed, known) = w.Exposure();
            if (exposed && known && !(claimed?.GetValueOrDefault(w.Id) ?? false))
            {
                haul.Add(w);
            }
        }
        var byId = Comparer<Wastepack>.Create((a, b) => string.CompareOrdinal(a.Id, b.Id));
        allow.Sort(byId);
        haul.Sort(byId);
        IReadOnlyList<Cell> area = [];
        if (facts.UncoveredCells.TryGetValue(out var uncovered) && uncovered > 0)
        {
            area = polluted;
        }
        return new PollutionWork { Allow = allow, Haul = haul, Area = area };
    }

    /// <summary>
    /// The polluted cells the planning window shows, sorted. Cells without a polluted verdict (fogged) are not
    /// polluted ground the crew can be sent to.
    /// </summary>
    public static IReadOnlyList<Cell> PollutedWindowCells(IReadOnlyList<SiteCell> cells)
    {
        var result = new List<Cell>();
        foreach (var c in cells)
        {
            if (c.Polluted.TryGetValue(out var polluted) && polluted)
            {
                result.Add(c.Cell);
            }
        }
        result.Sort(LessComparer<Cell>(CellLess));
        return result;
    }
}
