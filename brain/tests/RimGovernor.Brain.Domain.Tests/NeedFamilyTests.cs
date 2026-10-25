using Microsoft.Data.Sqlite;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The Need/Family spine (#3250), proven end to end with a test family: Needs derive, Methods are admitted under
/// the <c>need</c> owner with claims, a precondition waits, and the SeriousHarm Need runs in the immediate pass on
/// its narrow read alone.
/// </summary>
public sealed class NeedFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly NeedId Bleeding = NeedId.Of("test-care", "bleeding", "a");
    private static readonly NeedId Rest = NeedId.Of("test-care", "rest");

    /// <summary>What the test family reads: pawn a bleeding (Emergency, the narrow read) and whether the colony is rested (Pawns).</summary>
    private sealed record CareFacts(bool Bleeding, bool Rested);

    /// <summary>
    /// Two Needs: pawn a's bleeding (SeriousHarm) and the colony's rest (Wellbeing), which waits on the bleeding
    /// being stopped. Each is served by a delivery of its own pawn; <see cref="Clash"/> makes rest claim pawn a too.
    /// </summary>
    private sealed class CareFamily : IFamily<CareFacts>
    {
        public string Id => "test-care";

        public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns, FactFamily.Emergency];

        public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [FactFamily.Emergency];

        public bool RestWaits { get; set; } = true;

        public bool Clash { get; set; }

        public IReadOnlyList<Need> Derive(CareFacts facts) =>
        [
            new Need { Id = Rest, Tier = NeedTier.Wellbeing, Satisfaction = Of(facts.Rested, met: true), Preconditions = RestWaits ? [Bleeding] : [] },
            .. DeriveSeriousHarm(facts),
        ];

        // The immediate pass reads only the bleeding, so it derives only the SeriousHarm Need.
        public IReadOnlyList<Need> DeriveSeriousHarm(CareFacts facts) =>
            [new Need { Id = Bleeding, Tier = NeedTier.SeriousHarm, Satisfaction = Of(facts.Bleeding, met: false) }];

        public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, CareFacts facts) =>
        [
            .. ready.Select(n => n.Id == Bleeding
                ? new NeedMethod(n.Id, new("tend-a"), "pawn a is bleeding", id => Plan(id, "a", "tend-a"))
                : new NeedMethod(n.Id, new("rest"), "the colony is tired", id => Plan(id, Clash ? "a" : "b", "rest"))),
        ];

        private static Satisfaction Of(bool fact, bool met) =>
            fact == met ? Satisfaction.Met : Satisfaction.Unmet;

        private static PlanSpec Plan(PlanId id, string pawn, string method) =>
            new(id, new(1), [Delivery(id.Value + "-0", pawn, method)]);
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required CareFamily Family { get; init; }
        public required FamilyRunner<CareFacts> Runner { get; init; }
        public List<FamilyReadScope> Reads { get; } = [];
        public CareFacts Facts { get; set; } = new(true, false);
        public bool ImmediateReadsPawns { get; set; }
        public bool ReadFails { get; set; }

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run(bool immediate = false) =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World, Immediate = immediate }, TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run, NeedId need) => Assert.Single(run.Reports, r => r.Need == need).Verdict;
    }

    private static Colony Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-need-family-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with { Census = facts.Census with { ColonistCount = 3, IndoorSleepingCapacity = 3, BedCapacity = 3 },   FieldCoverage = 1.0};
        journal.Review(new() { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = facts }, ct);
        var family = new CareFamily();
        Colony? colony = null;
        var runner = new FamilyRunner<CareFacts>(family, (scope, _) =>
        {
            colony!.Reads.Add(scope);
            ReadNoteRules.NoteRead(FactFamily.Emergency, "test.bleeding");
            if (!scope.Immediate || colony.ImmediateReadsPawns)
            {
                ReadNoteRules.NoteRead(FactFamily.Pawns, "test.rest");
            }
            if (colony.ReadFails) throw new RequiredFactException("care");
            return Task.FromResult(colony.Facts);
        }, new FamilySchedule { Kinds = [ActionKind.PawnActivities] });
        colony = new Colony { Journal = journal, Family = family, Runner = runner };
        return colony;
    }

    private static DomainAction Delivery(string id, string pawn, string method) =>
        DomainAction.NewPawnActivitiesAction(new(id), new(1, [new(pawn, "assignment-" + id, method, 0, 1, false,
            [new PawnActivityStep(PawnActivityKind.Deliver, "stack-" + id, "", "", 1, 500).At(new(pawn[0] - 'a', 20))])]));

    [Fact]
    public void NeedsDeriveAndTheReadyOneIsAdmittedUnderTheNeedOwnerWhileThePreconditionWaits()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var run = colony.Run();

        Assert.Equal([Bleeding, Rest], run.Standings.Select(s => s.Need.Id));
        Assert.Equal(Outcome.Admitted, Colony.Report(run, Bleeding).Outcome);
        var waiting = Colony.Report(run, Rest);
        Assert.True(waiting.Is(Cause.WaitingOnNeed));
        Assert.Equal(Bleeding.Value, waiting.Refusal.Subject);
        Assert.Equal(Verdict.BuildingReasonAdmitted, run.Verdict);
        Assert.Empty(run.UndeclaredReads);

        var method = Assert.Single(run.Methods);
        Assert.Equal((Bleeding, new MethodId("tend-a"), ClaimOutcome.Admitted), (method.Need, method.Method, method.Outcome));
        var binding = colony.Journal.PlanMethod(method.Plan, ct)!;
        Assert.Equal(Bleeding.FilingKey, binding.Concern);
        Assert.Equal("tend-a", binding.Method.Value);
        var owner = colony.Journal.LoadNeed(Bleeding, World, ct);
        Assert.Equal(1, Assert.Single(owner.Methods).Attempt);
        Assert.Empty(colony.Journal.LoadNeed(Rest, World, ct).Methods);
    }

    [Fact]
    public void TheImmediatePassRunsOnlyTheSeriousHarmNeedOnItsNarrowRead()
    {
        using var colony = Open();
        var run = colony.Run(immediate: true);

        Assert.True(Assert.Single(colony.Reads).Immediate);
        Assert.Equal([Bleeding], run.Standings.Select(s => s.Need.Id));
        Assert.Equal([Bleeding], run.Reports.Select(r => r.Need));
        Assert.Empty(run.UndeclaredReads);
        Assert.Equal(Outcome.Admitted, Colony.Report(run, Bleeding).Outcome);
        Assert.Equal(run.Methods.Select(m => m.Plan), run.SeriousHarmPlans);
        Assert.Equal(run.SeriousHarmPlans, colony.Runner.SeriousHarmPlans);

        // A narrow read that reaches outside the SeriousHarm families is an audit finding.
        colony.ImmediateReadsPawns = true;
        Assert.Equal([FactFamily.Pawns], colony.Run(immediate: true).UndeclaredReads);
    }

    [Fact]
    public void AnOpenMethodContinuesAndAReleasedOneIsAdmittedAgainAsTheNextAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var first = Assert.Single(colony.Run().Methods);

        var again = colony.Run();
        Assert.Equal(Verdict.BuildingReasonExistingWork, Colony.Report(again, Bleeding));
        Assert.Equal((first.Plan, ClaimOutcome.Continued), (Assert.Single(again.Methods).Plan, Assert.Single(again.Methods).Outcome));
        Assert.Single(colony.Journal.LoadNeed(Bleeding, World, ct).Methods);

        colony.Journal.Cancel(first.Plan, new(first.Plan.Value + "-0"), ct);
        var reopened = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(reopened, Bleeding).Outcome);
        var second = Assert.Single(reopened.Methods);
        Assert.Equal(first.Method, second.Method);
        Assert.NotEqual(first.Plan, second.Plan);
        Assert.Equal(2, colony.Journal.LoadNeed(Bleeding, World, ct).Methods.Single(m => m.Plan == second.Plan).Attempt);
    }

    [Fact]
    public void AMetPreconditionReadiesTheNeedAndAnOverlappingClaimIsHeld()
    {
        using var colony = Open();
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), Bleeding).Outcome);

        // Without the precondition rest is ready, and its delivery claims pawn a, whom the bleeding Method holds.
        colony.Family.RestWaits = false;
        colony.Family.Clash = true;
        var run = colony.Run();
        var held = Colony.Report(run, Rest);
        Assert.True(held.Is(Cause.Claim));
        Assert.Equal("pawn/a", held.Refusal.Subject);

        // On its own pawn it is admitted beside the open bleeding Method.
        colony.Family.Clash = false;
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), Rest).Outcome);
    }

    [Fact]
    public void AnUnreadFactRaisesAndAMetPreconditionReleasesTheWaitingNeed()
    {
        using var colony = Open();
        colony.ReadFails = true;
        Assert.Equal("care", Assert.Throws<RequiredFactException>(() => colony.Run()).Fact);

        colony.ReadFails = false;
        colony.Facts = new(false, true);
        var run = colony.Run();
        Assert.Equal(Verdict.BuildingReasonNoDeficit, Colony.Report(run, Bleeding));
        Assert.Equal(Verdict.BuildingReasonNoDeficit, Colony.Report(run, Rest));
        Assert.Equal(Verdict.BuildingReasonNoDeficit, run.Verdict);
    }

    [Fact]
    public void NeedStandingsOrderByTierAndWaitOnAnyPreconditionNotMet()
    {
        var a = NeedId.Of("fam", "a");
        var b = NeedId.Of("fam", "b");
        var missing = NeedId.Of("other", "x");
        var standings = NeedsRules.NeedStandings("fam",
        [
            new Need { Id = a, Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet, Preconditions = [b] },
            new Need { Id = b, Tier = NeedTier.SeriousHarm, Satisfaction = Satisfaction.Met },
            new Need { Id = NeedId.Of("fam", "c"), Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet, Preconditions = [missing] },
        ]);
        Assert.Equal([NeedReadiness.Met, NeedReadiness.Waiting, NeedReadiness.Ready], standings.Select(s => s.Readiness));
        Assert.Equal(missing, standings[1].WaitingOn);

        var unmet = Satisfaction.Unmet;
        Assert.Throws<DomainException>(() => NeedsRules.NeedStandings("fam", [new Need { Id = a, Tier = NeedTier.Wellbeing, Satisfaction = unmet }, new Need { Id = a, Tier = NeedTier.Wellbeing, Satisfaction = unmet }]));
        Assert.Throws<DomainException>(() => NeedsRules.NeedStandings("other", [new Need { Id = a, Tier = NeedTier.Wellbeing, Satisfaction = unmet }]));
        Assert.Throws<DomainException>(() => NeedsRules.NeedStandings("fam", [new Need { Id = a, Tier = NeedTier.Wellbeing, Satisfaction = unmet, Preconditions = [a] }]));
    }

    [Fact]
    public void NeedIdsAreFamilyAndKindAndNeverACollidingConcernKey()
    {
        Assert.Equal(("test-care", "bleeding"), (Bleeding.Family, Bleeding.Kind));
        Assert.False(new NeedId("Test/kind").IsValid);
        Assert.False(new NeedId("fam").IsValid);
        Assert.False(new NeedId("fam/kind/sub ject").IsValid);
        Assert.False(new NeedId("fam/kind/a/b").IsValid);
        Assert.Contains('/', Bleeding.FilingKey.Value);
        Assert.Equal(Bleeding, ServiceTarget.ForNeed(Bleeding).OwnerNeed);
        Assert.Equal(default, new ServiceTarget(default, "", "").OwnerNeed);
        Assert.Equal(ServiceCoverage.Shortfall, EssentialServicesRules.NeedCoverage(new Need { Id = Rest, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet }));
        Assert.Equal(ServiceCoverage.Adequate, EssentialServicesRules.NeedCoverage(new Need { Id = Rest, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Met }));
    }

    [Fact]
    public void AFamilyIsOneCatalogEntryThatWakesOnItsFactsAndFilesUnderItsNeedIds()
    {
        var family = new CareFamily();
        var runner = new FamilyRunner<CareFacts>(family, (_, _) => Task.FromResult(new CareFacts(default, default)),
            new FamilySchedule { Kinds = [ActionKind.PawnActivities] });
        var catalog = PlannerCatalog.WithFamilies([runner]);
        var entry = Assert.Single(catalog, e => e.Family is not null);
        Assert.Equal("test-care", entry.Name);
        Assert.True(entry.Immediate);
        Assert.True(PlannerScheduler.OrdinaryPlanner(entry));
        Assert.Equal([ActionKind.PawnActivities], entry.Kinds);
        Assert.True(entry.Declared().SetEquals([FactFamily.Definitions, FactFamily.Identity, FactFamily.Pawns, FactFamily.Emergency]));
        Assert.NotEmpty(entry.Sections);
        Assert.Throws<ControlException>(() => PlannerCatalog.WithFamilies([runner, runner]));

        var row = PlannerWave.NeedDecision(entry, new NeedReport(Rest, Verdict.WaitFor(Cause.WaitingOnNeed, Bleeding.Value)), TimeSpan.Zero, false);
        Assert.Equal(("planner_step", "test-care", "waiting", "waiting_on_need"), (row.Kind, row.Target, row.Verdict, row.Reason));
        Assert.Equal(Rest.Value, row.Attrs["concern"]);
        Assert.Equal(Bleeding.Value, row.Attrs["subject"]);
    }

    [Fact]
    public void TheJournalOpensAtItsSchemaAndReplacesAnOlderFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-need-schema-" + Guid.NewGuid().ToString("N") + ".db");
        Journal.Open(path, ct).Dispose();
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var read = connection.CreateCommand();
            read.CommandText = "PRAGMA user_version";
            Assert.Equal((long)Journal.SchemaVersion, Convert.ToInt64(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            // The clock journal is gone (#3417): no clock_* table is created.
            read.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'clock\\_%' ESCAPE '\\'";
            Assert.Equal(0L, Convert.ToInt64(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            read.CommandText = $"PRAGMA user_version={Journal.SchemaVersion - 1}";
            read.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Assert.Throws<IncompatibleDatabaseException>(() => Journal.Open(path, ct));
        SqliteConnection.ClearAllPools();
        var (journal, aside) = Journal.OpenOrReplace(path, ct);
        using (journal)
        {
            Assert.NotNull(aside);
            Assert.EndsWith($"-{Journal.SchemaVersion - 1}.bak", aside);
            Assert.Empty(journal.LoadNeed(Bleeding, World, ct).Methods);
        }
    }
}
