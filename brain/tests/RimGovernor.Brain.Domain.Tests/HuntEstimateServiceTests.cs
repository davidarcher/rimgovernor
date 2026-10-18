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
        Meat = Fact.Known(100.0), MeatNutrition = Fact.Known(0.05),
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

    static async Task Settle(HuntEstimateService service, long tick)
    {
        for (var i = 0; i < 200 && service.Current(tick).Pending.Count != 0; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void BuildStatesNoHerdForPreyAloneInTheRequest()
    {
        var herded = Read() with { Prey = [Prey("boar") with { Herd = "wild-herd" }, Prey("boar2") with { Herd = "wild-herd" }] };
        var alone = HuntEncounterRequests.Build(herded, Roster, 5).Request!;
        Assert.Equal("", alone.Actors.Single(a => a.Wild).Herd);
        var both = HuntEncounterRequests.Build(herded, new(EquatableArray.From(["a"]), EquatableArray.From(["boar", "boar2"])), 5).Request!;
        Assert.All(both.Actors.Where(a => a.Wild), a => Assert.Equal("wild-herd", a.Herd));
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
    public void AnAnchorStillDescribesAReadAfterSmallGunnerMovesButNotAnInjuryOrALargeMoveAndNeverJudgesWhereThePreyStands()
    {
        HuntRoster[] solo = [Roster];
        Assert.True(HuntEncounterRequests.StillAnchored(Read(), Read(hunterX: 14), solo));
        Assert.False(HuntEncounterRequests.StillAnchored(Read(), Read(hunterX: 25), solo));
        Assert.False(HuntEncounterRequests.StillAnchored(Read(), Read(health: "Cut"), solo));
        // Biological age is a float that grows every tick: a few ticks of ageing must not stale an anchor.
        var older = Read();
        older = older with { Hunters = [older.Hunters[0] with { Age = older.Hunters[0].Age + 0.00001 }] };
        Assert.True(HuntEncounterRequests.StillAnchored(Read(), older, solo));
        // The prey is judged by presence and group, never by where it grazed to.
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "boar" };
        Assert.True(HuntEncounterRequests.StillAnchored(Read(), Read() with { Prey = [Prey("boar", 55)] }, solo));
        Assert.Empty(HuntEncounterRequests.PreyDrift(Read(), Read() with { Prey = [Prey("boar", 55)] }, allowed));
        Assert.Equal(["boar=unobserved"], HuntEncounterRequests.PreyDrift(Read(), Read() with { Prey = [] }, allowed));
        Assert.Equal(["boar=downed"], HuntEncounterRequests.PreyDrift(Read(), Read() with { Prey = [Prey("boar") with { Downed = true }] }, allowed));
        Assert.Equal(["boar=gone"], HuntEncounterRequests.PreyDrift(Read(), Read(), new HashSet<string>(StringComparer.Ordinal)));
        var herd = Read() with { Prey = [Prey("boar") with { Herd = "wild-herd" }, Prey("boar2", 34) with { Herd = "wild-herd" }] };
        allowed.Add("boar2");
        Assert.Empty(HuntEncounterRequests.PreyDrift(herd, herd with { Prey = [Prey("boar", 50) with { Herd = "wild-herd" }, Prey("boar2", 58) with { Herd = "wild-herd" }] }, allowed));
        Assert.Equal(["group:boar+boar2"], HuntEncounterRequests.PreyDrift(herd, herd with { Prey = [Prey("boar") with { Herd = "wild-herd" }, Prey("boar2", 58) with { Herd = "wild-herd" }] }, allowed));
        // A gunner's skill level or a hediff's severity moving is no change; the hediff set is.
        var cut = Read(health: "Cut");
        var healing = cut with { Hunters = [cut.Hunters[0] with { Skills = [new("Shooting", 9)], Health = [new("Cut", "arm", 0.1)] }] };
        Assert.True(HuntEncounterRequests.StillAnchored(cut, healing, solo));
        Assert.False(HuntEncounterRequests.StillAnchored(cut, Read(), solo));
        Assert.Equal("health(-Cut/arm)", HuntEncounterRequests.Differences(cut.Hunters[0], Read().Hunters[0]));
        Assert.Equal("", HuntEncounterRequests.Differences(cut.Hunters[0], healing.Hunters[0]));
        // A comparison stays anchored while every rostered gunner is described; one drifting past the tolerance ends it, and the drift names him.
        var pair = Pair();
        HuntRoster[] rosters = [PairRoster, new(EquatableArray.From(["a"]), EquatableArray.From(["boar"]))];
        Assert.True(HuntEncounterRequests.StillAnchored(pair, pair with { Hunters = [Hunter("a", 18), Hunter("b"), Hunter("h", 40) with { Hauls = true }] }, rosters));
        var strayed = pair with { Hunters = [Hunter("a"), Hunter("b", 19), Hunter("h") with { Hauls = true }] };
        Assert.False(HuntEncounterRequests.StillAnchored(pair, strayed, rosters));
        Assert.Equal(["b=moved:9"], HuntEncounterRequests.GunnerDrift(pair, strayed, rosters));
    }

    [Fact]
    public async Task RostersAreAskedOnceFromTheAnchorAndAnotherAnchorStartsANewComparison()
    {
        var fake = new Fake(Evidence);
        await using var service = new HuntEstimateService(new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(5)));
        var anchor = Read();
        Assert.Null(service.Current(100).Anchor);
        Assert.Equal(1, service.Request(anchor, [Roster], 100));
        Assert.Equal(0, service.Request(anchor, [Roster], 100));
        await Settle(service, 100);
        var view = service.Current(100);
        Assert.Same(anchor, view.Anchor);
        var estimate = Assert.Single(view.Estimates).Value;
        Assert.Equal((16, 0, 0), (estimate.Samples, estimate.HarmSamples, estimate.DeathSamples));
        // The answer is held for the comparison whatever the colony does meanwhile: the service never judges a read, policy does.
        Assert.Equal(0, service.Request(anchor, [Roster], 200));
        Assert.Equal(1, fake.Calls);
        // A read handed as the anchor that is not the comparison's, even an equal one, is a new comparison: the old answers are gone and the scene is asked again.
        var again = Read();
        Assert.Equal(1, service.Request(again, [Roster], 200));
        var restarted = service.Current(200);
        Assert.Same(again, restarted.Anchor);
        Assert.Empty(restarted.Estimates);
        await Settle(service, 200);
        Assert.Equal(2, fake.Calls);
        Assert.Single(service.Current(200).Estimates);
        // A day later the estimate has expired.
        Assert.Empty(service.Current(200 + HuntEncounterRequests.Lifetime + 1).Estimates);
    }

    [Fact]
    public async Task ASceneQueuedForAReplacedComparisonIsSkippedWithoutAPrediction()
    {
        using var gate = new SemaphoreSlim(0);
        var fake = new Fake(r => { gate.Wait(TimeSpan.FromSeconds(10)); return Evidence(r); });
        await using var service = new HuntEstimateService(new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(5)));
        var first = Pair();
        var solo = new HuntRoster(EquatableArray.From(["a"]), EquatableArray.From(["boar"]));
        Assert.Equal(2, service.Request(first, [solo, PairRoster], 100));
        for (var i = 0; i < 200 && Volatile.Read(ref fake.Calls) == 0; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        // The comparison restarts while the solo is in flight and the pair waits: both scenes are queued again at once for the new
        // comparison, the old pair is skipped unasked when the loop reaches it, and the old solo's answer belongs to the old comparison.
        var second = Pair();
        Assert.Equal(2, service.Request(second, [solo, PairRoster], 150));
        Assert.Equal(2, service.Current(150).Pending.Count);
        gate.Release(3);
        await Settle(service, 150);
        Assert.Equal(3, fake.Calls);
        Assert.Equal(2, service.Current(150).Estimates.Count);
        Assert.Same(second, service.Current(150).Anchor);
    }

    [Fact]
    public async Task ABusyServiceIsRetriedSoonAndAnUnsupportedRosterIsRefusedNotZeroRisk()
    {
        var busy = true;
        var fake = new Fake(r => busy ? EncounterPredictionConsumer.Refused(EncounterStatus.Busy, EncounterPredictionConsumer.Fingerprint(r), "worker_busy") : Evidence(r));
        await using var service = new HuntEstimateService(new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(5)));
        var read = Read();
        service.Request(read, [Roster], 100);
        await Settle(service, 100);
        var view = service.Current(100);
        Assert.Empty(view.Estimates);
        Assert.StartsWith("prediction_busy", Assert.Single(view.Refusals).Value);
        Assert.Empty(service.Current(100 + HuntEstimateService.RetryTicks + 1).Refusals);
        busy = false;
        Assert.Equal(1, service.Request(read, [Roster], 100 + HuntEstimateService.RetryTicks + 1));
        await Settle(service, 100 + HuntEstimateService.RetryTicks + 1);
        Assert.Single(service.Current(100 + HuntEstimateService.RetryTicks + 1).Estimates);
        var unarmed = Read() with { Hunters = [Hunter("a") with { Weapon = null }] };
        service.Request(unarmed, [Roster], 100 + HuntEstimateService.RetryTicks + 1);
        await Settle(service, 100 + HuntEstimateService.RetryTicks + 1);
        Assert.Equal("hunter_unarmed", service.Current(100 + HuntEstimateService.RetryTicks + 1).Refusals[Roster.Key]);
    }

    static HuntActivityRead Pair(int haulerX = 10) => Read() with
    {
        Hunters = [Hunter("a"), Hunter("b"), Hunter("h", haulerX) with { Hauls = true }],
        Prey = [Prey("boar") with { CorpseStore = new(5, 5) }],
    };
    static readonly HuntRoster PairRoster = new(EquatableArray.From(["a", "b"]), EquatableArray.From(["boar"]));

    [Fact]
    public async Task AHaulersRosterSharesItsGunnersSceneButAGunnerRetrieverIsAnotherSceneWithATimedLeave()
    {
        var fake = new Fake(Evidence);
        await using var service = new HuntEstimateService(new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(5)));
        var read = Pair();
        var hauled = PairRoster with { Retrievers = ["h"] };
        var departed = PairRoster with { Retrievers = ["b"], DepartureTick = 400 };
        Assert.Equal(1, service.Request(read, [hauled, PairRoster], 100));
        await Settle(service, 100);
        Assert.Equal(1, fake.Calls);
        Assert.True(service.Current(100).Estimates.ContainsKey(PairRoster.SceneKey));
        // The hauler is not in the scene: its walk is priced from each round's routes, so its moving stales nothing.
        Assert.True(HuntEncounterRequests.StillAnchored(read, Pair(haulerX: 25), [hauled]));
        Assert.Equal(0, service.Request(read, [hauled, PairRoster], 100));
        // The gunner-retriever leaves the predicted encounter at the measured tick; its roster is a scene of its own.
        var request = HuntEncounterRequests.Build(read, departed, 5).Request!;
        Assert.Equal(new EncounterDeparture(400, EncounterDepartureKind.Leave), request.Actors.Single(a => a.Id == "b").Departure);
        Assert.Null(request.Actors.Single(a => a.Id == "a").Departure);
        Assert.Equal("departure_outside_horizon", HuntEncounterRequests.Build(read, departed with { DepartureTick = HuntEncounterRequests.Horizon + 1 }, 5).Refusal);
        Assert.Equal(1, service.Request(read, [departed], 100));
        await Settle(service, 100);
        Assert.Equal(2, fake.Calls);
        Assert.Equal(2, service.Current(100).Estimates.Count);
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
            Assert.Equal(HuntEstimateView.None.Revision, service.Current(1).Revision);
        }
        finally
        {
            File.Delete(worker);
        }
    }
}
