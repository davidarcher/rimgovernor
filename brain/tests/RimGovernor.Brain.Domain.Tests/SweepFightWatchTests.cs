using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Simulation.CombatMetrics;
using Xunit;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class SweepFightWatchTests
{
    private static readonly IReadOnlyDictionary<string, string> Staged = new Dictionary<string, string>
    {
        ["patient"] = Sides.Colonist,
        ["defender"] = Sides.Colonist,
        ["raider"] = Sides.Hostile,
    };

    private static Fx.LabFightRead Initial() => new()
    {
        Tick = 100,
        Pawns = [new() { Id = "patient", Downed = true }, new() { Id = "defender" }, new() { Id = "raider" }],
    };

    [Fact]
    public void StagingAloneNeverProvesAnOutcomeEvenWithEveryColonistDown()
    {
        var first = Initial() with
        {
            Pawns = [new() { Id = "patient", Downed = true }, new() { Id = "defender", Downed = true }, new() { Id = "raider" }],
        };
        var watch = new SweepFightWatch(first, Staged, 7600);
        watch.Observe(first);
        Assert.Empty(CombatLabBundle.Aggregate("test", Staged, [first]).Winner);
        Assert.False(watch.Finished(first.Tick, false));
        watch.Observe(first with { Tick = 160 });
        Assert.True(watch.Finished(160, false));
    }

    [Fact]
    public void AnInitialPatientDoesNotEndTheFightButLosingTheLastStandingColonistDoes()
    {
        var first = Initial();
        var watch = new SweepFightWatch(first, Staged, 7600);
        watch.Observe(first);
        Assert.False(watch.Finished(first.Tick, false));

        var fighting = first with { Tick = 160 };
        watch.Observe(fighting);
        Assert.False(watch.Finished(fighting.Tick, false));

        var lost = new Fx.LabFightRead
        {
            Tick = 3820,
            Pawns = [new() { Id = "patient", Downed = true }, new() { Id = "defender", Downed = true }, new() { Id = "raider" }],
        };
        watch.Observe(lost);
        Assert.Equal(Sides.Hostile, CombatLabBundle.Aggregate("test", Staged, [first, lost]).Winner);
        Assert.True(watch.Finished(lost.Tick, false));
    }

    [Theory]
    [InlineData(true, false, false, 1, 0)]
    [InlineData(false, true, false, 0, 1)]
    [InlineData(false, false, true, 0, 1)]
    public void DeadMissingAndFleeingHostilesUseCanonicalOutcomeAndCounting(bool dead, bool missing, bool fleeing, int deaths, int fled)
    {
        var first = Initial();
        var watch = new SweepFightWatch(first, Staged, 7600);
        var last = first with
        {
            Tick = 200,
            Pawns = missing
                ? [new() { Id = "patient", Downed = true }, new() { Id = "defender" }]
                : [new() { Id = "patient", Downed = true }, new() { Id = "defender" }, new() { Id = "raider", Dead = dead, Fleeing = fleeing }],
        };
        watch.Observe(last);
        var metrics = CombatLabBundle.Aggregate("test", Staged, [first, last]);
        Assert.Equal(Sides.Colonist, metrics.Winner);
        Assert.Equal(deaths, metrics.EnemyDeaths);
        Assert.Equal(fled, metrics.EnemyFled);
        Assert.True(watch.Finished(last.Tick, false));
    }

    [Fact]
    public void AFleeingColonistStillFightsByTheCanonicalDefinition()
    {
        var first = Initial();
        var watch = new SweepFightWatch(first, Staged, 7600);
        var last = first with
        {
            Tick = 200,
            Pawns = [new() { Id = "patient", Downed = true }, new() { Id = "defender", Fleeing = true }, new() { Id = "raider" }],
        };
        watch.Observe(last);
        Assert.Empty(CombatLabBundle.Aggregate("test", Staged, [first, last]).Winner);
        Assert.False(watch.Finished(last.Tick, false));
    }

    [Fact]
    public void UnseenPodPassengersCannotBeCountedAsFledBeforeTheyArrive()
    {
        var first = Initial() with { Pawns = [new() { Id = "patient", Downed = true }, new() { Id = "defender" }] };
        var watch = new SweepFightWatch(first, Staged, 7600);
        watch.Observe(first);
        watch.Observe(first with { Tick = 200 });
        Assert.False(watch.Finished(200, false));

        watch.Observe(Initial() with { Tick = 300 });
        Assert.False(watch.Finished(300, false));
        watch.Observe(first with { Tick = 400 });
        Assert.True(watch.Finished(400, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CumulativeDamageAlsoProvesAnAbsentPassengerArrived(bool victim)
    {
        var first = Initial() with { Pawns = [new() { Id = "patient", Downed = true }, new() { Id = "defender" }] };
        var watch = new SweepFightWatch(first, Staged, 7600);
        watch.Observe(first with
        {
            Tick = 200,
            Damage = [new() { Tick = 180, Victim = victim ? "raider" : "defender", Instigator = victim ? "defender" : "raider" }],
        });
        Assert.True(watch.Finished(200, false));
    }

    [Fact]
    public void NativeReadsWaitForGameTickProgressAndNeverExtendTheOriginalBudget()
    {
        var watch = new SweepFightWatch(Initial(), Staged, 7600);
        Assert.False(watch.ReadDue(-1));
        Assert.False(watch.ReadDue(100));
        Assert.False(watch.ReadDue(100 + SweepFightWatch.ReadInterval - 1));
        Assert.True(watch.ReadDue(100 + SweepFightWatch.ReadInterval));
        watch.Observe(Initial() with { Tick = 175 });
        Assert.False(watch.ReadDue(175));
        Assert.False(watch.ReadDue(175 + SweepFightWatch.ReadInterval - 1));
        Assert.True(watch.ReadDue(175 + SweepFightWatch.ReadInterval));
        Assert.False(watch.Finished(7599, false));
        Assert.True(watch.Finished(7600, false));
        Assert.True(watch.Finished(7601, false));
        Assert.False(watch.ReadDue(7600));
        Assert.True(watch.Finished(200, true));
    }
}
