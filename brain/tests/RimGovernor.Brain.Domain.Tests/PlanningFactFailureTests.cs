using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using Xunit;
using Clock = RimGovernor.Brain.GameFacts.Clock;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PlanningFactFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public class Game : DispatchProxy
    {
        internal Exception? StatusFailure { get; set; }
        internal Clock.Status Status { get; } = new()
        {
            Context = ColonyFactsCoreTests.Snapshot().Context!, State = Clock.ClockState.Held,
            Hold = new Clock.HoldState.NeverRunCase(), LastRun = new Clock.RunHistory.NeverRunCase(),
            Pacing = new Clock.Pacing.StoppedCase(), HoldSerial = 0,
            PlayerRequest = new Clock.PlayerSpeedRequest.NoneCase(), NativeTickBoundary = true,
            ActualPaused = true, EffectiveTicksPerSecond = 0,
        };

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            nameof(IGameApi.ReadTickAsync) => Task.FromResult(new RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick
                { Context = Status.Context, Paused = true, ForcePaused = false }),
            nameof(IGameApi.ReadClockStatusAsync) => StatusFailure is { } failure
                ? Task.FromException<Clock.Status>(failure) : Task.FromResult(Status),
            _ => throw new InvalidOperationException(targetMethod.Name),
        };
    }

    [Fact]
    public async Task OptionalClockAbsenceDoesNotReuseEarlierStatusAndRecoversOnNextRead()
    {
        var facade = DispatchProxy.Create<IGameApi, Game>();
        var game = (Game)(object)facade;
        using var client = new GameClient(facade);
        var request = new StepRequest { ClockStatus = true };
        Assert.Same(game.Status, (await client.ReadStepAsync(request, Ct))!.ClockStatus);
        game.StatusFailure = new BridgeUnavailableException("clock not owned");
        Assert.Null((await client.ReadStepAsync(request, Ct))!.ClockStatus);
        game.StatusFailure = null;
        Assert.Same(game.Status, (await client.ReadStepAsync(request, Ct))!.ClockStatus);
        var requiredFailure = new RequiredFactException("clock context");
        game.StatusFailure = requiredFailure;
        Assert.Same(requiredFailure, await Assert.ThrowsAsync<RequiredFactException>(() => client.ReadStepAsync(request, Ct)));
    }

    [Fact]
    public void ContainmentRejectsMissingStuffFactsThenUsesRefreshedSharedMaterialWithoutStock()
    {
        var reading = ColonyFactsCoreTests.Reading();
        var broken = new PlanningDefinition { Name = "shell", Stuffed = true };
        var door = new PlanningDefinition { Name = "door", Stuffed = true,
            StuffOptions = [new StuffOption { Stuff = "material", Costs = [new(new("material"), 1)] }] };
        Assert.Throws<NoStuffDataException>(() => AnimalContainment.ShellSharedStuff(reading, broken, door));
        var refreshed = broken with { StuffOptions = door.StuffOptions };
        Assert.Equal("material", AnimalContainment.ShellSharedStuff(reading, refreshed, door));
    }
}
