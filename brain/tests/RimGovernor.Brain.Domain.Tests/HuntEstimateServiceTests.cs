using RimGovernor.Brain.Host;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HuntEstimateServiceTests
{
    sealed class Fake(Func<EncounterRequest, EncounterPrediction> reply) : IEncounterPredictor
    {
        public int Calls;
        public Task<EncounterPrediction> PredictAsync(EncounterRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(reply(request));
        }
    }

    static HuntActorRead Hunter(string id, int x = 10) => new(id, "Colonist", new(x, 10), 30)
    {
        Xenotype = "Baseliner", Weapon = new("Gun_BoltActionRifle", "", "Normal", 100), WeaponRange = Fact.Known(30.0),
        Skills = [new("Shooting", 8)], Available = true,
    };
    static HuntActorRead Prey(string id, int x = 30) => new(id, "Boar", new(x, 10), 3)
    {
        MeatDefinition = "Meat_Pig", Meat = Fact.Known(100.0), MeatNutrition = Fact.Known(0.05),
    };
    static HuntActivityRead Read(int hunterX = 10, string health = "") => new()
    {
        Known = true, Hunters = [Hunter("a", hunterX) with { Health = health.Length == 0 ? [] : [new(health, "arm", 0.5)] }], Prey = [Prey("boar")],
        Scene = new("w", "build", "mods", "Clear", "Soil", 0, 0, 60, 40, 20, 1.0),
    };
    static readonly HuntRoster Roster = new(EquatableArray.From(["a"]), EquatableArray.From(["boar"]));

    static EncounterPrediction Evidence(EncounterRequest request) => new()
    {
        Status = EncounterStatus.Evidence, RequestFingerprint = EncounterPredictionConsumer.Fingerprint(request), Reason = "",
        Samples = request.Seeds.Select(seed => new EncounterSample(seed, 800, false, request.Actors.Select(a =>
            new EncounterActorOutcome(a.Id, false, false, 1, 0, false, [], [], [], null, null, null, null, null)).ToArray())).ToArray(),
    };

    static async Task Settle(HuntEstimateService service, HuntActivityRead read, long tick)
    {
        for (var i = 0; i < 200 && service.Current(read, tick).Pending.Count != 0; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void BuildStatesTheSceneInTheWindowAndRefusesWhatItCannotState()
    {
        var (request, refusal) = HuntEncounterRequests.Build(Read(), Roster, 5);
        Assert.Equal("", refusal);
        Assert.Equal((60, 40, "native-hunt/timed-departure", HuntEncounterRequests.Samples), (request!.Width, request.Height, request.Identity.Tactic, request.Seeds.Count));
        Assert.Equal("boar", request.Actors.Single(a => !a.Wild).HuntTarget);
        Assert.Equal(30, request.Actors.Single(a => a.Wild).X);
        var unarmed = Read() with { Hunters = [Hunter("a") with { Weapon = null }] };
        Assert.Equal("hunter_unarmed", HuntEncounterRequests.Build(unarmed, Roster, 5).Refusal);
        var strange = Read() with { Hunters = [Hunter("a") with { Xenotype = "" }] };
        Assert.Equal("hunter_xenotype_unobserved", HuntEncounterRequests.Build(strange, Roster, 5).Refusal);
        Assert.Equal("scene_unobserved", HuntEncounterRequests.Build(Read() with { Scene = null }, Roster, 5).Refusal);
        var outside = Read() with { Prey = [Prey("boar", 90)] };
        Assert.Equal("actor_outside_scene", HuntEncounterRequests.Build(outside, Roster, 5).Refusal);
    }

    [Fact]
    public void AnEstimateSurvivesSmallMovesButNotAnInjuryOrALargeMove()
    {
        Assert.True(HuntEncounterRequests.StillDescribes(Read(), Read(hunterX: 14), Roster));
        Assert.False(HuntEncounterRequests.StillDescribes(Read(), Read(hunterX: 25), Roster));
        Assert.False(HuntEncounterRequests.StillDescribes(Read(), Read(health: "Cut"), Roster));
    }

    [Fact]
    public async Task RostersAreAskedOnceAnsweredAsEstimatesAndReusedWhileTheySTillDescribeTheWorld()
    {
        var fake = new Fake(Evidence);
        await using var service = new HuntEstimateService(new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(5)));
        var read = Read();
        Assert.Equal(1, service.Request(read, [Roster], 100));
        Assert.Equal(0, service.Request(read, [Roster], 100));
        await Settle(service, read, 100);
        var view = service.Current(read, 100);
        var estimate = Assert.Single(view.Estimates).Value;
        Assert.Equal((16, 0, 0), (estimate.Samples, estimate.HarmSamples, estimate.DeathSamples));
        Assert.Equal(0, service.Request(Read(hunterX: 12), [Roster], 200));
        Assert.Equal(1, fake.Calls);
        // An injured hunter is a different scene: the estimate is no longer current and the roster is asked again.
        var hurt = Read(health: "Cut");
        Assert.Empty(service.Current(hurt, 200).Estimates);
        Assert.Equal(1, service.Request(hurt, [Roster], 200));
        await Settle(service, hurt, 200);
        Assert.Equal(2, fake.Calls);
        // A day later the estimate has expired.
        Assert.Empty(service.Current(hurt, 200 + HuntEncounterRequests.Lifetime + 1).Estimates);
    }

    [Fact]
    public async Task ABusyServiceIsRetriedSoonAndAnUnsupportedRosterIsRefusedNotZeroRisk()
    {
        var busy = true;
        var fake = new Fake(r => busy ? EncounterPredictionConsumer.Refused(EncounterStatus.Busy, EncounterPredictionConsumer.Fingerprint(r), "worker_busy") : Evidence(r));
        await using var service = new HuntEstimateService(new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(5)));
        var read = Read();
        service.Request(read, [Roster], 100);
        await Settle(service, read, 100);
        var view = service.Current(read, 100);
        Assert.Empty(view.Estimates);
        Assert.Equal("prediction_busy", Assert.Single(view.Refusals).Value);
        Assert.Empty(service.Current(read, 100 + HuntEstimateService.RetryTicks + 1).Refusals);
        busy = false;
        Assert.Equal(1, service.Request(read, [Roster], 100 + HuntEstimateService.RetryTicks + 1));
        await Settle(service, read, 100 + HuntEstimateService.RetryTicks + 1);
        Assert.Single(service.Current(read, 100 + HuntEstimateService.RetryTicks + 1).Estimates);
        var unarmed = Read() with { Hunters = [Hunter("a") with { Weapon = null }] };
        service.Request(unarmed, [Roster], 100 + HuntEstimateService.RetryTicks + 1);
        await Settle(service, unarmed, 100 + HuntEstimateService.RetryTicks + 1);
        Assert.Equal("hunter_unarmed", service.Current(unarmed, 100 + HuntEstimateService.RetryTicks + 1).Refusals[Roster.Key]);
    }

    [Fact]
    public async Task TheWorkerIsConfiguredByAnExistingAbsoluteFileOrTheServiceSaysWhyNot()
    {
        Assert.Null(HuntEstimateService.Create(_ => null, "b", "m", "m", "/game", "/work", out var reason));
        Assert.Contains(HuntEstimateService.WorkerVariable, reason);
        Assert.Null(HuntEstimateService.Create(name => name == HuntEstimateService.WorkerVariable ? "relative/worker.dll" : null, "b", "m", "m", "/game", "/work", out reason));
        Assert.Contains("existing absolute file", reason);
        var worker = Path.GetTempFileName();
        try
        {
            await using var service = HuntEstimateService.Create(name => name == HuntEstimateService.WorkerVariable ? worker : null, "b", "m", "m", "/game", "/work", out reason)!;
            Assert.Equal("", reason);
            Assert.Equal(HuntEstimateView.None.Revision, service.Current(Read(), 1).Revision);
        }
        finally
        {
            File.Delete(worker);
        }
    }
}
