using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Ex = RimGovernor.Brain.Executor;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared integration rig of task 9.1 (redefine-habitat-requirements): one real journal on a temp database driven
/// through the cycle a normal review runs — stage a review over the native's facts, commit competing Needs' Methods
/// through claim admission (<see cref="Journal.CommitClaimedMethod"/>), dispatch and receipt their orders (Hands with
/// <see cref="PowerRestorationTests.Boundary"/> as the fake native, which records every order it accepts), stage the next
/// review over mutated facts, save the governor state and load it the way the host's world rebuild does (retire every open
/// plan, rebuild the family tables from the save, reset the review), then run again. The power family runs through its
/// real <see cref="FamilyRunner{TFacts}"/> on the same journal. Everything else is the production code; nothing here
/// decides a claim, a release or a binding.
/// </summary>
internal sealed class ColonyReviewRig : IDisposable
{
    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static readonly GenerationSnapshot First = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    /// <summary>The footprints and costs the review prices (WorkAdmission.Buildings): a claim is a whole footprint.</summary>
    internal const string Bed = "TestBed", HospitalBed = "TestHospitalBed", Generator = "WoodFiredGenerator", Sandbags = "Sandbags", Wall = "TestWall";

    internal const string Bench = PlannedRoomSelectionTests.HostBench;

    internal static readonly string[] Pawns = ["a", "b", "doc"];

    private static readonly HashSet<string> Stuffless = [Generator, Sandbags, SleepingRules.SleepingSpotDefinition];

    public Journal Journal { get; }

    public GenerationSnapshot World { get; private set; } = First;

    public Tick Tick { get; private set; } = new(100);

    public ReviewFacts Facts { get; private set; }

    public ReviewResult Review { get; private set; } = null!;

    /// <summary>The power family's facts the fake native answers; its servicing history is re-read from the journal as the reader does.</summary>
    public PowerFacts Power { get; set; } = new();

    public PowerRestorationTests.Boundary Native { get; } = new();

    private ulong _revision;

    private ColonyReviewRig(Journal journal, ReviewFacts facts)
    {
        Journal = journal;
        Facts = facts;
    }

    /// <summary>A colony of <see cref="Pawns"/> with <paramref name="rice"/> rice and <paramref name="steel"/> steel, whose review prices every footprint used here.</summary>
    internal static ReviewFacts Colony(int steel = 100, int rice = 100)
    {
        var facts = WorkResourceTestFacts.Create(Pawns);
        return facts with
        {
            Census = facts.Census with
            {
                Resources = [new Quantity { DefName = "Rice", Units = rice }, new Quantity { DefName = "Steel", Units = steel }],
            },
            // Every colonist can build (LayoutClusterRules.BuildersOf), so a claimed one is the only thing that takes a builder away.
            ForecastObserved = facts.ForecastObserved with { WorkPawns = new(EquatableArray.From(facts.ForecastObserved.WorkPawns.Pawns.Select(p => p with
            {
                Work = PawnFixtures.WorkTable(true, [WorkPriority.Planned(WorkType.Construction, 1) with { Skill = "Construction", Order = 5 }]),
                Observed = p.Observed with { Biography = p.Observed.Biography with { Skills = [new() { DefName = "Construction", Level = 8, Passion = Passion.None }] } },
            }))) },
            WorkAdmission = facts.WorkAdmission with
            {
                Buildings =
                [
                    new(Wall, "Rice", [new(new("Rice"), 6)]) { Size = new Cell(1, 1) },
                    new(Bed, "Rice", [new(new("Rice"), 1)]) { Size = new Cell(1, 2) },
                    new(HospitalBed, "Rice", [new(new("Rice"), 1)]) { Size = new Cell(1, 2) },
                    new(Bench, "Rice", [new(new("Rice"), 1), new(new("Steel"), 6)]) { Size = new Cell(3, 1) },
                    new(Generator, "", [new(new("Steel"), 6)]) { Size = new Cell(1, 1) },
                    new(Sandbags, "", [new(new("Rice"), 1)]) { Size = new Cell(1, 1) },
                    new(SleepingRules.SleepingSpotDefinition, "", []) { Size = new Cell(1, 2) },
                ],
            },
        };
    }

    internal static ColonyReviewRig Open(ReviewFacts? facts = null)
    {
        IntentActionRules.RegisterIntentKinds();
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-colony-review-" + Guid.NewGuid().ToString("N") + ".db"), Ct);
        var rig = new ColonyReviewRig(journal, facts ?? Colony());
        rig.Stage(new Tick(100));
        return rig;
    }

    /// <summary>Stages review N+1 at <paramref name="tick"/> over the current facts, after <paramref name="mutate"/> changes them.</summary>
    public ReviewResult Stage(Tick tick, Func<ReviewFacts, ReviewFacts>? mutate = null)
    {
        Tick = tick;
        Facts = (mutate ?? (f => f))(Facts) with { Tick = tick };
        Review = Journal.Review(new ReviewRequest { Revision = _revision, Current = World, Tick = tick, Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = Facts }, Ct);
        _revision = Review.Review.Revision;
        return Review;
    }

    /// <summary>The facts the staged review completed (its work view and sleeping review among them).</summary>
    public ReviewFacts Reviewed => Review.Detection!.Facts;

    /// <summary>The open work's claims the staged review read from the journal.</summary>
    public WorkResourceClaims Claims => Reviewed.WorkView!.Claims;

