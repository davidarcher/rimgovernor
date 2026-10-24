using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The biotech and royalty families (#3261): Needs with pure met, unmet and unknown predicates; the charger as the
/// mechs Need's precondition; the pollution batch and its wastepack allows admitted from the review's cohort; permit Methods admitted side by side for two
/// holders and refused on one holder's settings; the psylink hold; the families as one due-queue entry each.
/// </summary>
public sealed class BiotechRoyaltyFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly BiotechNeeds AllBiotech = new() { Mechs = true, MechCharger = true, GeneBank = true, Pollution = true };

    // Only the Need under test is composed: the family raises on the first composed Need whose fact is unread.
    private static Need Biotech(BiotechFacts facts, NeedId id, BiotechNeeds? needs = null) =>
        Assert.Single(new BiotechFamily(needs ?? new BiotechNeeds
        {
            Mechs = id == BiotechFamily.Mechs,
            MechCharger = id == BiotechFamily.MechCharger,
            GeneBank = id == BiotechFamily.GeneBank,
            Pollution = id == BiotechFamily.Pollution,
        }).Derive(facts with { Installed = true }), n => n.Id == id);

    private static Need Royalty(RoyaltyFamilyFacts facts, NeedId id) =>
        Assert.Single(new RoyaltyFamily(id == RoyaltyFamily.Permits, id == RoyaltyFamily.Psylink).Derive(facts with { Installed = true }));

    [Fact]
    public void WithoutTheExpansionBiotechAndRoyaltyDeriveNothing()
    {
        Assert.Empty(new BiotechFamily(AllBiotech).Derive(new BiotechFacts()));
        Assert.Empty(new RoyaltyFamily().Derive(new RoyaltyFamilyFacts()));
        Assert.Empty(new IdeologyFamily().Derive(new IdeologyFacts()));
        Assert.False(new RoundsFacts().AnomalyInstalled);
        Assert.Empty(new AnomalyFamily().Derive(new AnomalyFacts()));
    }

    [Theory]
    [InlineData("mech-charger")]
    [InlineData("mechs")]
    [InlineData("gene-bank")]
    public void TheOwedNeedsAreMetUnmetOrUnknownByTheirOwedFact(string kind)
    {
        var id = NeedId.Of(BiotechFamily.FamilyId, kind);
        BiotechFacts With(Fact<bool> owed) => kind switch
        {
            "mech-charger" => new BiotechFacts { MechChargerOwed = owed },
            "mechs" => new BiotechFacts { MechsOwed = owed },
            _ => new BiotechFacts { GeneBankOwed = owed },
        };
        var owed = Biotech(With(Known(true)), id);
        Assert.Equal((Satisfaction.Unmet, NeedTier.Development, 1.0), (owed.Satisfaction, owed.Tier, owed.Deficit.Value));
        Assert.Equal(Satisfaction.Met, Biotech(With(Known(false)), id).Satisfaction);
        var fact = kind switch { "mech-charger" => "mech_charger_owed", "mechs" => "mech_owed", _ => "gene_bank_owed" };
        Assert.Equal(fact, Assert.Throws<RequiredFactException>(() => Biotech(With(Unknown<bool>()), id)).Fact);
    }

    [Fact]
    public void MechsWaitOnTheChargerWhenItIsComposed()
    {
        var settled = Polluted(0) with { MechsOwed = Known(false), MechChargerOwed = Known(false), GeneBankOwed = Known(false) };
        var needs = new BiotechFamily(AllBiotech).Derive(settled);
        Assert.Equal([BiotechFamily.MechCharger], Assert.Single(needs, n => n.Id == BiotechFamily.Mechs).Preconditions);
        Assert.All(needs.Where(n => n.Id != BiotechFamily.Mechs), n => Assert.Empty(n.Preconditions));
        Assert.Empty(Biotech(settled, BiotechFamily.Mechs, new BiotechNeeds { Mechs = true }).Preconditions);

        // The gestation bill is declared only while the mechs Need is ready: owed, with no charger owed.
        var family = new BiotechFamily(AllBiotech);
        Assert.True(family.MechsReady(new BiotechFacts { MechsOwed = Known(true), MechChargerOwed = Known(false) }));
        Assert.False(family.MechsReady(new BiotechFacts { MechsOwed = Known(true), MechChargerOwed = Known(true) }));
        Assert.Equal("mech_charger_owed", Assert.Throws<RequiredFactException>(() => family.MechsReady(new BiotechFacts { MechsOwed = Known(true), MechChargerOwed = Unknown<bool>() })).Fact);
        Assert.False(family.MechsReady(new BiotechFacts { MechsOwed = Known(false), MechChargerOwed = Known(false) }));
        Assert.True(new BiotechFamily(new BiotechNeeds { Mechs = true }).MechsReady(new BiotechFacts { MechsOwed = Known(true) }));
    }

    private static Wastepack Pack(string id, bool forbidden = false) => new()
    {
        Id = id,
        Definition = "Wastepack",
        Cell = new Cell(1, 2),
        Frozen = Known(false),
        InAtomizer = Known(false),
        Forbidden = Known(forbidden),
    };

    private static BiotechFacts Polluted(uint uncovered, params Wastepack[] packs) =>
        new() { Installed = true, Pollution = Known(new PollutionFacts { Wastepacks = packs, UncoveredCells = Known(uncovered) }), PollutedCells = [new Cell(3, 4)] };

    [Fact]
    public void PollutionIsMetUnmetOrUnknownAndItsMethodNamesItsWork()
    {
        Assert.Equal((Satisfaction.Met, NeedTier.Wellbeing), (Biotech(Polluted(0), BiotechFamily.Pollution).Satisfaction, Biotech(Polluted(0), BiotechFamily.Pollution).Tier));
        Assert.Equal("pollution census", Assert.Throws<RequiredFactException>(() => Biotech(new BiotechFacts(), BiotechFamily.Pollution)).Fact);
        Assert.Equal("pollution census", Assert.Throws<RequiredFactException>(() => Biotech(Polluted(0, Pack("w1") with { Frozen = Unknown<bool>() }), BiotechFamily.Pollution)).Fact);

        var owed = Polluted(2, Pack("w1"));
        var need = Biotech(owed, BiotechFamily.Pollution);
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
        var method = Assert.Single(new BiotechFamily(AllBiotech).Propose([need], owed));
        Assert.StartsWith("pollution-", method.Method.Value, StringComparison.Ordinal);
        var plan = method.Plan(new PlanId("p"));
        Assert.Equal([ActionKind.Area, ActionKind.WastepackHaul], plan.Actions.Select(a => a.Kind));
        // The same work names the same Method; a refused pack drops out of the batch and renames it.
        Assert.Equal(method.Method, Assert.Single(new BiotechFamily(AllBiotech).Propose([need], owed)).Method);
        var refused = owed with { PollutionRefused = ["w1"] };
        var rest = Assert.Single(new BiotechFamily(AllBiotech).Propose([need], refused));
        Assert.NotEqual(method.Method, rest.Method);
        Assert.Equal([ActionKind.Area], rest.Plan(new PlanId("p")).Actions.Select(a => a.Kind));

        // A forbidden pack is allowed first (named in the Method's hash), not hauled in the same batch.
        var forbidden = Polluted(0, Pack("w2", forbidden: true));
        var allow = Assert.Single(new BiotechFamily(AllBiotech).Propose([Biotech(forbidden, BiotechFamily.Pollution)], forbidden));
        Assert.Null(allow.Hold);
        var supply = Assert.Single(allow.Plan(new PlanId("p")).Actions).SupplyAllow!;
        Assert.Equal(("w2", "Wastepack", new Cell(1, 2), false), (supply.Thing, supply.Definition, supply.Cell, supply.Forbidden));
        Assert.NotEqual(allow.Method, Assert.Single(new BiotechFamily(AllBiotech).Propose([need], Polluted(0, Pack("w3", forbidden: true)))).Method);
    }

    [Fact]
    public void AForbiddenWastepacksAllowIsAdmittedFromTheReviewCohortAndItsClaimRefusesAnOverlap()
    {
        var ct = TestContext.Current.CancellationToken;
        var forbidden = Polluted(0, Pack("w2", forbidden: true));
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-pollution-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var review = journal.ReviewRounds(new RoundsRequest
        {
            Current = World,
            Tick = new(100),
            Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(),
            Facts = WorkResourceTestFacts.Create("a", "b") with { Pollution = forbidden.Pollution },
        }, ct).Review;
        Assert.Equal("w2", Assert.Single(review.PollutionSupplies).Thing);

        var facts = forbidden;
        var runner = new FamilyRunner<BiotechFacts>(new BiotechFamily(new BiotechNeeds { Pollution = true }), (_, _) => Task.FromResult(facts), BiotechRounds.Schedule);
        FamilyRun Run() => runner.RunAsync(new FamilyStep { Journal = journal, World = World }, ct).GetAwaiter().GetResult();
        var first = Run();
        Assert.Equal(Outcome.Admitted, Assert.Single(first.Reports, r => r.Need == BiotechFamily.Pollution).Verdict.Outcome);
        var plan = journal.LoadPlan(Assert.Single(first.Methods).Plan, ct);
        Assert.Equal(ActionKind.SupplyAllow, Assert.Single(plan.Progress).Action.Kind);
        Assert.Contains(ActionKind.SupplyAllow, BiotechRounds.Schedule.Kinds);

        // New work that allows the same pack again overlaps the open Method's claim on it and is refused.
        facts = Polluted(2, Pack("w2", forbidden: true));
        var held = Assert.Single(Run().Reports, r => r.Need == BiotechFamily.Pollution).Verdict;
        Assert.True(held.Is(Cause.Claim));
        Assert.Equal("w2", held.Refusal.Subject);
        Assert.Single(journal.LoadNeed(BiotechFamily.Pollution, World, ct).Methods);
    }

    private static RoyaltyFacts Titled(params string[] holders) => TitledWith("CallAid", PermitChoiceRules.PermitWorkerCallAid, holders);

    private static RoyaltyFacts TitledWith(string permit, string worker, params string[] holders) => new()
    {
        Ladder = [new RoyalRung { Title = "Knight", Seniority = Known(100) }],
        Permits = new Dictionary<string, RoyalPermit>
        {
            [permit] = new()
            {
                Name = permit,
                MinTitle = Known("Knight"),
                PermitPoints = Known(1),
                Acts = Known(true),
                FavorCost = Known(4),
                Worker = worker,
            },
        },
        Holders = holders.ToDictionary(h => new PawnId(h), _ => (EquatableArray<RoyalHolding>)[new RoyalHolding
        {
            FactionDef = "Empire",
            Title = "Knight",
            PermitPoints = Known(1),
            Permits = [],
        }]),
    };

    [Fact]
    public void PermitsAreMetUnmetOrUnknownByWhetherAPermitIsTakeable()
    {
        var owed = Royalty(new RoyaltyFamilyFacts { Royalty = Known(Titled("a")) }, RoyaltyFamily.Permits);
        Assert.Equal((Satisfaction.Unmet, NeedTier.Development), (owed.Satisfaction, owed.Tier));
        Assert.Equal(Satisfaction.Met, Royalty(new RoyaltyFamilyFacts { Royalty = Known(new RoyaltyFacts()) }, RoyaltyFamily.Permits).Satisfaction);
        Assert.Equal("royalty", Assert.Throws<RequiredFactException>(() => Royalty(new RoyaltyFamilyFacts(), RoyaltyFamily.Permits)).Fact);

        var method = Assert.Single(new RoyaltyFamily().Propose([owed], new RoyaltyFamilyFacts { Royalty = Known(Titled("a")) }));
        Assert.Equal("permit-a-CallAid", method.Method.Value);
        var setting = Assert.Single(method.Plan(new PlanId("p")).Actions).PawnSettings!;
        Assert.Equal(new PawnId("a"), setting.Pawn);
    }

    [Fact]
    public void PsylinkIsMetUnmetOrUnknownAndHeldWhileOwed()
    {
        var owed = Royalty(new RoyaltyFamilyFacts { PsylinkOwed = Known(true) }, RoyaltyFamily.Psylink);
        Assert.Equal(Satisfaction.Unmet, owed.Satisfaction);
        Assert.Equal(Satisfaction.Met, Royalty(new RoyaltyFamilyFacts { PsylinkOwed = Known(false) }, RoyaltyFamily.Psylink).Satisfaction);
        Assert.Equal("psylink owed", Assert.Throws<RequiredFactException>(() => Royalty(new RoyaltyFamilyFacts(), RoyaltyFamily.Psylink)).Fact);
        var held = Assert.Single(new RoyaltyFamily().Propose([owed], new RoyaltyFamilyFacts()));
        Assert.Equal(RoyaltyFamily.UnsupportedNeuroformer, held.Hold!.Subject);
        var settled = new RoyaltyFamilyFacts { Installed = true, Royalty = Known(new RoyaltyFacts()), PsylinkOwed = Known(false) };
        Assert.Equal(2, new RoyaltyFamily().Derive(settled).Count);
        Assert.DoesNotContain(new RoyaltyFamily(true, false).Derive(settled), n => n.Id == RoyaltyFamily.Psylink);
        Assert.DoesNotContain(new RoyaltyFamily(false, true).Derive(settled), n => n.Id == RoyaltyFamily.Permits);
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<RoyaltyFamilyFacts> Runner { get; init; }
        public RoyaltyFamilyFacts Facts { get; set; } = new();

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public ConcernProgress? Row(NeedId need) => Journal.LoadRounds(TestContext.Current.CancellationToken).Progress.SingleOrDefault(p => p.Concern == need.FilingKey);
    }

    private static Colony Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-royalty-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<RoyaltyFamilyFacts>(new RoyaltyFamily(true, false), (_, _) => Task.FromResult(colony!.Facts), RoyaltyRounds.Schedule);
        colony = new Colony { Journal = journal, Runner = runner };
        return colony;
    }

    private static RoyaltyFamilyFacts Permit(RoyaltyFacts royalty) => new() { Installed = true, Royalty = Known(royalty) };

    [Fact]
    public void PermitMethodsRunSideBySideAndAHoldersSecondPermitIsHeldByItsClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Permit(Titled("a"));
        var first = colony.Run();
        Assert.Equal(Outcome.Admitted, Assert.Single(first.Reports, r => r.Need == RoyaltyFamily.Permits).Verdict.Outcome);
        Assert.StartsWith("Permits", OperationIntent.Of(colony.Journal.PlanMethod(Assert.Single(first.Methods).Plan, ct)!), StringComparison.Ordinal);

        // Another holder's permit claims another pawn's settings and runs beside the open one.
        colony.Facts = Permit(Titled("b"));
        Assert.Equal(Outcome.Admitted, Assert.Single(colony.Run().Reports, r => r.Need == RoyaltyFamily.Permits).Verdict.Outcome);
        Assert.Equal(2, colony.Journal.LoadNeed(RoyaltyFamily.Permits, World, ct).Methods.Count);

        // A second permit for the first holder overlaps its open Method's settings claim and is refused.
        colony.Facts = Permit(TitledWith("CallLaborers", PermitChoiceRules.PermitWorkerCallLaborers, "a"));
        var held = Assert.Single(colony.Run().Reports, r => r.Need == RoyaltyFamily.Permits).Verdict;
        Assert.True(held.Is(Cause.Claim));
        Assert.Equal("settings/a", held.Refusal.Subject);
        Assert.Equal(2, colony.Journal.LoadNeed(RoyaltyFamily.Permits, World, ct).Methods.Count);
    }

    [Fact]
    public void NeedRowsReachRoundsProgress()
    {
        using var colony = Open();
        colony.Facts = Permit(Titled("a"));
        colony.Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [PlannerWave.NeedProgressOf("royalty", colony.Run())!], TestContext.Current.CancellationToken);
        var row = colony.Row(RoyaltyFamily.Permits)!;
        Assert.Equal(("permit-a-CallAid", true), (row.Method, row.Open));
    }

    [Fact]
    public void EachFamilyIsOneDueQueueEntryAndTheOldPlannersAreGone()
    {
        var biotech = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<BiotechFacts>(new BiotechFamily(AllBiotech), (_, _) => Task.FromResult(new BiotechFacts()), BiotechRounds.Schedule));
        Assert.Equal("biotech", biotech.Name);
        Assert.Contains(ActionKind.Building, biotech.Kinds);
        Assert.Contains(ActionKind.Area, biotech.Kinds);
        Assert.Contains(ActionKind.WastepackHaul, biotech.Kinds);

        var royalty = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<RoyaltyFamilyFacts>(new RoyaltyFamily(), (_, _) => Task.FromResult(new RoyaltyFamilyFacts()), RoyaltyRounds.Schedule));
        Assert.Equal("royalty", royalty.Name);
        Assert.Contains(ActionKind.PawnSettings, royalty.Kinds);

        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name is "pollution" or "mechcharger" or "genebank" or "psylink" or "permits");
    }

    [Fact]
    public void PollutionWorkClaimsItsAreaAndPacks()
    {
        var owed = Polluted(2, Pack("w1"));
        var plan = Assert.Single(new BiotechFamily(AllBiotech).Propose([Biotech(owed, BiotechFamily.Pollution)], owed)).Plan(new PlanId("p"));
        var claims = MethodClaimsRules.MethodClaimsOf(new WorkAdmissionEvidence(), [.. plan.Actions.Select(a => new WorkAction(plan.Id, a, new ProgressView { Action = a.Id, Stage = Stage.Pending }))]);
        Assert.Contains("w1", claims.Targets);
        Assert.Contains(claims.Targets, t => t.StartsWith("area/pollution", StringComparison.Ordinal));
    }
}
