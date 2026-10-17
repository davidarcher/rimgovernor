namespace RimGovernor.Brain.Acceptance;

/// <summary>The tier a case declares (charter rule 8): which pass runs it.</summary>
public enum CaseTier
{
    /// <summary>Run by name (<c>acceptance run</c>, the remote workflow's cases tier) and by <c>acceptance suite -all</c>.</summary>
    OnDemand,

    /// <summary>
    /// Runner-proving cases over a kept debug game plus one short serve-driven case, a few minutes on two workers. Every
    /// smoke case runs on any fixture build. Extend it with a case that proves a runner path the others miss, not per area.
    /// </summary>
    Smoke,

    /// <summary>Whole-colony proofs, run against main every night: a signal rather than a gate.</summary>
    Nightly,

    /// <summary>
    /// Never run by a tier or by <c>acceptance suite -all</c>: diagnostics, which gate nothing and stay runnable by hand
    /// with <c>acceptance run</c>. Generators are their own subcommands (<c>acceptance colony</c>, <c>acceptance baseline</c>).
    /// </summary>
    Off,
}

/// <summary>
/// The two named tiers an acceptance pass takes (<see cref="CaseTier.Smoke"/> and <see cref="CaseTier.Nightly"/>), from
/// the tier each case declares. <c>acceptance list -tier &lt;name&gt;</c> prints a tier; <c>acceptance suite -tier
/// &lt;name&gt;</c> runs it and records the tier in the suite report.
/// </summary>
public static class Tiers
{
    /// <summary>The tier names in the order the usage lists them.</summary>
    public static readonly IReadOnlyList<string> TierNames = ["nightly", "smoke"];

    /// <summary>Whether <paramref name="tier"/> names a tier.</summary>
    public static bool IsTier(string tier) => TierNames.Contains(tier, StringComparer.Ordinal);

    /// <summary>Resolves a tier name to the registry cases that declare it, in registry order. An unknown tier throws.</summary>
    public static IReadOnlyList<AcceptanceCase> CasesOf(CaseRegistry registry, string tier)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tier);
        var declared = tier switch
        {
            "nightly" => CaseTier.Nightly,
            "smoke" => CaseTier.Smoke,
            _ => throw new AcceptanceException($"unknown tier \"{tier}\" (one of {string.Join(", ", TierNames)})"),
        };
        return Tiered(registry).Where(c => c.Tier == declared).ToList();
    }

    /// <summary>The registry less the off-tier and matrix cases, in registry order (what <c>acceptance suite -all</c> runs).</summary>
    public static IReadOnlyList<AcceptanceCase> Tiered(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.All().Where(c => c.Tier != CaseTier.Off && !c.Matrix).ToList();
    }

    /// <summary>Whether the case hosts the brain service itself (Go <c>serveDriven</c>).</summary>
    public static bool ServeDriven(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.Serve is not null || c.Service;
    }
}
