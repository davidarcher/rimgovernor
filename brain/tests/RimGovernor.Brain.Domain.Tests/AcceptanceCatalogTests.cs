using System.Globalization;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The acceptance catalog in the gate (#3397): the whole registry <see cref="CaseCatalog.Register"/> builds, without a
/// game. Every case is a <see cref="TypedCase"/> that declares a claim, ticks within its start's cap and a tier.
/// </summary>
public sealed class AcceptanceCatalogTests
{
    [Fact]
    public void CatalogHoldsEveryCaseToTheCharter()
    {
        var registry = new CaseRegistry();
        CaseCatalog.Register(registry);

        Assert.Empty(Findings(registry));
    }

    [Fact]
    public void FlagsAnUncheckedTypedCase()
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
        registry.Register(new TypedCase
        {
            Name = "lifecycle/unclaimed",
            Scope = "an owned start that is not its claim",
            Tier = CaseTier.OnDemand,
            Ticks = 0,
            Start = new OwnedStart(),
            Run = (_, _) => Task.CompletedTask,
        }.ToCase());

        var findings = Findings(registry);

        Assert.Contains("case lab/unchecked: Scope must name the one native claim", findings);
        Assert.Contains("case lab/unchecked declares 30001 ticks; its start's cap is 30000", findings);
        Assert.Contains("case lab/unchecked declares no CaseTier (99)", findings);
        Assert.Contains("case lifecycle/unclaimed: a typed case owns its process lifecycle only when the start is its claim (ClaimsStart)", findings);
    }

    private static List<string> Findings(CaseRegistry registry)
    {
        var findings = new List<string>();
        foreach (var typed in registry.All().Select(c => c.Typed))
        {
            if (string.IsNullOrWhiteSpace(typed.Scope))
            {
                findings.Add($"case {typed.Name}: Scope must name the one native claim");
            }

            if (typed.Ticks < 0 || typed.Ticks > typed.Cap)
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"case {typed.Name} declares {typed.Ticks} ticks; its start's cap is {typed.Cap}"));
            }

            if (typed.HorizonProblem() is { } horizonProblem)
            {
                findings.Add($"case {typed.Name}: {horizonProblem}");
            }

            if (typed.StartProblem() is { } startProblem)
            {
                findings.Add($"case {typed.Name}: {startProblem}");
            }

            if (!Enum.IsDefined(typed.Tier))
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"case {typed.Name} declares no CaseTier ({(int)typed.Tier})"));
            }
        }

        return findings;
    }
}
