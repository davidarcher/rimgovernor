using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class BabyPlayCreditTests
{
    private const double Fall = .00025 / 150;
    private const double Gain = .0002;

    private static BabyRead Baby(bool standing = false) => new()
    {
        CribMood = new(0, 0, 0), Pawn = "baby", Fits = [], InNursery = standing, Hearers = [],
        SleepWithoutNursery = new("", false, true, 3e-5, 0, 21),
        Play = new() { OngoingCarer = "", RoomFactor = 1, Carers = [new("pawn", null, null, 1, 1, 1, 1)] },
    };

    private static ForecastCoupledContext Context(BabyRead baby) => ForecastValueTests.Context(ForecastValueTests.Pawn()) with
    {
        Needs = NeedsFixtures.Empty with
        {
            Pawns = [ForecastValueTests.Pawn(), NeedsFixtures.Pawn("baby")],
            Babies = NeedsFixtures.NoBabies with { Babies = [baby] },
        },
        PlannedBeds = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>> { [1] = [new("Crib", "WoodLog", false, 1, .75)] },
        NurserySites = [new("toy", 1, OpKind.Build, "ToyBox", "WoodLog", Rotation.North, [new(1, 1)])],
    };

    [Fact]
    public void UniformRandomJobChoicesAverageTheirDurationsRatherThanTheirRates()
    {
        var times = new[] { .84 / (Gain - Fall), .84 / (Gain - Fall), .84 / (Gain * 1.25 - Fall) };
        var expected = times.Average() / (times.Average() + .84 / Fall);
        Assert.Equal(expected, ForecastBabyPlay.CareShare(Fall, [Gain, Gain, Gain * 1.25]), 14);
        Assert.True(expected > Fall / ((Gain + Gain + Gain * 1.25) / 3));
    }

    [Fact]
    public void APlannedToyCreditsTheCaretakerEvenBeforeAnyToyExists()
    {
        var baby = Baby();
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var credit = ForecastBabyPlay.Of(baby, 0, 1, Context(baby), findings)!.Value;
        Assert.Equal("", credit.Carer);
        Assert.Equal(Fall / Gain - ForecastBabyPlay.CareShare(Fall, [Gain, Gain, Gain * 1.25]), credit.Lower, 14);
        Assert.Equal(credit.Lower, credit.Upper);
        Assert.DoesNotContain("nursery_play_exposure_bounded", findings);
    }

    [Fact]
    public void StandingDecorationAndToySavingsKeepTheWalkingAndDropExposureBounds()
    {
        var baby = Baby(true);
        baby = baby with { Play = baby.Play! with
        {
            RoomFactor = 1.1, Carers = [new("pawn", new(1.25, 1, 1.1), null, 1, 1.1, 1, 1)],
        } };
        var context = Context(baby) with { NurserySites = [] };
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var credit = ForecastBabyPlay.Of(baby, 0, 1, context, findings)!.Value;
        Assert.Equal(Fall / Gain - ForecastBabyPlay.CareShare(Fall, [Gain * 1.1, Gain, Gain * 1.25]), credit.Lower, 14);
        Assert.Equal(Fall / Gain - ForecastBabyPlay.CareShare(Fall, [Gain * 1.1, Gain * 1.1, Gain * 1.25 * 1.1]), credit.Upper, 14);
        Assert.True(credit.Lower > 0);
        Assert.True(credit.Upper > credit.Lower);
        Assert.Contains("nursery_play_exposure_bounded", findings);
        Assert.Contains(findings, f => f.StartsWith("nursery_play_savings:baby:", StringComparison.Ordinal));
        // Decoration alone saves verified static-play time even when neither side has a toy and
        // the walk's conservative exposure enclosure retains the absence multiplier.
        var decorationOnly = baby with { Play = baby.Play! with { Carers = [new("pawn", null, null, 1, 1.1, 1, 1)] } };
        var decor = ForecastBabyPlay.Of(decorationOnly, 0, 1, Context(decorationOnly) with { NurserySites = [] }, new SortedSet<string>())!.Value;
        Assert.Equal(Fall / Gain - ForecastBabyPlay.CareShare(Fall, [Gain * 1.1, Gain]), decor.Lower, 14);
        Assert.True(decor.Lower > 0);
    }

    [Fact]
    public void PlannedDecorationUsesTheCribsDistanceAndDoesNotStackDecorations()
    {
        var baby = Baby();
        var context = Context(baby);
        context = context with { NurserySites = [.. context.NurserySites,
            new("crib", 1, OpKind.Build, "Crib", "WoodLog", Rotation.North, [new(0, 0)]),
            new("decor", 1, OpKind.Build, "BabyDecoration", "", Rotation.North, [new(9, 0), new(8, 0)])] };
        var near = ForecastBabyPlay.Of(baby, 0, 1, context, new SortedSet<string>())!.Value;
        var farContext = context with { NurserySites = [.. context.NurserySites.Where(s => s.Id != "decor"),
            new("decor", 1, OpKind.Build, "BabyDecoration", "", Rotation.North, [new(10, 0)])] };
        var far = ForecastBabyPlay.Of(baby, 0, 1, farContext, new SortedSet<string>())!.Value;
        Assert.Equal(Fall / Gain - ForecastBabyPlay.CareShare(Fall, [Gain * 1.1, Gain, Gain * 1.25]), near.Lower, 14);
        Assert.True(near.Lower > far.Lower);
    }

    [Fact]
    public void ExistingPlayFurnitureIsAnAlternativeAndMayMakeANurseryWorse()
    {
        var baby = Baby();
        baby = baby with { Play = baby.Play! with { Carers = [new("pawn", new(2, 1, 1), new(2, 1, 1), 1, 1, 1, 1)] } };
        var credit = ForecastBabyPlay.Of(baby, 0, 1, Context(baby), new SortedSet<string>())!.Value;
        Assert.True(credit.Lower < 0);
        Assert.Equal(credit.Lower, credit.Upper);
    }

    [Fact]
    public void UnassignedCareBoundsEveryEligibleCarerWhileAnObservedJobNamesItsActualCarer()
    {
        var baby = Baby();
        baby = baby with { Play = baby.Play! with { Carers = [.. baby.Play!.Carers, new("other", new(2, 1, 1), new(2, 1, 1), 1, 1, 1, 1)] } };
        var context = Context(baby);
        context = context with { Needs = context.Needs with { Pawns = [.. context.Needs.Pawns, NeedsFixtures.Pawn("other")] } };
        var shared = ForecastBabyPlay.Of(baby, 0, 1, context, new SortedSet<string>())!.Value;
        Assert.Empty(shared.Carer);
        Assert.True(shared.Lower < 0);
        Assert.True(shared.Upper > 0);
        var active = ForecastBabyPlay.Of(baby with { Play = baby.Play! with { OngoingCarer = "pawn" } }, 0, 1, context, new SortedSet<string>())!.Value;
        Assert.Equal("pawn", active.Carer);
        Assert.True(active.Lower > 0);
        Assert.Equal(active.Lower, active.Upper);
        // A different carer may do the work in each scenario. Same-carer differences would all be
        // nonnegative here, while the allocation-free guaranteed bound must include a loss.
        var changing = baby with { InNursery = true, Play = baby.Play! with
        {
            Carers = [new("pawn", new(1.25, 1, 1), null, 1, 1, 1, 1), new("other", new(2, 1, 1), new(2, 1, 1), 1, 1, 1, 1)],
        } };
        var changingCredit = ForecastBabyPlay.Of(changing, 0, 1, context with { NurserySites = [] }, new SortedSet<string>())!.Value;
        Assert.Equal(ForecastBabyPlay.CareShare(Fall, [Gain, Gain, Gain * 2]) - ForecastBabyPlay.CareShare(Fall, [Gain, Gain, Gain * 1.25]), changingCredit.Lower, 14);
        Assert.True(changingCredit.Lower < 0);
    }

    [Fact]
    public void AbsentNeedsAndCarersAreNamedWhileMissingMechanicsAndStuffStatsThrow()
    {
        var baby = Baby();
        var findings = new SortedSet<string>();
        Assert.Null(ForecastBabyPlay.Of(baby with { Play = null }, 0, 1, Context(baby), findings));
        Assert.Contains("nursery_baby_no_play_need", findings);
        Assert.Null(ForecastBabyPlay.Of(baby with { Play = baby.Play! with { Carers = [] } }, 0, 1, Context(baby), findings));
        Assert.Contains("nursery_no_play_carer", findings);
        var context = Context(baby);
        Assert.Throws<RequiredFactException>(() => ForecastBabyPlay.Of(baby, 0, 1,
            context with { Needs = context.Needs with { Babies = context.Needs.Babies with { Play = null } } }, findings));
        Assert.Throws<RequiredFactException>(() => ForecastBabyPlay.Of(baby, 0, 1,
            context with { NurserySites = [new("toy", 1, OpKind.Build, "ToyBox", "UnreadStuff", Rotation.North, [new(1, 1)])] }, findings));
    }
}
