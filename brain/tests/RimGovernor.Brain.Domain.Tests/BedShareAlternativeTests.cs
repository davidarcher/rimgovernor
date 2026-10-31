using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT-001: shared, private and held sleeping provision are layout alternatives the shared ledger compares, not a
/// size the clustering's local price fixes. The clustering's proposal is the baseline; one barracks of the whole pool and
/// every bed its own room are deviations seeded and read in the same batch as the host alternatives. The evaluator stands
/// in for the native read: each trial's sleepers lose <c>loss</c> capability-days in a barracks (its mood priced at the
/// wellbeing weight) and each room housing beds costs <c>shell</c> to build, so the ledger alone decides which wins.
/// </summary>
public sealed class BedShareAlternativeTests
{
    private static StationFacts Prepared(StationFacts f) => LayoutDeriveRules.FirstPlanFacts(f.Survey, f, []);

    /// <summary>Three colonists on a temperate map with free wood: the clustering proposes a bedroom each.</summary>
    private static StationFacts Bedrooms() => Prepared(FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild"));

    /// <summary>Four calm colonists on a treeless map, logs at market: the clustering proposes one barracks (#3749).</summary>
    private static StationFacts Barracks() => Prepared(FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", treeless: true, colonists: 4));

    private static HostedRegroupInputs Inputs(StationFacts prepared)
    {
        var inputs = LayoutStationPlanRules.HostedRegroupInputsOf(prepared);
        return inputs with { Alternatives = [.. inputs.Alternatives.Select(h => h with { Input = h.Input with { Price = new() { Work = new(8000, LayoutRegroupRules.CellWalkDays(60)) } } })] };
    }

    /// <summary>The sleep stations of each room of a trial that houses beds.</summary>
    private static int[] SleepRooms(Regrouping layout) =>
        [.. layout.Clustering.Groups.Select(g => g.Stations.Where(s => s.Purpose == StationPurpose.Sleep).Sum(s => s.Count)).Where(n => n > 0)];

    private static ForecastConsequenceVector Vector(double wellbeing, double investment)
    {
        var ledger = new ForecastValueResult(new LedgerTerm.Priced(wellbeing + investment), [])
        {
            Hunger = new LedgerTerm.Priced(0), Rest = new LedgerTerm.Priced(0), FoodPoisoning = new LedgerTerm.Priced(0), Thermal = new LedgerTerm.Priced(0),
            MoodBreaks = new LedgerTerm.Priced(0), Wellbeing = new LedgerTerm.Priced(wellbeing), Harm = new LedgerTerm.Priced(0), Investment = investment,
        };
        var value = new ForecastValueExplanation(ledger, new()) { UrgentZero = new(true, 0), BreakRisk = new(true, 0), Death = new(true, 0) };
        return new(EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 5).ToArray()), EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 6).ToArray()),
            new ForecastTerm.Assessed(0d), new ForecastTerm.Assessed(0d), 0, []) { Value = value };
    }

    private static Task<Regrouping> Compare(HostedRegroupInputs inputs, double loss, double shell) =>
        LayoutRegroupRules.RegroupAsync(inputs, (candidates, _) => Task.FromResult<IReadOnlyList<RegroupValuation>>([.. candidates.Select(candidate =>
        {
            var rooms = SleepRooms(candidate.Layout);
            return new RegroupValuation.Evaluated(candidate.Id, Vector(rooms.Where(n => n > 1).Sum() * loss, rooms.Length * shell), []);
        })]), TestContext.Current.CancellationToken);

    private static RegroupDecision Share(Regrouping result, int size) =>
        Assert.Single(result.Decisions, d => d.Kind == LayoutBedShareRules.BedShareMove && d.A == LayoutBedShareRules.FreeBeds && d.B == size.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void TheProposalIsTheBaselineAndTheSharedAndPrivateSizesAreItsDeviations()
    {
        Assert.Equal([4], LayoutBedShareRules.DeviationsOf(1, 0, 4));
        Assert.Equal([1, 2], LayoutBedShareRules.DeviationsOf(4, 2, 4));
        Assert.Equal([1, 4], LayoutBedShareRules.DeviationsOf(3, 9, 4));
        Assert.Empty(LayoutBedShareRules.DeviationsOf(1, 0, 1));

        var inputs = Inputs(Bedrooms());
        var pool = LayoutClusterRules.BedPoolOf(inputs.Alternatives[0].Input.Cluster)!.Value;
        Assert.True(pool > 1);
        Assert.IsType<BedShare.Proposed>(inputs.Alternatives[0].Share);
        var shared = Assert.Single(inputs.Alternatives, a => a.Share is BedShare.Fixed);
        Assert.Equal(new BedShare.Fixed(pool), shared.Share);
        Assert.Equal(new BedShare.Fixed(pool), shared.Input.Cluster.BedMerge!.Share);
        Assert.Equal(LayoutBedShareRules.IdOf(pool), shared.Host.Id);
        Assert.Equal(pool, LayoutClusterRules.BedMergeChoiceOf(shared.Input.Cluster)!.Size);
        Assert.Equal(1, LayoutClusterRules.BedMergeChoiceOf(inputs.Alternatives[0].Input.Cluster)!.Size);
    }

    [Fact]
    public async Task APermanentBarracksWinsWhenItsSleepersLoseLessThanTheBedroomsCost()
    {
        var inputs = Inputs(Bedrooms());
        var pool = LayoutClusterRules.BedPoolOf(inputs.Alternatives[0].Input.Cluster)!.Value;

        // A barracks sleeper loses 0.1 capability-days; a room costs 5 to build: one barracks beats a bedroom each.
        var result = await Compare(inputs, loss: 0.1, shell: 5);

        Assert.Equal([pool], SleepRooms(result));
        Assert.Equal(LayoutRegroupRules.RegroupApplied, Share(result, pool).Verdict);
        Assert.True(Share(result, pool).After < 0);
    }

    [Fact]
    public async Task TheBarracksLosesWhenTheBedroomsGainExceedsTheirCost()
    {
        var inputs = Inputs(Bedrooms());
        var pool = LayoutClusterRules.BedPoolOf(inputs.Alternatives[0].Input.Cluster)!.Value;

        // The same rooms, but a barracks costs each sleeper 10: the bedrooms the clustering proposed are kept.
        var result = await Compare(inputs, loss: 10, shell: 5);

        Assert.All(SleepRooms(result), n => Assert.Equal(1, n));
        Assert.Equal(LayoutRegroupRules.RegroupKept, Share(result, pool).Verdict);
        Assert.True(Share(result, pool).After > 0);
    }

    [Fact]
    public async Task PrivateRoomsReplaceAProposedBarracksWhenTheirGainExceedsTheirCost()
    {
        var inputs = Inputs(Barracks());
        Assert.True(LayoutClusterRules.BedMergeChoiceOf(inputs.Alternatives[0].Input.Cluster)!.Size > 1);

        var result = await Compare(inputs, loss: 10, shell: 5);

        Assert.All(SleepRooms(result), n => Assert.Equal(1, n));
        Assert.Equal(LayoutRegroupRules.RegroupApplied, Share(result, 1).Verdict);
    }
}
