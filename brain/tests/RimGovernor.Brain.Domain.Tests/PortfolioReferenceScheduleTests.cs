using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Existence proof for the four-worker portfolio fixture: a hand-built schedule that relieves all four demanded
/// ingestions, accepted by <see cref="ReferenceScheduleChecker"/>. It shows feasibility, not global optimality, and
/// changes no production behavior or baseline. See docs/developers/testing/portfolio-allocations.md.
/// </summary>
public sealed class PortfolioReferenceScheduleTests
{
    private const string Root = "food/27";
    private static string Supply(int n) => $"{Root}/supply/{n}/rice/4/composition/{28 + 3 * n}";
    private static string Recipe(int n) => $"{Root}/recipe/{n}/composition/{29 + 3 * n}";
    private static string Delivery(int n) => $"{Root}/delivery/{n}/composition/{30 + 3 * n}";
    private static string Eat(string pawn, int event_, int meal) =>
        $"{Root}/delivery/{meal}/eat/{pawn}/{event_}/composition/{40 + 4 * meal + (pawn == "diner" ? 0 : 2) + event_}";

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private static ForecastCoupledContext Context() =>
        PolicyFunctions.GenerateCoupledActivities(PortfolioAllocationTests.LargerFacts(), 700)!.Context;

    /// <summary>
    /// Helper cooks all four meals back to back on the one stove at its observed 91-tick rate; porter carries rice
    /// early and each meal just in time; diner and pawn each eat in both hunger windows.
    /// </summary>
    internal static List<ReferenceStep> Reference() =>
    [
        new(Supply(0), "porter", 0, 0, 0, 1),
        new(Supply(1), "porter", 1, 2, 2, 3),
        new(Supply(2), "porter", 3, 4, 4, 5),
        new(Supply(3), "porter", 5, 6, 6, 7),
        new(Recipe(0), "helper", 1, 1, 1, 92),
        new(Recipe(1), "helper", 92, 92, 92, 183),
        new(Recipe(2), "helper", 183, 183, 183, 274),
        new(Recipe(3), "helper", 274, 274, 274, 365),
        new(Delivery(0), "porter", 92, 92, 92, 93),
        new(Delivery(1), "porter", 182, 183, 183, 184),
        new(Delivery(2), "porter", 273, 274, 274, 275),
        new(Delivery(3), "porter", 364, 365, 365, 366),
        new(Eat("diner", 0, 0), "diner", 240, 240, 240, 250),
        new(Eat("pawn", 0, 1), "pawn", 240, 240, 240, 250),
        new(Eat("diner", 1, 2), "diner", 490, 490, 490, 500),
        new(Eat("pawn", 1, 3), "pawn", 490, 490, 490, 500),
    ];

    private static List<ReferenceStep> Replace(List<ReferenceStep> steps, string activity, Func<ReferenceStep, ReferenceStep> change) =>
        [.. steps.Select(s => s.Activity == activity ? change(s) : s)];

