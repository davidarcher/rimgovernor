namespace RimGovernor.Brain.Policy;

/// <summary>One standing bank: its capacity as the game reports it and how many packs it already holds.</summary>
public sealed record GeneBankSlot
{
    public required int Capacity { get; init; }

    public required int Held { get; init; }
}

/// <summary>
/// The gene-building rows the need reads: the standing banks and, per pack, whether it lies loose on the map (a
/// pack held by a bank is not loose).
/// </summary>
public sealed record GeneBankFacts
{
    public required IReadOnlyList<GeneBankSlot> Banks { get; init; }

    public required IReadOnlyList<bool> Packs { get; init; }
}

public static class GeneBankConcernRules
{
    // The biotech/gene-bank Need adds capacity for loose genepacks, which deteriorate outside powered banks.
    // Capacity comes from native bank facts; the power/basic Need owns power supply. Banks are identified by
    // CompProperties_GenepackContainer.

    /// <summary>
    /// RoundsFacts.GeneBankOwed's value: whether more packs lie loose than the banks'
    /// free slots hold. Unknown while the section is unread.
    /// </summary>
    public static Fact<bool> GeneBankNeed(Fact<GeneBankFacts> section)
    {
        if (!section.TryGetValue(out var f))
        {
            return Unknown<bool>();
        }
        var free = f.Banks.Sum(b => Math.Max(b.Capacity - b.Held, 0));
        var loose = f.Packs.Count(isLoose => isLoose);
        return Known(loose > free);
    }
}
