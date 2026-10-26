using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;
using Context = RimGovernor.Brain.GameFacts.Common.ObservationContext;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class AcquisitionConflictTests
{
    public interface INative : IReviewSource, IReviewWorkBenchSource { }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly GenerationSnapshot Root = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now() => DateTimeOffset.UtcNow;
    }

    private sealed class Session : IPlayerSession, IWorldSource
    {
        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null) => Task.FromResult(Root);
        public Task ManualAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ManualForResumeAsync(CancellationToken ct) => Task.CompletedTask;
        public bool HoldsGrant(GenerationSnapshot scope) => scope == Root;
        public bool TargetsWorld(World world) => true;
        public void Disable() { }
        public ControlState State() => new(Root, true, true);
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<World> ReadWorldAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    public class Native : DispatchProxy
    {
        internal Context Context = null!;
        internal Action? BeforeTick;
        private readonly DefinitionCatalog _catalog = new(
            [new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" }],
            DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IReviewSource.DefinitionCatalogAsync)) return Task.FromResult(_catalog);
            if (targetMethod.Name == nameof(IReviewSource.TickAsync))
            {
                BeforeTick?.Invoke();
                return Task.FromResult(new RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick { Context = Context, Paused = true, ForcePaused = false });
            }
            throw new NotSupportedException(targetMethod.Name);
        }
    }

    [Fact]
    public async Task CooldownConflictAbortsTheStepAndRefreshedStepWithdrawsTheStalledSource()
    {
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-acquisition-conflict-" + Guid.NewGuid().ToString("N") + ".db"), Ct);
        var settings = Rules.DefaultPlannerSettings();
        var tick = settings.AcquisitionProgress().Deadline.Value + 100;
        var request = new ReviewRequest { Current = Root, Tick = new(tick), Enabled = true,
            Policy = settings, Facts = WorkResourceTestFacts.Create() };
        journal.Review(request, Ct);
        var context = new Context { Identity = Boundary.Identity(Root), Tick = tick,
            NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(Root.Native.Value) };
        var source = DispatchProxy.Create<INative, Native>();
        var native = (Native)source;
        native.Context = context;
        var session = new Session();
        using var player = Player.Create(new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), journal, session, session, Ct);
        var reviewer = new ColonyReview(player, source, new Clock(), settings, TimeSpan.FromSeconds(30));
        var identity = new ObservationIdentity { Context = context };
        var reading = ColonyFactsCoreTests.Reading(identity);
        var acquisition = new AcquisitionSource
        {
            Observed = new() { Source = new() { Id = "plant" }, Token = "plant-cas", Resource = "RawBerries", Yield = 1,
                Designation = new RimGovernor.Brain.GameFacts.Observations.AcquisitionDesignation.Designated(new(1)),
                Taken = false, Kind = new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Plant(true, 1, false) },
            Entity = new() { Id = "plant", DefName = "Plant_Berry", Label = "berries",
                Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new(5, 5)) }
        };
        reading = reading with { Facts = reading.Facts with { Acquisition = [acquisition] },
            Window = new Held<PlanningCells> { Value = new() { Cells = [] } } };
        reviewer.Census.Retain(new() { Reading = reading, Emergency = new(), Frame = new() });
        var planner = new AcquisitionPlanner(reviewer, FoodRules.FoodSupplyNeed.FilingKey);
        var owner = new NeedOwner(FoodRules.FoodSupplyNeed, Root, []);
        native.BeforeTick = () => journal.Review(request, Ct);
        using var entry = await player.EnterAsync("acquisition-conflict", false, Ct);
        await Assert.ThrowsAsync<ConflictException>(() => planner.StepAsync(new StepArbiter(), entry.Call, entry.Epoch, owner));
        Assert.Empty(journal.LoadNeed(FoodRules.FoodSupplyNeed, Root, Ct).Methods);
        native.BeforeTick = null;
        var result = await planner.StepAsync(new StepArbiter(), entry.Call, entry.Epoch,
            new(FoodRules.FoodSupplyNeed, Root, []));
        Assert.Equal(Verdict.BuildingReasonAdmitted, result.Verdict);
        var action = Assert.Single(journal.LoadPlan(result.Plan, Ct).Spec.Actions);
        Assert.Equal("plant", action.AcquisitionWithdraw!.Thing);
    }
}
