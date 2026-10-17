using System.Globalization;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The acceptance catalog in the gate (#3397): the whole registry <see cref="CaseCatalog.Register"/> builds, without a
/// game. Every typed case declares a claim, ticks within its start's cap and a tier; every legacy case passes the
/// runner's <see cref="AcceptanceCase.Lint"/>; and the frozen legacy API holds exactly <see cref="LegacyCases"/> cases.
/// </summary>
public sealed class AcceptanceCatalogTests
{
    /// <summary>
    /// The cases still on the frozen legacy <see cref="AcceptanceCase"/> API. Shrink-only: a port or deletion lowers it
    /// in the same commit, and a new legacy case fails (write a <see cref="TypedCase"/>).
    /// </summary>
    private const int LegacyCases = 316;

    [Fact]
    public void CatalogHoldsEveryCaseToTheCharter()
    {
        var registry = new CaseRegistry();
        CaseCatalog.Register(registry);

        Assert.Empty(Findings(registry, LegacyCases));
    }

    [Fact]
    public void FlagsAnUncheckedTypedCaseALintFailureAndALegacyCaseOverTheCount()
    {
        var registry = new CaseRegistry();

        // Registering the runner record skips TypedCase.Validate, so only the catalog check sees the typed case.
        registry.Register(new TypedCase
        {
            Name = "lab/unchecked",
            Scope = " ",
            Ticks = TypedCase.StagedTickCap + 1,
            Tier = (CaseTier)99,
            Run = (_, _) => Task.CompletedTask,
        }.ToCase());
        registry.Register(new AcceptanceCase
        {
            Name = "lab/no-budget",
            Start = new LabStart(),
            Crew = new() { Size = 3 },
            Run = (_, _) => Task.CompletedTask,
        });

        var findings = Findings(registry, legacyBaseline: 0);

        Assert.Contains("case lab/unchecked: Scope must name the one native claim", findings);
        Assert.Contains("case lab/unchecked declares 30001 ticks; its start's cap is 30000", findings);
        Assert.Contains("case lab/unchecked declares no CaseTier (99)", findings);
        Assert.Contains(findings, f => f.StartsWith("case \"lab/no-budget\": Budget is missing", StringComparison.Ordinal));
        Assert.Contains("1 cases on the legacy API, above the frozen 0: write new cases as TypedCase", findings);
        Assert.Contains("0 cases on the legacy API, below the frozen 1: lower LegacyCases to 0", Findings(new CaseRegistry(), legacyBaseline: 1));
    }

    private static List<string> Findings(CaseRegistry registry, int legacyBaseline)
    {
        var findings = new List<string>();
        var legacy = 0;
        foreach (var c in registry.All())
        {
            if (c.Typed is not { } typed)
            {
                legacy++;
                findings.AddRange(c.Lint());
                continue;
            }

            if (string.IsNullOrWhiteSpace(typed.Scope))
            {
                findings.Add($"case {typed.Name}: Scope must name the one native claim");
            }

            if (typed.Ticks < 0 || typed.Ticks > typed.Cap)
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"case {typed.Name} declares {typed.Ticks} ticks; its start's cap is {typed.Cap}"));
            }

            if (!Enum.IsDefined(typed.Tier))
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"case {typed.Name} declares no CaseTier ({(int)typed.Tier})"));
            }
        }

        if (legacy > legacyBaseline)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture, $"{legacy} cases on the legacy API, above the frozen {legacyBaseline}: write new cases as TypedCase"));
        }
        else if (legacy < legacyBaseline)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture, $"{legacy} cases on the legacy API, below the frozen {legacyBaseline}: lower LegacyCases to {legacy}"));
        }

        return findings;
    }
}
