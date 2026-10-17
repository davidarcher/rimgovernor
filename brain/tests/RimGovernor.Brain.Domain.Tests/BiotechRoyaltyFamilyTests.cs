using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The biotech and royalty families (#3261): Needs with pure met, unmet and unknown predicates; the charger as the
/// mechs Need's precondition; the pollution batch and its allow hold; permit Methods admitted side by side for two
/// holders and refused on one holder's settings; the psylink hold; the families as one due-queue entry each.
/// </summary>
public sealed class BiotechRoyaltyFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly BiotechNeeds AllBiotech = new() { Mechs = true, MechCharger = true, GeneBank = true, Pollution = true };

    private static Need Biotech(BiotechFacts facts, NeedId id, BiotechNeeds? needs = null) =>
        Assert.Single(new BiotechFamily(needs ?? AllBiotech).Derive(facts), n => n.Id == id);

    private static Need Royalty(RoyaltyFamilyFacts facts, NeedId id) => Assert.Single(new RoyaltyFamily().Derive(facts), n => n.Id == id);

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
        var unknown = Biotech(With(Unknown<bool>()), id);
        Assert.Equal(Satisfaction.Unknown, unknown.Satisfaction);
        Assert.False(unknown.Deficit.IsKnown);
        Assert.NotEmpty(owed.Evidence);
    }

    [Fact]
    public void MechsWaitOnTheChargerWhenItIsComposed()
    {
        var needs = new BiotechFamily(AllBiotech).Derive(new BiotechFacts());
        Assert.Equal([BiotechFamily.MechCharger], Assert.Single(needs, n => n.Id == BiotechFamily.Mechs).Preconditions);
        Assert.All(needs.Where(n => n.Id != BiotechFamily.Mechs), n => Assert.Empty(n.Preconditions));
        Assert.Empty(Biotech(new BiotechFacts(), BiotechFamily.Mechs, new BiotechNeeds { Mechs = true }).Preconditions);

        // The gestation bill is declared only while the mechs Need is ready: owed, with no charger owed.
        var family = new BiotechFamily(AllBiotech);
        Assert.True(family.MechsReady(new BiotechFacts { MechsOwed = Known(true), MechChargerOwed = Known(false) }));
        Assert.False(family.MechsReady(new BiotechFacts { MechsOwed = Known(true), MechChargerOwed = Known(true) }));
        Assert.False(family.MechsReady(new BiotechFacts { MechsOwed = Known(true), MechChargerOwed = Unknown<bool>() }));
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
        new() { Pollution = Known(new PollutionFacts { Wastepacks = packs, UncoveredCells = Known(uncovered) }), PollutedCells = [new Cell(3, 4)] };

    [Fact]
    public void PollutionIsMetUnmetOrUnknownAndItsMethodNamesItsWork()
    {
        Assert.Equal((Satisfaction.Met, NeedTier.Wellbeing), (Biotech(Polluted(0), BiotechFamily.Pollution).Satisfaction, Biotech(Polluted(0), BiotechFamily.Pollution).Tier));
        Assert.Equal(Satisfaction.Unknown, Biotech(new BiotechFacts(), BiotechFamily.Pollution).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Biotech(Polluted(0, Pack("w1") with { Frozen = Unknown<bool>() }), BiotechFamily.Pollution).Satisfaction);

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

        // A forbidden pack's allow is not admitted for the Need: it is held, not committed.
        var forbidden = Polluted(0, Pack("w2", forbidden: true));
        var held = Assert.Single(new BiotechFamily(AllBiotech).Propose([Biotech(forbidden, BiotechFamily.Pollution)], forbidden));
        Assert.Equal(BiotechFamily.UnsupportedWastepackAllow, held.Hold!.Subject);
    }

    private static RoyaltyFacts Titled(params string[] holders) => TitledWith("CallAid", PolicyConstants.PermitWorkerCallAid, holders);

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
        Assert.Contains(owed.Evidence, e => e.Fact == "next_permit" && e.Value.Value == "a/CallAid");
        Assert.Equal(Satisfaction.Met, Royalty(new RoyaltyFamilyFacts { Royalty = Known(new RoyaltyFacts()) }, RoyaltyFamily.Permits).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Royalty(new RoyaltyFamilyFacts(), RoyaltyFamily.Permits).Satisfaction);

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
        Assert.Equal(Satisfaction.Unknown, Royalty(new RoyaltyFamilyFacts(), RoyaltyFamily.Psylink).Satisfaction);
        var held = Assert.Single(new RoyaltyFamily().Propose([owed], new RoyaltyFamilyFacts()));
        Assert.Equal(RoyaltyFamily.UnsupportedNeuroformer, held.Hold!.Subject);
        Assert.DoesNotContain(new RoyaltyFamily(true, false).Derive(new RoyaltyFamilyFacts()), n => n.Id == RoyaltyFamily.Psylink);
        Assert.DoesNotContain(new RoyaltyFamily(false, true).Derive(new RoyaltyFamilyFacts()), n => n.Id == RoyaltyFamily.Permits);
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

    private static RoyaltyFamilyFacts Permit(RoyaltyFacts royalty) => new() { Royalty = Known(royalty) };

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
        colony.Facts = Permit(TitledWith("CallLaborers", PolicyConstants.PermitWorkerCallLaborers, "a"));
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
