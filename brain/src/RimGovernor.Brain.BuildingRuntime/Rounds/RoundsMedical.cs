using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The medical family's reserves work and its critical Needs' amputations (#3258): the medicine Need's stalled-source
/// withdrawals and herb harvest, and a patient's life-saving amputation, each under its Need and admitted by claims.
/// The medicine bill is declared to the work ledger under the reserves Need.
/// </summary>
public sealed partial class RoundsMedicalPlanner : IOrderDeclarer
{
    private readonly Rounder reviewer;
    private readonly FacadeNativeClient native;

    private RoundsMedicalPlanner(Rounder reviewer, FacadeNativeClient native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsMedicalPlanner NewRoundsMedicalPlanner(Rounder reviewer, FacadeNativeClient native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsMedicalPlanner: reviewer or native is missing");
        }

        return new RoundsMedicalPlanner(reviewer, native);
    }

    /// <summary>
    /// The reserves Need's restock: a stalled medicine designation is withdrawn first, once per stall; then, below
    /// the target bar, the medicine bill is the ledger's, and with no bench for it the herbs are harvested. Each is
    /// a Method of the Need; an open one is its own work, and nothing else waits on it.
    /// </summary>
    internal async Task<RoundsMedicalResult> RestockAsync(NeedWork work, CancellationToken call)
    {
        var p = reviewer.Player;
        var state = work.State;
        var owner = work.Owner;
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsMedicalResult { Verdict = Verdict.BuildingReasonNoReview };
        }

        var started = reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var observed = await reviewer.ColonyFactsAsync(native, identity, false, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: colony facts rejected", e);
        }

        if ((observed.Context?.Tick).GetValueOrDefault() < review.Tick.Value)
        {
            throw new ControlException("step: observed context older than the review");
        }

        var tables = await native.FrameTablesAsync(identity, call).ConfigureAwait(false);
        var items = await reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
        Resource medicineResource;
        try
        {
            medicineResource = items.MedicineAt(0);
        }
        catch (DomainException)
        {
            return new RoundsMedicalResult { Verdict = Verdict.FieldUnavailable("medicine_catalog") };
        }

        var sources = AcquisitionReads.ColonyAcquisition(observed, tables, await CatalogAsync(identity, call).ConfigureAwait(false));
        var stalledSources = new HashSet<string>();
        bool Medicine(AcquisitionSource row) => new Resource(row.Resource) == medicineResource;
        foreach (var row in RoundsAcquisitionPlanner.StalledDesignations(sources, false, new Tick((observed.Context?.Tick).GetValueOrDefault()), reviewer.Policy.AcquisitionProgress(), Medicine))
        {
            stalledSources.Add(row.Id);
            var (stallMethod, stallPlan) = RoundsAcquisitionPlanner.StallWithdraw(row);
            if (p.Journal.OwnerMethodPlan(owner, stallMethod, call) is not null)
            {
                continue;
            }

            p.Current(call, work.Epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("step: session state changed");
            }

            var withdraw = p.Journal.CommitOwnerWork(owner, stallMethod, "withdraw stalled " + row.Resource, stallPlan, call);
            return new RoundsMedicalResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(withdraw), Plan = withdraw.Outcome is ClaimOutcome.Conflict or ClaimOutcome.Refused ? default : stallPlan.Id };
        }

        var facts = ColonyFramesReads.ColonyMedicalReserve(observed, tables, await CatalogAsync(identity, call).ConfigureAwait(false)) with { Catalog = items };
        var medicalReview = MedicalRules.ReviewMedicalReserve(facts, true, reviewer.Policy.MedicalReserve);
        if (!medicalReview.Active)
        {
            return new RoundsMedicalResult { Verdict = Verdict.BuildingReasonNoDeficit };
        }

        var seen = owner.Methods.Select(m => m.Method).ToList();
        var census = await native.ReadGearBenchesAsync(identity, call).ConfigureAwait(false);
        var benches = census.Select(row => row.Bench).ToList();
        var choice = MedicalRules.SelectMedicineMethod(new MedicinePlanningRequest { Review = medicalReview, Resource = medicineResource, Seen = seen, Benches = Known<EquatableArray<GearBench>>([.. benches]) });
        if (choice.Kind == MedicineMethodKind.Blocked)
        {
            return await HarvestMedicineAsync(work, observed, tables, medicalReview, facts, medicineResource, stalledSources, started, call).ConfigureAwait(false);
        }

        if (choice.Kind != MedicineMethodKind.Produce)
        {
            return new RoundsMedicalResult { Verdict = RoundsMedicalPlanner.MedicineChoiceVerdict(choice.Kind, medicineResource) };
        }

        return new RoundsMedicalResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "medicine_bill") };
    }

    /// <summary>The load's definition catalog; none when the native serves no definitions.</summary>
    private async Task<DefinitionCatalog?> CatalogAsync(C.Identity identity, CancellationToken ct)
    {
        if (native is IDefinitionSource source)
        {
            return await source.DefinitionCatalogAsync(identity, ct).ConfigureAwait(false);
        }

        if (reviewer.Native is IDefinitionSource shared)
        {
            return await shared.DefinitionCatalogAsync(identity, ct).ConfigureAwait(false);
        }

        return null;
    }

    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(Declare(reviewer.Policy, projection, benches).For(MedicalRules.MedicalReservesNeed.FilingKey.Value));

    /// <summary>
    /// The reserves Need's medicine bill: while the stock is below the target bar, the stock-target bill
    /// <see cref="MedicalRules.SelectMedicineMethod"/> picks; nothing while it is met; an abstain while the stock
    /// or the medicine catalog is unread.
    /// </summary>
    internal static Declared Declare(RoundsPolicy policy, ColonyProjection projection, IReadOnlyList<GearBench> benches)
    {
        var f = projection.Facts;
        var reserve = MedicalRules.MedicalReserveAtTarget(f.MedicalReserve with { Colonists = f.Colonists }, policy.Seasonal(f.Calendar, f.DisasterConditions).MedicalReserve);
        if (reserve is null || !reserve.Stock.IsKnown)
        {
            return WorkRules.Abstaining(Cause.UnreadMedicine);
        }

        if (!reserve.Active)
        {
            return new Declared();
        }

        Resource medicine;
        try
        {
            medicine = f.Items.MedicineAt(0);
        }
        catch (DomainException)
        {
            return WorkRules.Abstaining(Cause.UnreadMedicine);
        }

        var choice = MedicalRules.SelectMedicineMethod(new MedicinePlanningRequest { Review = reserve, Resource = medicine, Benches = Known<EquatableArray<GearBench>>([.. benches]) });
        return CareOrdersRules.DeclareMedicine(choice, benches);
    }

    private async Task<RoundsMedicalResult> HarvestMedicineAsync(NeedWork work, O.ColonyFactsSnapshot observed, Tables tables, MedicalReserveReview medicalReview, MedicalReserveObservation facts, Resource medicineResource, HashSet<string> stalledSources, DateTimeOffset started, CancellationToken call)
    {
        var p = reviewer.Player;
        var state = work.State;
        if (!medicalReview.Replenish.TryGetValue(out var replenish))
        {
            return new RoundsMedicalResult { Verdict = Verdict.FieldUnavailable(RoundsMedicalPlanner.UnreadMedicalFact(facts)) };
        }

        if (replenish <= 0)
        {
            return new RoundsMedicalResult { Verdict = Verdict.BuildingReasonNoDeficit };
        }

        var review = p.Journal.LoadRounds(call);
        // The economy/resource Need's supply plan routes medicine to a field when it opened one: its Need owner is
        // read, not worked, here.
        if (reviewer.ResourceTargets(state.Snapshot).Count > 0)
        {
            var supplyGoal = p.Journal.LoadNeed(EconomyFamily.Resource, state.Snapshot, call);
            var supply = await reviewer.ResourceSupplyAsync(state, review, supplyGoal, call).ConfigureAwait(false);
            if (supply.FieldRoute(medicineResource))
            {
                return new RoundsMedicalResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "medicine_field") };
            }
        }

        var sources = AcquisitionReads.ColonyAcquisition(observed, tables, await CatalogAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false));
        if (!sources.TryGetValue(out var rows))
        {
            return new RoundsMedicalResult { Verdict = Verdict.FieldUnavailable("acquisition_sources") };
        }

        var pending = 0.0;
        foreach (var row in rows)
        {
            if (row.Designated && !row.Hunt && !stalledSources.Contains(row.Id) && new Resource(row.Resource) == medicineResource)
            {
                pending += row.Yield;
            }
        }

        var plans = p.Journal.LoadPlans(call);
        var held = new Dictionary<string, bool>();
        foreach (var plan0 in plans)
        {
            foreach (var progress in plan0.Progress)
            {
                if (progress.Action.Acquisition is { } acquisition && WorkProgress.Open([progress]))
                {
                    held[acquisition.Thing] = true;
                }
            }
        }

        IReadOnlyList<AcquisitionSource> selected;
        try
        {
            selected = AcquisitionRules.SelectResourceAcquisition(sources, Known((double)replenish), Known(pending), medicineResource, held);
        }
        catch (DomainException)
        {
            return new RoundsMedicalResult { Verdict = Verdict.FieldUnavailable("acquisition_sources") };
        }

        if (selected.Count == 0)
        {
            if (pending >= replenish)
            {
                return new RoundsMedicalResult { Verdict = Verdict.BuildingReasonExistingWork };
            }

            return new RoundsMedicalResult { Verdict = Verdict.AwaitingPlan("medicine_source", "") };
        }

        var text = new StringBuilder();
        foreach (var row in selected)
        {
            text.Append($"{row.Id}/{row.Resource}/{row.Token}/{row.Cell.X}/{row.Cell.Z}\n");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        var method = new MethodId("acquire-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
        if (p.Journal.OwnerMethodPlan(work.Owner, method, call) is not null)
        {
            return new RoundsMedicalResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "acquire_method") };
        }

        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        for (var i = 0; i < selected.Count; i++)
        {
            var row = selected[i];
            var value = new Acquisition(row.Id, row.Resource, row.Cell);
            actions.Add(DomainAction.NewAcquisitionAction(new ActionId($"{id}-{i}"), value));
        }

        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, work.Epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("harvestMedicine: session state changed or the read took too long");
        }

        var commit = p.Journal.CommitOwnerWork(work.Owner, method, "", plan, call);
        return new RoundsMedicalResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit), Plan = commit.Outcome is ClaimOutcome.Conflict or ClaimOutcome.Refused ? default : id };
    }

    /// <summary>
    /// A critical patient's life-saving amputation in the ordinary pass: the first one the care census owes the
    /// patient, once per part per requeue window, under the patient's Need. Null when none is owed (or the census is
    /// unread).
    /// </summary>
    internal async Task<Verdict?> AmputateAsync(NeedWork work, PawnId patient, CancellationToken call)
    {
        var p = reviewer.Player;
        var state = work.State;
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return null;
        }

        var expected = await StepScope.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("planAmputation: boundary moved");
        }

        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, claims, call).ConfigureAwait(false);
        var window = expected.Tick.Value / RoundsMedicalPlanner.AmputationRequeueTicks;
        if (!read.Projection.Facts.MedicalPawns.TryGetValue(out var pawns))
        {
            return null;
        }

        foreach (var pawn in pawns.Where(pawn => pawn.Id == patient))
        {
            foreach (var surgery in EmergencyRules.LifeSavingAmputations(pawn))
            {
                var method = new MethodId($"amputate-{surgery.Pawn}-{surgery.Part}-{window}");
                if (p.Journal.OwnerMethodPlan(work.Owner, method, call) is not null)
                {
                    continue;
                }

                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewSurgeryAction(new ActionId($"{id}-0"), surgery);
                var plan = new PlanSpec(id, new PlanRevision(1), [action]);
                p.Current(call, work.Epoch);
                if (p.Session.State() != state)
                {
                    throw new ControlException("planAmputation: session state changed");
                }

                return RoundsBuildingPlanner.OwnerCommitVerdict(p.Journal.CommitOwnerWork(work.Owner, method, "", plan, call));
            }
        }

        return null;
    }
}

public readonly record struct RoundsMedicalResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

partial class RoundsMedicalPlanner
{
    internal const long AmputationRequeueTicks = GameTime.TicksPerHour;

    internal static bool MedicalIssue(IReadOnlyList<O.ReadIssue> issues, string field) => issues.Any(issue => (issue.Field ?? "") == field);

    internal static string UnreadMedicalFact(MedicalReserveObservation facts)
    {
        if (!facts.Items.IsKnown)
        {
            return "medicine_items";
        }

        return "medicine_stock";
    }

    internal static Verdict MedicineChoiceVerdict(MedicineMethodKind kind, Resource resource) => kind switch
    {
        MedicineMethodKind.Recovered => BuildingReasonNoDeficit,
        MedicineMethodKind.Unknown => FieldUnavailable("bench_recipes"),
        MedicineMethodKind.Blocked => AwaitingPlan("production_bench", resource.Value),
        _ => WaitFor(Cause.MethodUsed, "medicine_method"),
    };
}
