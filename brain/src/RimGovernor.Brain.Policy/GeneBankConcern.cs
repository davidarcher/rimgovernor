namespace RimGovernor.Brain.Policy;

public static class GeneBankConcernRules
{
    // The biotech/gene-bank Need adds capacity for loose genepacks, which deteriorate outside powered banks.
    // Capacity comes from native bank facts; the power/basic Need owns power supply. Banks are identified by
    // CompProperties_GenepackContainer.

    /// <summary>
    /// Whether more packs lie loose (held by no bank) than the standing banks' free slots hold, over the Biotech
    /// colony section's banks and genepacks.
    /// </summary>
    public static bool GeneBankOwed(RimGovernor.Brain.GameFacts.Observations.BiotechColonyFacts biotech)
    {
        var free = biotech.GeneBanks.Sum(b => Math.Max(b.Capacity - b.PackIds.Count, 0));
        var loose = biotech.Genepacks.Count(p => p.BankId is null);
        return loose > free;
    }
}
