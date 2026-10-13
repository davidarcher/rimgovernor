namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The two shapes an acceptance pass takes (Go <c>cmd/acceptance/tier.go</c>):
/// <list type="bullet">
/// <item><description>smoke: runner-proving bridge-only cases over a kept debug game and one short serve-driven case
/// (the Go build embedded suites/smoke.json; the rows are listed here);</description></item>
/// <item><description>nightly: the end-to-end cases; the scheduled loop against main on CI, a signal rather than a gate.</description></item>
/// </list>
/// Every other case runs on demand by name (<c>acceptance run</c>, or the remote workflow's cases tier).
/// <c>acceptance list -tier &lt;name&gt;</c> prints a tier; <c>acceptance suite -tier &lt;name&gt;</c> runs it and records
/// the tier in the suite report.
/// </summary>
public static class Tiers
{
    /// <summary>The tier names in the order the usage lists them.</summary>
    public static readonly IReadOnlyList<string> TierNames = ["nightly", "smoke"];

    /// <summary>
    /// The committed smoke set: bridge-only cases over a kept debug game plus one short serve-driven case (light/dark, so
    /// the build carries LightingFixture), a few minutes on two workers. Every row runs on any fixture build. Extend it
    /// with a case that proves a runner path the others miss, not per area.
    /// </summary>
    private static readonly string[] SmokeNames = ["smoke/identity", "light/dark"];

    /// <summary>The nightly tier: whole-colony proofs, a signal rather than a gate.</summary>
    private static readonly HashSet<string> EndToEnd = new(StringComparer.Ordinal)
    {
        "combatlab/native-hold",
        "campaign/foothold", "campaign/recovery", "clearance/shrine-breach",
        "defense/perimeter", "food/reserve", "food/starving-tribal-recovery", "production/ladder",
        "shelter/bunks-first", "startup/labor",
        "shelter/retirement", "shelter/climate-mild", "shelter/climate-cold", "shelter/climate-hot", "shelter/solo-cold", "solo/sea-ice-stock", "solo/sea-ice-rhadamant",
        "sustained/colony-stable", "sustained/winter", "upkeep/campaign", "sleeping/suites",
        "layout/ring", "layout/rich-soil", "layout/sea-ice-envelope", "layout/storage-successor",
        "mood/headroom", "mood/ledger", "mood/gathering",
        "explain/every-concern",
    };

    /// <summary>Whether <paramref name="tier"/> names a tier.</summary>
    public static bool IsTier(string tier) => TierNames.Contains(tier, StringComparer.Ordinal);

    /// <summary>
    /// Resolves a tier to its registry cases in registry order (Go <c>tierCases</c>). An unknown tier, or a smoke row the
    /// registry does not hold, throws.
    /// </summary>
    public static IReadOnlyList<AcceptanceCase> CasesOf(CaseRegistry registry, string tier)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tier);
        var all = Tiered(registry);
        switch (tier)
        {
            case "nightly":
                return all.Where(c => EndToEnd.Contains(c.Name)).ToList();
            case "smoke":
                foreach (var name in SmokeNames)
                {
                    if (registry.Lookup(name) is null)
                    {
                        throw new AcceptanceException($"suites/smoke.json names unknown case \"{name}\"");
                    }
                }

                return all.Where(c => SmokeNames.Contains(c.Name, StringComparer.Ordinal)).ToList();
            default:
                throw new AcceptanceException($"unknown tier \"{tier}\" (one of {string.Join(", ", TierNames)})");
        }
    }

    /// <summary>
    /// The names no tier runs: fixture generators, which <c>acceptance setup generate</c> runs, and diagnostics, which
    /// gate nothing and stay runnable by hand with <c>acceptance run</c>.
    /// </summary>
    public static bool OffTier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        switch (name)
        {
            case "sustained/colony":
            case "sustained/colony-loud":
            case "sustained/food":
            case "lifecycle/headless-soak":
            case "medical/stable-patient":
            case "review/colony-week":
                return true;
        }

        return name.StartsWith("tools/", StringComparison.Ordinal)
            || name.StartsWith("sustained/matrix-", StringComparison.Ordinal)
            || name.StartsWith("speedmatrix/", StringComparison.Ordinal);
    }

    /// <summary>The registry less the off-tier and matrix cases, in registry order (what <c>acceptance suite -all</c> runs).</summary>
    public static IReadOnlyList<AcceptanceCase> Tiered(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.All().Where(c => !OffTier(c.Name) && !c.Matrix).ToList();
    }

    /// <summary>Whether the case hosts the brain service itself (Go <c>serveDriven</c>).</summary>
    public static bool ServeDriven(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.Serve is not null || c.Service;
    }
}
