namespace RimGovernor.Brain.Policy;

/// <summary>One standing bank: its capacity as the game reports it and how many packs it already holds. (Go <c>GeneBankSlot</c>.)</summary>
public sealed record GeneBankSlot
{
    public Fact<int> Capacity { get; init; }

    public int Held { get; init; }
}

/// <summary>
/// The gene-building rows the need reads: the standing banks and, per pack, whether it lies loose on the map (a
/// pack held by a bank is not loose; unknown when the pack reported neither). (Go <c>GeneBankFacts</c>.)
/// </summary>
public sealed record GeneBankFacts
{
    public IReadOnlyList<GeneBankSlot> Banks { get; init; } = [];

    public IReadOnlyList<Fact<bool>> Packs { get; init; } = [];
}

public static partial class PolicyFunctions
{
    // MaintainGeneBank adds capacity for loose genepacks, which deteriorate outside powered banks. Capacity comes
    // from native bank facts; EnsureBasicPower owns power supply. Banks are identified by
    // CompProperties_GenepackContainer.
    public static readonly ConcernId MaintainGeneBank = new("MaintainGeneBank");

    /// <summary>
    /// Go <c>GeneBankNeed</c>: RoundsFacts.GeneBankOwed's value: whether more packs lie loose than the banks'
    /// free slots hold. Unknown while the section is unread, a bank's capacity is unknown or a pack's
    /// whereabouts are.
    /// </summary>
    public static Fact<bool> GeneBankNeed(Fact<GeneBankFacts> section)
    {
        if (!section.TryGetValue(out var f))
        {
            return Unknown<bool>();
        }
        var free = 0;
        var loose = 0;
        foreach (var b in f.Banks)
        {
            if (!b.Capacity.TryGetValue(out var capacity))
            {
                return Unknown<bool>();
            }
            free += Math.Max(capacity - b.Held, 0);
        }
        foreach (var p in f.Packs)
        {
            if (!p.TryGetValue(out var isLoose))
            {
                return Unknown<bool>();
            }
            if (isLoose)
            {
                loose++;
            }
        }
        return Known(loose > free);
    }
}