    /// <summary>Commits <paramref name="actions"/> as <paramref name="method"/> under <paramref name="need"/> through claim admission.</summary>
    public NeedCommit Commit(NeedId need, string method, params DomainAction[] actions) =>
        Journal.CommitClaimedMethod(need, World, new(method), "", new PlanSpec(new(method + "-plan-" + Guid.NewGuid().ToString("N")[..8]), new(1), EquatableArray.From(actions)), Ct);

    /// <summary>The plan the Need's admitted <paramref name="method"/> runs.</summary>
    public PlanId PlanOf(NeedId need, string method) =>
        Assert.Single(Journal.LoadNeed(need, World, Ct).Methods, m => m.Method.Value == method).Plan;

    /// <summary>Prepares and dispatches <paramref name="action"/> without a receipt yet (the native has not answered).</summary>
    public AttemptId Dispatch(PlanId plan, ActionId action)
    {
        var scope = World with { Plan = plan, Revision = new(1) };
        _ = Journal.Prepare(plan, action, scope, Tick, Ct);
        return Journal.Dispatch(plan, action, scope, Tick, Ct).View.Attempt;
    }

    /// <summary>The native accepts the dispatched order.</summary>
    public Progress Accept(PlanId plan, ActionId action) => Journal.RecordReceipt(plan, action, Dispatch(plan, action), Receipt.Accepted, Ct);

    /// <summary>The native refuses the dispatched order permanently, naming why.</summary>
    public BatchResult Refuse(PlanId plan, ActionId action, string reason = "no path") =>
        Receive(plan, action, Dispatch(plan, action), Receipt.Refused, reason);

    /// <summary>A receipt for <paramref name="attempt"/>, as Hands records one the native returned.</summary>
    public BatchResult Receive(PlanId plan, ActionId action, AttemptId attempt, Receipt receipt, string reason = "no path") =>
        Assert.Single(Journal.RecordReceipts([new BatchReceipt(plan, action, attempt, receipt)
            { Refusal = receipt == Receipt.Refused ? new NativeRefusal("refused", reason, RefusalClass.Permanent) : null }], Ct));

    /// <summary>Runs <paramref name="plan"/>'s <paramref name="actions"/> through Hands to the fake native.</summary>
    public async Task HandsAsync(PlanId plan, params ActionId[] actions)
    {
        using var hands = new Ex.Executor(new ExecutorJournal(Journal), Native, new PowerRestorationTests.Clock(), new Ex.Limits
            { MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10), JournalTimeout = TimeSpan.FromSeconds(10) },
            new PlanAuthorizer(Journal, true));
        hands.UpdateAuthority(new() { Snapshot = World, Enabled = true });
        await hands.RunBatchAsync(plan, actions, Ct);
    }

    /// <summary>The power facts with the servicing history the journal holds, as the home-upkeep reader derives it.</summary>
    public PowerFacts ObservedPower()
    {
        var (underway, refused) = HomeUpkeepReader.ServicingHistory(Journal, World, PowerFamily.Basic, [PowerRestorationRules.RefuelMethod], [],
            [(HomeUpkeepReader.RefuelJob, ServicingKind.Refuel)], Ct);
        var r = Power.Restoration;
        return Power with { Restoration = r with { Underway = [.. underway, .. r.Underway], Refused = [.. refused] } };
    }

    /// <summary>One run of the real power family on this journal.</summary>
    public FamilyRun RunPower() =>
        new FamilyRunner<PowerFacts>(new PowerFamily(), (_, _) => Task.FromResult(ObservedPower()),
                new FamilySchedule { Kinds = [ActionKind.RecoveryService, ActionKind.Acquisition] })
            .RunAsync(new FamilyStep { Journal = Journal, World = World }, Ct).GetAwaiter().GetResult();

    /// <summary>Records the held layout plan (the review's survey writes it).</summary>
    public void Hold(LayoutPlan plan) => Journal.RecordLayoutPlan(World, Tick, plan, Ct);

    /// <summary>The held layout plan the next review reads.</summary>
    public LayoutPlan Held() => Assert.IsType<LayoutPlan>(Journal.LayoutPlan(World, Tick, Ct)?.Plan);

    /// <summary>The governor state the save carries.</summary>
    public IReadOnlyDictionary<string, string> Save() => new Dictionary<string, string>(Journal.GovernorStateBlobs(Ct), StringComparer.Ordinal);

    /// <summary>
    /// Loads <paramref name="saved"/> as a new world (<paramref name="load"/>), in the host's world-rebuild order: every open
    /// plan retires, the family tables are rebuilt from the save and the review resets; then the next review is staged.
    /// </summary>
    public void Load(IReadOnlyDictionary<string, string> saved, string load, Tick tick)
    {
        Journal.RebuildOwners(null, Ct);
        Journal.RebuildFamilies(saved, Ct);
        Journal.ResetReview(Ct);
        _revision = 0;
        World = World with { Load = new(load) };
        Stage(tick);
    }

    /// <summary>A building order at <paramref name="x"/>,<paramref name="z"/> facing north in the stuff the review prices it in.</summary>
    internal static DomainAction Build(string id, string definition, int x, int z) =>
        DomainAction.NewBuildingAction(new(id), new(definition, new(x, z), Rotation.North, Stuffless.Contains(definition) ? "" : "Rice"), ConstructionTier.Survive);

    /// <summary>The cells a building order claims, as the review's own claims read them.</summary>
    public IReadOnlySet<string> Footprint(DomainAction build) =>
        MethodClaimsRules.BuildingClaimTargets(Review.Review.WorkEvidence!, build.Building!).ToHashSet(StringComparer.Ordinal);

    public void Dispose() => Journal.Dispose();
}