    private static void AssertRejected(string code, List<ReferenceStep> steps)
    {
        var result = ReferenceScheduleChecker.Check(Context(), steps);
        Assert.Contains(result.Violations, v => v.StartsWith(code + ":", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceRelievesAllFourDemandedIngestions()
    {
        var context = Context();
        Assert.Equal(["diner#0", "diner#1", "pawn#0", "pawn#1"],
            ReferenceScheduleChecker.DemandEvents(context).Select(e => e.Pawn + "#" + e.Index).Order(StringComparer.Ordinal));
        var result = ReferenceScheduleChecker.Check(context, Reference());
        Assert.Empty(result.Violations);
        Assert.Equal(["diner#0", "diner#1", "pawn#0", "pawn#1"], result.Served.Order(StringComparer.Ordinal));
        Assert.Equal(500, result.CompletedAt);
        Assert.Equal([250d, 500d], ReferenceScheduleChecker.DemandEvents(context).Select(e => e.DueTick).Distinct().Order());
    }

    [Fact]
    public void DevelopingCookVariantUsesTheLearningTable()
    {
        // The developing cook works at 100 ticks, completes the recipe, levels up and then works at 80 (100 / 1.25).
        var steps = Reference();
        steps = Replace(steps, Recipe(0), s => s with { Worker = "pawn", Depart = 1, Arrive = 1, Start = 1, End = 101 });
        steps = Replace(steps, Recipe(1), s => s with { Worker = "pawn", Depart = 101, Arrive = 101, Start = 101, End = 181 });
        steps = Replace(steps, Recipe(2), s => s with { Depart = 181, Arrive = 181, Start = 181, End = 272 });
        steps = Replace(steps, Recipe(3), s => s with { Depart = 272, Arrive = 272, Start = 272, End = 363 });
        steps = Replace(steps, Delivery(0), s => s with { Depart = 101, Arrive = 101, Start = 101, End = 102 });
        steps = Replace(steps, Delivery(1), s => s with { Depart = 180, Arrive = 181, Start = 181, End = 182 });
        steps = Replace(steps, Delivery(2), s => s with { Depart = 271, Arrive = 272, Start = 272, End = 273 });
        steps = Replace(steps, Delivery(3), s => s with { Depart = 362, Arrive = 363, Start = 363, End = 364 });
        // Having cooked at the stove, the pawn walks one tick to the dining cell.
        steps = Replace(steps, Eat("pawn", 0, 1), s => s with { Depart = 239 });
        var result = ReferenceScheduleChecker.Check(Context(), steps);
        Assert.Empty(result.Violations);
        Assert.Equal(4, result.Served.Count);
    }

    [Fact]
    public void CheckerRejectsWorkerOverlap() =>
        // Porter starts the fourth supply trip while still finishing the third.
        AssertRejected("worker_overlap", Replace(Reference(), Supply(3), s => s with { Depart = 4, Arrive = 5, Start = 5, End = 6 }));

    [Fact]
    public void CheckerRejectsOverbookedStove() =>
        // Diner cooks the second meal on the one stove while the helper is still using it.
        AssertRejected("stove_overlap", Replace(Reference(), Recipe(1), s => s with { Worker = "diner", Depart = 50, Arrive = 50, Start = 50, End = 155 }));

    [Fact]
    public void CheckerRejectsMealEatenBeforeItIsCookedAndDelivered()
    {
        // The pawn's first meal is carried only after it is already eaten.
        var steps = Replace(Reference(), Delivery(1), s => s with { Depart = 299, Arrive = 300, Start = 300, End = 301 });
        AssertRejected("prerequisite_unfinished", steps);
        AssertRejected("conservation", steps);
    }

    [Fact]
    public void CheckerRejectsConservationViolation()
    {
        // Two ingestions draw the same single cooked meal; the bench and every dependency are otherwise respected.
        var steps = Replace(Reference(), Eat("pawn", 1, 3), s => s with { Activity = Eat("pawn", 1, 2) });
        steps = [.. steps.Where(s => s.Activity != Recipe(3) && s.Activity != Delivery(3) && s.Activity != Supply(3))];
        AssertRejected("conservation", steps);
    }

    [Fact]
    public void CheckerRejectsWrongRatesAndMissedWindows()
    {
        AssertRejected("work_rate", Replace(Reference(), Recipe(0), s => s with { End = 81 }));
        AssertRejected("learning_rate", Replace(Reference(), Recipe(0), s => s with { Worker = "pawn", End = 92 }));
        AssertRejected("ingestion_late", Replace(Reference(), Eat("diner", 0, 0), s => s with { Depart = 245, Arrive = 245, Start = 245, End = 255 }));
    }

    [Fact]
    public void CheckerCountsOnlyIngestionsThatActuallyHappen()
    {
        var steps = Reference().Where(s => s.Activity != Eat("pawn", 1, 3)).ToList();
        var result = ReferenceScheduleChecker.Check(Context(), steps);
        Assert.Empty(result.Violations);
        Assert.Equal(["diner#0", "diner#1", "pawn#0"], result.Served.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReferenceBeatsEveryRecordedIncumbent()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RimGovernor.Brain.Domain.Tests.four-workers.quality.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var served = ReferenceScheduleChecker.Check(Context(), Reference()).Served.Count;
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var incumbent = row.GetProperty("Quality").GetProperty("Physical").EnumerateArray()
                .Count(p => System.Text.RegularExpressions.Regex.IsMatch(p.GetProperty("Activity").GetString()!, @"/eat/\w+/\d+/composition/\d+$")
                    && !p.GetProperty("Activity").GetString()!.Contains("/after/", StringComparison.Ordinal)); // "after" tasks are deliveries gated on an ingestion
            // One at the smallest budgets; two (one consumer's both events) once #3197's extension runs. Never all four.
            Assert.InRange(incumbent, 1, 2);
            Assert.True(served > incumbent);
        }
    }

    [Fact]
    public void CheckerSourceDoesNotUseProductionScheduling()
    {
        var source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ThisFile())!, "ReferenceScheduleChecker.cs"));
        Assert.DoesNotContain("Policy" + "Functions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CompareActivity", source, StringComparison.Ordinal);
    }
}
