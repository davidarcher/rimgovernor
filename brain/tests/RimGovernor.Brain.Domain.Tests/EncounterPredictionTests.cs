using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Host;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class EncounterPredictionTests
{
    [Fact]
    public async Task ChangedHelperDepartureRejectsOtherwiseMatchingReply()
    {
        var request = Request();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<EncounterPrediction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new Predictor((r, _) => { started.SetResult(); return reply.Task; });
        var current = request;
        var consumer = new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(30));
        var pending = consumer.PredictAsync(request, () => current, TestContext.Current.CancellationToken);
        await started.Task;
        current = request with { Actors = [request.Actors[0] with { Departure = new(12, EncounterDepartureKind.Leave) }, request.Actors[1]] };
        reply.SetResult(Evidence(request));
        var result = await pending;
        Assert.Equal(EncounterStatus.Stale, result.Status);
        Assert.Empty(result.Samples);
        Assert.False(result.QualifiedForSelection);
    }

    [Theory]
    [InlineData(EncounterStatus.Unavailable)]
    [InlineData(EncounterStatus.Unsupported)]
    [InlineData(EncounterStatus.Malformed)]
    public async Task FailedProvidersDoNotAuthorize(EncounterStatus status)
    {
        var request = Request();
        var fake = new Predictor((_, _) => Task.FromResult(Evidence(request) with { Status = status, Samples = [] }));
        var result = await new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(30)).PredictAsync(request, () => request, TestContext.Current.CancellationToken);
        Assert.Equal(status, result.Status);
        Assert.False(result.QualifiedForSelection);
    }

    [Fact]
    public async Task CancellationCompletesEvenWhenProviderIgnoresItAndLateReplyCannotEscape()
    {
        var request = Request();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new TaskCompletionSource<EncounterPrediction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new Predictor((_, _) => { started.SetResult(); return delayed.Task; });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(30)).PredictAsync(request, () => request, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        var result = await pending;
        Assert.Equal(EncounterStatus.Cancelled, result.Status);
        delayed.SetResult(Evidence(request));
        Assert.Empty(result.Samples);
        Assert.False(result.QualifiedForSelection);
    }

    [Fact]
    public async Task StaleProviderIdentityIsRejectedWithoutChangingCurrentScenario()
    {
        var request = Request();
        var fake = new Predictor((_, _) => Task.FromResult(Evidence(request) with { RequestFingerprint = "previous-request" }));
        var result = await new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(30)).PredictAsync(request, () => request, TestContext.Current.CancellationToken);
        Assert.Equal(EncounterStatus.Stale, result.Status);
    }

    [Fact]
    public async Task IndependentFakeCanReturnConsequencesWithoutWorkerOrSafetyClaim()
    {
        var request = Request();
        var fake = new Predictor((_, _) => Task.FromResult(Evidence(request)));
        var result = await new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(30)).PredictAsync(request, () => request, TestContext.Current.CancellationToken);
        Assert.Single(result.Samples);
        Assert.Equal(20, result.Samples[0].DurationTicks);
        Assert.False(result.QualifiedForSelection);
    }

    [Fact]
    public async Task UnknownGearAndChangedBuildRejectBeforeStartingMissingExecutable()
    {
        var request = Request();
        await using var worker = new LocalEncounterPredictor(Options());
        var gear = request with { Actors = [request.Actors[0] with { Weapon = new("rifle", "", "", 0) }, request.Actors[1]] };
        Assert.Equal("weapon_condition_unknown", (await worker.PredictAsync(gear, TestContext.Current.CancellationToken)).Reason);
        var changed = request with { Identity = request.Identity with { GameBuild = "different" } };
        Assert.Equal("observation_build_or_mods_mismatch", (await worker.PredictAsync(changed, TestContext.Current.CancellationToken)).Reason);
        Assert.Equal(EncounterStatus.Unavailable, (await worker.PredictAsync(request, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task UnsupportedObservationsAndEffortAreExplicitNotTruncated()
    {
        var request = Request();
        await using var worker = new LocalEncounterPredictor(Options());
        var unknown = request with { UnsupportedObservations = ["temperature", "custom_gene"] };
        Assert.Contains("temperature", (await worker.PredictAsync(unknown, TestContext.Current.CancellationToken)).Reason, StringComparison.Ordinal);
        var excessive = request with { Seeds = [1, 2] };
        Assert.Equal("sample_or_tick_budget", (await worker.PredictAsync(excessive, TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData("negative_products")]
    [InlineData("late_shot")]
    [InlineData("late_melee")]
    [InlineData("melee_after_withdrawal")]
    [InlineData("negative_tick")]
    [InlineData("after_withdrawal")]
    [InlineData("missing_participant")]
    public async Task MalformedIndependentProviderConsequencesAreRejected(string fault)
    {
        var request = Request() with { Actors = [Request().Actors[0] with { Departure = new(12, EncounterDepartureKind.Leave) }, Request().Actors[1]] };
        var evidence = Evidence(request);
        var actor = evidence.Samples[0].Actors[0];
        actor = fault switch
        {
            "negative_products" => actor with { Dead = true, Products = new("meat", -1, "", 0) },
            "late_shot" => actor with { FirstAttackTick = 0, LastShotTick = 15 },
            "late_melee" => actor with { FirstAttackTick = 15 },
            "melee_after_withdrawal" => actor with { FirstAttackTick = 10, WithdrawalTick = 5 },
            "negative_tick" => actor with { FirstAttackTick = -1 },
            "after_withdrawal" => actor with { FirstAttackTick = 0, LastShotTick = 10, WithdrawalTick = 5 },
            _ => actor,
        };
        evidence = evidence with { Samples = [evidence.Samples[0] with { Actors = fault == "missing_participant" ? [] : [actor, evidence.Samples[0].Actors[1]] }] };
        var fake = new Predictor((_, _) => Task.FromResult(evidence));
        var result = await new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(30)).PredictAsync(request, () => request, TestContext.Current.CancellationToken);
        Assert.Equal(EncounterStatus.Malformed, result.Status);
        Assert.False(result.QualifiedForSelection);
    }

    [Fact]
    public async Task QualificationNeedsAcceptedReportAndExactScenarioScope()
    {
        var request = Request();
        var qualified = Evidence(request) with
        {
            EngineFingerprint = "engine",
            Qualification = new("criteria-v1", "report-1", "fixture-domain", "build", "mods", request.Identity.Tactic, "engine", [EncounterPredictionConsumer.Fingerprint(request)]),
        };
        var fake = new Predictor((_, _) => Task.FromResult(qualified));
        var ordinary = await new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(30)).PredictAsync(request, () => request, TestContext.Current.CancellationToken);
        Assert.False(ordinary.QualifiedForSelection);
        var consumer = new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(30), [qualified.Qualification]);
        Assert.True((await consumer.PredictAsync(request, () => request, TestContext.Current.CancellationToken)).QualifiedForSelection);
        var changed = request with { Identity = request.Identity with { Mods = "changed" } };
        qualified = qualified with { RequestFingerprint = EncounterPredictionConsumer.Fingerprint(changed), Qualification = qualified.Qualification with { Mods = "changed", CoveredRequestFingerprints = [EncounterPredictionConsumer.Fingerprint(changed)] } };
        Assert.False((await consumer.PredictAsync(changed, () => changed, TestContext.Current.CancellationToken)).QualifiedForSelection);
    }

    [Fact]
    public async Task ReviewedReportQualifiesMatchingEvidenceWithoutProviderSelfCertification()
    {
        var request = Request();
        var evidence = Evidence(request) with { EngineFingerprint = "engine:mods=official" };
        var accepted = new EncounterQualification("reviewed-v1", "reviewed-report", "tested-domain", "build", "mods",
            request.Identity.Tactic, evidence.EngineFingerprint, [EncounterPredictionConsumer.Fingerprint(request)]);
        var fake = new Predictor((_, _) => Task.FromResult(evidence));
        var consumer = new EncounterPredictionConsumer(fake, TimeSpan.FromSeconds(30), [accepted]);
        Assert.True((await consumer.PredictAsync(request, () => request, TestContext.Current.CancellationToken)).QualifiedForSelection);
        evidence = evidence with { EngineFingerprint = "engine:mods=changed" };
        Assert.False((await consumer.PredictAsync(request, () => request, TestContext.Current.CancellationToken)).QualifiedForSelection);
    }
    private static LocalEncounterOptions Options() => new("missing-predictor-executable", "missing", "missing", ".", "build", "mods", "predictor-mods",
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 1, 100, 60, 10, 65536);

    private static EncounterRequest Request() => new()
    {
        Identity = new("world", 10, "build", "mods", "native-hunt/timed-departure"),
        Width = 60, Height = 60, Terrain = "soil", Weather = "clear", HorizonTicks = 100, Seeds = [1], SamplingReason = "paired-smoke",
        Actors = [new() { Id = "hunter", Kind = "human", Wild = false, X = 1, Z = 1, Age = 30, Xenotype = "baseline", HuntTarget = "prey" },
            new() { Id = "prey", Kind = "deer", Wild = true, X = 2, Z = 2 }],
    };
    private static EncounterPrediction Evidence(EncounterRequest request) => new()
    {
        Status = EncounterStatus.Evidence, RequestFingerprint = EncounterPredictionConsumer.Fingerprint(request), Reason = "fake-observation",
        Samples = [new(1, 20, false, request.Actors.Select(a => new EncounterActorOutcome(a.Id, false, false, 1, 0, false, [], [], [], null, null, null, null, null)).ToArray())], ModelUncertainty = ["unqualified"],
    };
    private sealed class Predictor(Func<EncounterRequest, CancellationToken, Task<EncounterPrediction>> predict) : IEncounterPredictor
    {
        public Task<EncounterPrediction> PredictAsync(EncounterRequest request, CancellationToken cancellationToken) => predict(request, cancellationToken);
    }
}
