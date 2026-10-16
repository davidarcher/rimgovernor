using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The colony admin family (#3264): admin/creep-joiners is a Need with a pure met, unmet and unknown predicate; its
/// orders are Need-owned Methods admitted by claims, one per pawn and order, and the inspection record is a
/// <c>family/admin/creep_joiners</c> record that survives a save and reload.
/// </summary>
public sealed class AdminFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private const string Inspection = "SurgicalInspection";

    private static Need Derive(CreepJoinerWork work) => Assert.Single(new AdminFamily().Derive(new AdminFacts { CreepJoiners = work }));

    private static Inspections Inspect(params string[] pawns) => new()
    {
        Orders = EquatableArray.From(pawns.Select(p => new InspectionOrder(new PawnId(p), Inspection, DomainConstants.NoSurgeryPart))),
        Record = new CreepJoinerRecord { Inspections = pawns.ToDictionary(p => new PawnId(p), _ => InspectionStage.Ordered) },
    };

    [Fact]
    public void CreepJoinersIsMetUnmetOrUnknownByTheWorkOwed()
    {
        var met = Derive(new CreepJoinerWork { Owed = Known(false) });
        Assert.Equal((Satisfaction.Met, 0.0, NeedTier.Wellbeing), (met.Satisfaction, met.Deficit.Value, met.Tier));
        Assert.Equal("admin/creep-joiners", met.Id.Value);

        var unmet = Derive(new CreepJoinerWork { Owed = Known(true), Inspections = Inspect("a", "b"), Isolation = [new IsolationMove(new("c"), "Area_1")] });
        Assert.Equal((Satisfaction.Unmet, 3.0), (unmet.Satisfaction, unmet.Deficit.Value));
        Assert.Contains(unmet.Evidence, e => e.Fact == "inspections" && e.Value.Value == "2");
        Assert.Contains(unmet.Evidence, e => e.Fact == "isolation_moves" && e.Value.Value == "1");

        // A record change alone (an ended bill) is owed work with no order.
        var recorded = Derive(new CreepJoinerWork { Owed = Known(true) });
        Assert.Equal((Satisfaction.Unmet, 0.0), (recorded.Satisfaction, recorded.Deficit.Value));

        var unknown = Derive(new CreepJoinerWork());
        Assert.Equal(Satisfaction.Unknown, unknown.Satisfaction);
        Assert.False(unknown.Deficit.IsKnown);
        Assert.Empty(new AdminFamily(creepJoiners: false).Derive(new AdminFacts()));
    }

    private static readonly AdminFamily Composed = new(new AdminNeeds { SupplySafety = true, Burial = true });

    private static StartingSupply Stack(string thing, int x, bool forbid) => new() { Thing = thing, Definition = "Steel", Cell = new(x, 1), Forbid = forbid };

    [Fact]
    public void SupplySafetyIsMetUnmetOrUnknownByTheCohort()
    {
        Need Supply(AdminFacts facts) => Assert.Single(Composed.Derive(facts), n => n.Id == AdminFamily.SupplySafety);

        var met = Supply(new AdminFacts { SupplySafety = Known(EquatableArray.From(Array.Empty<StartingSupply>())) });
        Assert.Equal((Satisfaction.Met, 0.0, NeedTier.Wellbeing, "admin/supply-safety"), (met.Satisfaction, met.Deficit.Value, met.Tier, met.Id.Value));

        var unmet = Supply(new AdminFacts { SupplySafety = Known(EquatableArray.From([Stack("s1", 1, true), Stack("s2", 2, false), Stack("s3", 3, false)])) });
        Assert.Equal((Satisfaction.Unmet, 3.0), (unmet.Satisfaction, unmet.Deficit.Value));
        Assert.Contains(unmet.Evidence, e => e.Fact == "forbid" && e.Value.Value == "1");
        Assert.Contains(unmet.Evidence, e => e.Fact == "allow" && e.Value.Value == "2");

        // An unread census (or a read that never ran) is Unknown, never met.
        Assert.Equal(Satisfaction.Unknown, Supply(new AdminFacts()).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Supply(new AdminFacts { SupplySafety = Unknown<EquatableArray<StartingSupply>>() }).Satisfaction);
    }

    [Fact]
    public void BurialIsMetUnmetOrUnknownByTheStepOwed()
    {
        Need Burial(AdminFacts facts) => Assert.Single(Composed.Derive(facts), n => n.Id == AdminFamily.Burial);

        Assert.Equal((Satisfaction.Met, 0.0, "admin/burial"), (Burial(new AdminFacts { Burial = Known(false) }).Satisfaction, Burial(new AdminFacts { Burial = Known(false) }).Deficit.Value, AdminFamily.Burial.Value));
        var unmet = Burial(new AdminFacts { Burial = Known(true) });
        Assert.Equal((Satisfaction.Unmet, 1.0, NeedTier.Wellbeing), (unmet.Satisfaction, unmet.Deficit.Value, unmet.Tier));
        Assert.Equal(Satisfaction.Unknown, Burial(new AdminFacts { Burial = Unknown<bool>() }).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Burial(new AdminFacts()).Satisfaction);
        // Only the composed Needs are derived.
        Assert.Equal([AdminFamily.SupplySafety, AdminFamily.Burial], Composed.Derive(new AdminFacts()).Select(n => n.Id));
    }

    private static PlanSpec Forbid(params StartingSupply[] stacks)
    {
        var id = PlanIds.MintPlanId();
        return new PlanSpec(id, new PlanRevision(1), [.. stacks.Select((s, i) => DomainAction.NewSupplyAllowAction(new ActionId(id.Value + "-" + i), SupplyAllow.NewSupplyForbid(s.Thing, s.Definition, s.Cell)))]);
    }

    [Fact]
    public void SupplyBatchesAreAdmittedSideBySideUnderTheNeedAndAStackTwoShareIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        BridgeFunctions.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-admin-supply-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        LootItem Unsafe(StartingSupply s) => new() { Supply = s with { Forbid = false }, Forbidden = false, SafeToHaul = false, SafetyKnown = true };
        var stacks = new[] { Stack("s1", 1, true), Stack("s2", 2, true), Stack("s3", 3, true) };
        var facts = WorkResourceTestFacts.Create("a", "b", "c") with { EventLoot = Known(EquatableArray.From(stacks.Select(Unsafe))) };
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        Assert.Equal(["s1", "s2", "s3"], journal.LoadRounds(ct).EventLoot.Pending.Select(p => p.Thing));

        var first = journal.CommitClaimedMethod(AdminFamily.SupplySafety, World, new MethodId("supply-a"), "", Forbid(stacks[0]), ct);
        var second = journal.CommitClaimedMethod(AdminFamily.SupplySafety, World, new MethodId("supply-b"), "", Forbid(stacks[1]), ct);
        Assert.Equal((ClaimOutcome.Admitted, ClaimOutcome.Admitted), (first.Outcome, second.Outcome));
        Assert.Equal(AdminFamily.SupplySafety.FilingKey, journal.PlanMethod(first.Owner.Methods[0].Plan, ct)!.Concern);

        // The same batch continues; a batch over a stack an open batch holds is held by that stack's claim.
        Assert.Equal(ClaimOutcome.Continued, journal.CommitClaimedMethod(AdminFamily.SupplySafety, World, new MethodId("supply-a"), "", Forbid(stacks[0]), ct).Outcome);
        var overlap = journal.CommitClaimedMethod(AdminFamily.SupplySafety, World, new MethodId("supply-c"), "", Forbid(stacks[0], stacks[2]), ct);
        Assert.Equal(ClaimOutcome.Conflict, overlap.Outcome);
        Assert.Equal("s1", overlap.Conflict!.Claim);

        // A stack the cohort does not list is never admitted.
        Assert.ThrowsAny<Exception>(() => journal.CommitClaimedMethod(AdminFamily.SupplySafety, World, new MethodId("supply-d"), "", Forbid(Stack("gone", 9, true)), ct));
        Assert.Equal(2, journal.LoadNeed(AdminFamily.SupplySafety, World, ct).Methods.Count);
    }

    [Fact]
    public void AWeaponDropIsHeldAsAnUnsupportedForecast()
    {
        var need = new Need { Id = AdminFamily.CreepJoiners, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet };
        var drops = new AdminFacts { CreepJoiners = new CreepJoinerWork { Owed = Known(true), Drops = [new WeaponDrop(new("a"), "Gun_1")] } };
        var held = Assert.Single(new AdminFamily().Propose([need], drops));
        Assert.Equal(AdminFamily.UnsupportedDisarm, held.Hold!.Subject);
        Assert.Empty(new AdminFamily().Propose([need], new AdminFacts { CreepJoiners = new CreepJoinerWork { Owed = Known(true), Inspections = Inspect("a") } }));
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<AdminFacts> Runner { get; init; }
        public CreepJoinerWork Work { get; set; } = new();

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run(StepArbiter? arbiter = null) =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World, Arbiter = arbiter }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run) => Assert.Single(run.Reports, r => r.Need == AdminFamily.CreepJoiners).Verdict;

        public CreepJoinerRecord Record() => Journal.LoadCreepJoiners(new(World.Colony, World.Load, World.Map), TestContext.Current.CancellationToken);

        public void File(FamilyRun run) =>
            Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [PlannerWave.NeedProgressOf(AdminFamily.FamilyId, run)!], TestContext.Current.CancellationToken);
    }

    private static Colony Open(string name = "rg-admin-")
    {
        BridgeFunctions.RegisterIntentKinds();
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), name + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<AdminFacts>(new AdminFamily(), (_, _) => Task.FromResult(new AdminFacts { CreepJoiners = colony!.Work }), AdminRounds.Schedule,
            (work, facts, call) => Task.FromResult(AdminRounds.CreepJoinerStep(colony!.Journal, work, facts.CreepJoiners!, call)));
        colony = new Colony { Journal = journal, Runner = runner };
        return colony;
    }

    [Fact]
    public void IsolationAndInspectionAreAdmittedSideBySideAndTheRecordIsWrittenWithTheOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Work = new CreepJoinerWork { Owed = Known(true), Isolation = [new IsolationMove(new("a"), "Area_1")], Inspections = Inspect("b") };
        var run = colony.Run();

        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        Assert.Equal(["creepjoiner-inspect-b", "creepjoiner-isolate-a"], run.Methods.Select(m => m.Method.Value).Order());
        Assert.All(run.Methods, m => Assert.Equal(AdminFamily.CreepJoiners.FilingKey, colony.Journal.PlanMethod(m.Plan, ct)!.Concern));
        Assert.Equal(InspectionStage.Ordered, colony.Record().Inspections[new("b")]);
        var isolate = Assert.Single(run.Methods, m => m.Method.Value == "creepjoiner-isolate-a");
        Assert.StartsWith("Creep joiners: creepjoiner isolate a", OperationIntent.Of(colony.Journal.PlanMethod(isolate.Plan, ct)!));

        // The same orders continue their open Methods.
        var again = colony.Run();
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Colony.Report(again));
        Assert.Equal(2, colony.Journal.LoadNeed(AdminFamily.CreepJoiners, World, ct).Methods.Count);

        colony.File(run);
        Assert.Contains(colony.Journal.LoadRounds(ct).Progress, p => p.Concern == AdminFamily.CreepJoiners.FilingKey);
    }

    [Fact]
    public void AnOrderOverlappingAnotherNeedsSurgeryIsHeldByItsClaimAndLeavesThePawnUnordered()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var plan = PlanIds.MintPlanId();
        var held = colony.Journal.CommitClaimedMethod(HomeUpkeepFamily.Repairs, World, new MethodId("other"), "",
            new PlanSpec(plan, new PlanRevision(1), [DomainAction.NewSurgeryAction(new(plan.Value + "-0"), new Surgery(new("b"), Inspection, DomainConstants.NoSurgeryPart, false))]), ct);
        Assert.Equal(ClaimOutcome.Admitted, held.Outcome);

        colony.Work = new CreepJoinerWork { Owed = Known(true), Inspections = Inspect("b") };
        var verdict = Colony.Report(colony.Run());
        Assert.True(verdict.Is(Cause.Claim));
        Assert.Equal($"surgery/b/{Inspection}/-1", verdict.Refusal.Subject);
        Assert.Empty(colony.Journal.LoadNeed(AdminFamily.CreepJoiners, World, ct).Methods);
        Assert.Empty(colony.Record().Inspections);
    }

    [Fact]
    public void APawnClaimedElsewhereInTheStepGetsNoOrderAndAnEndedBillIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var arbiter = new StepArbiter();
        Assert.True(arbiter.TryClaim([new PawnId("b")]));
        // b's order cannot claim the pawn; c's inspection ended since the stored record.
        colony.Journal.SaveCreepJoiners(new(World.Colony, World.Load, World.Map), new CreepJoinerRecord { Inspections = new Dictionary<PawnId, InspectionStage> { [new("c")] = InspectionStage.Ordered } }, ct);
        var inspections = Inspect("b") with
        {
            Record = new CreepJoinerRecord { Inspections = new Dictionary<PawnId, InspectionStage> { [new("b")] = InspectionStage.Ordered, [new("c")] = InspectionStage.Done } },
        };
        colony.Work = new CreepJoinerWork { Owed = Known(true), Inspections = inspections, Record = colony.Record() };
        Assert.Equal(Verdict.WaitFor(Cause.MethodUsed, "creepjoiner_order_recorded"), Colony.Report(colony.Run(arbiter)));
        Assert.Equal(new Dictionary<PawnId, InspectionStage> { [new("c")] = InspectionStage.Done }, colony.Record().Inspections);
    }

    [Fact]
    public void TheInspectionRecordIsReadBackAfterASaveAndReload()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new Store.World(World.Colony, World.Load, World.Map);
        var record = new CreepJoinerRecord { Inspections = new Dictionary<PawnId, InspectionStage> { [new("a")] = InspectionStage.Ordered, [new("b")] = InspectionStage.Done } };
        IReadOnlyDictionary<string, string> saved;
        using (var source = Open("rg-admin-save-"))
        {
            source.Journal.SaveCreepJoiners(world, record, ct);
            saved = source.Journal.GovernorStateBlobs(ct);
        }
        Assert.Contains(Journal.GovernorCreepJoinersKey, saved.Keys);

        using var loaded = Open("rg-admin-load-");
        loaded.Journal.RebuildFamilies(saved, ct);
        loaded.Journal.ResetRounds(ct);
        // The reload's load token differs; the same colony and map read the record back.
        var reloaded = loaded.Journal.LoadCreepJoiners(world with { Load = new("load-2") }, ct);
        Assert.Equal(record.Encode(), reloaded.Encode());
        Assert.Empty(loaded.Journal.LoadCreepJoiners(world with { Colony = new("other") }, ct).Inspections);

        // A save without the blob leaves no record.
        loaded.Journal.RebuildFamilies(new Dictionary<string, string>(), ct);
        Assert.Empty(loaded.Journal.LoadCreepJoiners(world, ct).Inspections);
    }
}
