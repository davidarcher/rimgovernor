using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

// The biotech/pollution Need (BiotechFamily) keeps wastepacks out of the open and the cleanup crew's area on the
// polluted ground. Wastepacks that are neither frozen nor inside an atomizer deteriorate into pollution (Biotech
// CompDissolution); a forbidden pack cannot be hauled at all. The Need is unmet while any wastepack is exposed or
// forbidden, or the game counts a polluted cell outside the pollution-clear area; it is met when none is. The
// methods are designations and area edits, never the outcome (the pack frozen or atomized, the cell inside the
// area). The existing toxic-fallout shelter and crop-pollution handling are separate.

/// <summary>
/// One spawned wastepack stack with the game's own verdicts (CompDissolution.IsFrozen, InAtomizer, the item's
/// forbidden flag).
/// </summary>
public sealed record Wastepack
{
    public required string Id { get; init; }

    public required string Definition { get; init; }

    public required Cell Cell { get; init; }

    public required bool Frozen { get; init; }

    public required bool InAtomizer { get; init; }

    public required bool Forbidden { get; init; }

    /// <summary>A pack neither frozen nor in an atomizer: it deteriorates into pollution.</summary>
    public bool Exposed => !Frozen && !InAtomizer;
}

/// <summary>
/// The Biotech pollution read the goal works from. UncoveredCells counts the polluted cells outside the
/// pollution-clear area (the game's grid, whole map).
/// </summary>
public sealed record PollutionFacts
{
    public required IReadOnlyList<Wastepack> Wastepacks { get; init; }

    public required uint UncoveredCells { get; init; }
}

/// <summary>
/// What the pollution Need orders this cycle: packs to allow (a forbidden pack cannot be hauled), packs to haul
/// to storage, and the polluted cells to put in the pollution-clear area.
/// </summary>
public sealed record PollutionWork
{
    public IReadOnlyList<Wastepack> Allow { get; init; } = [];

    public IReadOnlyList<Wastepack> Haul { get; init; } = [];

    public IReadOnlyList<Cell> Area { get; init; } = [];

    public bool Empty => Allow.Count == 0 && Haul.Count == 0 && Area.Count == 0;
}

public static partial class PollutionRules
{
    /// <summary>
    /// The binary pollution signal: any exposed or forbidden pack, or polluted cell outside the area, is a
    /// deficit. Unknown while the section is unread.
    /// </summary>
    public static Fact<bool> PollutionDeficit(Fact<PollutionFacts> facts) =>
        facts.TryGetValue(out var f)
            ? Known(f.UncoveredCells > 0 || f.Wastepacks.Any(w => w.Forbidden || w.Exposed))
            : Unknown<bool>();

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
            if (w.Forbidden)
            {
                // A pack's allow names it by id, definition and cell (SupplyAllow); a pack without them cannot be allowed.
                if (IdText.IsValidText(w.Id) && IdText.IsValidText(w.Definition) && w.Cell.X >= 0 && w.Cell.Z >= 0)
                {
                    allow.Add(w);
                }
                continue;
            }
            if (w.Exposed && !(claimed?.GetValueOrDefault(w.Id) ?? false))
            {
                haul.Add(w);
            }
        }
        var byId = Comparer<Wastepack>.Create((a, b) => string.CompareOrdinal(a.Id, b.Id));
        allow.Sort(byId);
        haul.Sort(byId);
        IReadOnlyList<Cell> area = [];
        if (facts.UncoveredCells > 0)
        {
            area = polluted;
        }
        return new PollutionWork { Allow = allow, Haul = haul, Area = area };
    }

    /// <summary>
    /// The review's pollution cohort: the allow of every forbidden pack SelectPollutionWork would order, the only
    /// supply allows method admission takes from the biotech/pollution Need. Empty while the read is unknown.
    /// </summary>
    public static IReadOnlyList<StartingSupply> PollutionAllowSupplies(Fact<PollutionFacts> facts) =>
        facts.TryGetValue(out var f)
            ? [.. SelectPollutionWork(f, null, []).Allow.Select(w => new StartingSupply { Thing = w.Id, Definition = w.Definition, Cell = w.Cell })]
            : [];

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
        result.Sort(PlannedRules.LessComparer<Cell>(CombatManhunterRules.CellLess));
        return result;
    }
}
