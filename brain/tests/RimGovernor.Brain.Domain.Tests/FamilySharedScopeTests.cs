using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;
using PbCommon = RimGovernor.Contracts.Wire.Common;
using PbLifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The wave's family reads validate against the scope the clock step read once (#3556): the step hands it to each
/// family read, a read that has it costs no tick hop, and a read without one (outside a step) reads the tick itself.
/// </summary>
public sealed class FamilySharedScopeTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly PbCommon.ObservationContext Context = new()
    {
        Identity = new PbCommon.Identity { ColonyId = "colony", MapId = 1, LoadToken = "load" },
        Tick = 3000,
        NativeGeneration = 1,
    };

    /// <summary>A tick source that counts its hops and answers with <see cref="Context"/>.</summary>
    private sealed class Ticks : ISource
    {
        public int Hops { get; private set; }

        public Task<PbLifecycle.TickReply> TickAsync(CancellationToken cancellationToken)
        {
            Hops++;
            return Task.FromResult(new PbLifecycle.TickReply { Outcome = new PbLifecycle.LoadedTick { Context = Context } });
        }
    }

    private sealed record ScopeFacts(Identity? Scope);

    /// <summary>One family whose read records the scope it was handed.</summary>
    private sealed class ScopeFamily : IFamily<ScopeFacts>
    {
        public string Id => "test-scope";

        public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony];

        public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

        public IReadOnlyList<Need> Derive(ScopeFacts facts) =>
            [new Need { Id = NeedId.Of("test-scope", "scope"), Tier = NeedTier.Development, Satisfaction = Satisfaction.Met }];

        public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, ScopeFacts facts) => [];
    }

    [Fact]
    public void TheStepsIdentityIsItsClockStatusContext()
    {
        var identity = DecodeReads.ContextIdentity(Context);
        Assert.Equal((World.Colony, World.Map, World.Load, new Tick(3000)), (identity.Colony, identity.Map, identity.Load, identity.Tick));
        Assert.Equal(World.Native, identity.NativeGeneration.TryGetValue(out var generation) ? generation : default);
    }

    [Fact]
    public async Task AReadWithTheStepsScopeHopsForNoTickAndOneWithoutReadsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticks = new Ticks();
        var known = DecodeReads.ContextIdentity(Context);

        var shared = await StepScope.StepScopeAsync(new FamilyReadScope(World, false, known), ticks, ct);
        Assert.Same(known, shared);
        Assert.Equal(0, ticks.Hops);

        var own = await StepScope.StepScopeAsync(new FamilyReadScope(World, false), ticks, ct);
        Assert.Equal(known, own);
        Assert.Equal(1, ticks.Hops);
    }

    [Fact]
    public async Task TheFamilyStepHandsItsScopeToTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-shared-scope-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with { Colonists = Known(3L), IndoorCapacity = Known(3L), BedCapacity = Known(3L), FieldCoverage = Known(1.0) };
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        var handed = new List<Identity?>();
        var runner = new FamilyRunner<ScopeFacts>(new ScopeFamily(), (scope, _) =>
        {
            handed.Add(scope.Scope);
            return Task.FromResult(new ScopeFacts(scope.Scope));
        }, new FamilySchedule { Kinds = [ActionKind.Building] });
        var known = DecodeReads.ContextIdentity(Context);

        await runner.RunAsync(new FamilyStep { Journal = journal, World = World, Scope = known }, ct);
        await runner.RunAsync(new FamilyStep { Journal = journal, World = World }, ct);

        Assert.Equal([known, null], handed);
    }
}
