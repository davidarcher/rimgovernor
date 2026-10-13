using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsMedicalSource : Observation.IColonySource, IColonyFactsReader
{
    Task<(IReadOnlyList<GearBenchRead> Reads, BridgeResult Result)> ReadGearBenchesAsync(C.Identity identity, CancellationToken ct);
}

/// <summary>Composes MaintainMedicalReserves and the critical-medicine amputation method.</summary>
public sealed class RoundsMedicalPlanner : IOrderDeclarer
{
    private readonly Rounder reviewer;
    private readonly IRoundsMedicalSource native;

    private RoundsMedicalPlanner(Rounder reviewer, IRoundsMedicalSource native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsMedicalPlanner NewRoundsMedicalPlanner(Rounder reviewer, IRoundsMedicalSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsMedicalPlanner: reviewer or native is missing");
        }

        return new RoundsMedicalPlanner(reviewer, native);
    }

    internal async Task<RoundsMedicalResult> StepAsync(StepArbiter? arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var amputation = await PlanAmputationAsync(state, review, call, epoch).ConfigureAwait(false);
        if (amputation.Plan.Value != "")
        {
            return amputation;
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyFunctions.MaintainMedicalReserves, call);
        if (!workable || goal is null || !review.Latches.Medical.Restocks())
        {
            return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        var started = reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var reply = await reviewer.ColonyFactsAsync(native, identity, false, call).ConfigureAwait(false);
        var observed = reply.Observed ?? throw new ControlException("step: observed == nil");
        try
        {
            BridgeApi.ValidateColonyFacts(observed, identity);
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: colony facts rejected", e);
        }

        if (observed.Context.Tick < review.Tick.Value)
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

        var sources = ObservationPackage.ColonyAcquisition(observed, tables, await CatalogAsync(identity, call).ConfigureAwait(false));
        var stalledSources = new HashSet<string>();
        bool Medicine(AcquisitionSource row) => new Resource(row.Resource) == medicineResource;
        foreach (var row in BuildingRuntimePackage.StalledDesignations(sources, false, new Tick(observed.Context.Tick), reviewer.Policy.AcquisitionProgress(), Medicine))
        {
            stalledSources.Add(row.Id);
            var (stallMethod, stallPlan) = BuildingRuntimePackage.StallWithdraw(row);
            try
            {
                p.Journal.LoadMethod(goal.Standard.Id, goal.Standard.Episode, stallMethod, call);
                continue;
            }
            catch (NotFoundException)
            {
            }

            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("step: session state changed");
            }

            p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, stallMethod, stallPlan, call);
            return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = stallPlan.Id };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var facts = ObservationPackage.ColonyMedicalReserve(observed, tables, await CatalogAsync(identity, call).ConfigureAwait(false)) with { Catalog = items };
        var medicalReview = PolicyFunctions.ReviewMedicalReserve(facts, review.Latches.MedicalReserve, reviewer.Policy.MedicalReserve);
        if (!medicalReview.Active)
        {
            return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        var seen = goal.Methods.Select(m => m.MethodId).ToList();
        var (census, _) = await native.ReadGearBenchesAsync(identity, call).ConfigureAwait(false);
        var benches = census.Select(row => row.Bench).ToList();
        var choice = PolicyFunctions.SelectMedicineMethod(new MedicinePlanningRequest { Review = medicalReview, Resource = medicineResource, Seen = seen, Benches = Known<EquatableArray<GearBench>>([.. benches]) });
        if (choice.Kind == MedicineMethodKind.Blocked)
        {
            return await HarvestMedicineAsync(state, goal, observed, tables, medicalReview, facts, medicineResource, stalledSources, started, call, epoch).ConfigureAwait(false);
        }

        if (choice.Kind != MedicineMethodKind.Produce)
        {
            return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.MedicineChoiceVerdict(choice.Kind, medicineResource) };
        }

        return new RoundsMedicalResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "medicine_bill") };
    }

    /// <summary>The load's definition catalog (Go carried it on the frame tables); none when the native serves no definitions.</summary>
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

    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var declared = DeclareOrdersInner(snapshot, projection, benches, ct);
        return Task.FromResult(declared.For(PolicyFunctions.MaintainMedicalReserves));
    }

    private Declared DeclareOrdersInner(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var journal = reviewer.Player.Journal;
        var review = journal.LoadRounds(ct);
        if (!review.Enabled || review.Snapshot != snapshot)
        {
            return PolicyFunctions.Abstaining(Cause.UnreadReview);
        }

        var (_, workable) = journal.Workable(review, PolicyFunctions.MaintainMedicalReserves, ct);
        if (!workable || !review.Latches.Medical.Restocks())
        {
            return new Declared();
        }

        var facts = projection.Facts.MedicalReserve;
        var medicalReview = PolicyFunctions.ReviewMedicalReserve(facts, review.Latches.MedicalReserve, reviewer.Policy.MedicalReserve);
        if (!medicalReview.Active)
        {
            return new Declared();
        }

        Resource medicine;
        try
        {
            medicine = projection.Facts.Items.MedicineAt(0);
        }
        catch (DomainException)
        {
            return PolicyFunctions.Abstaining(Cause.UnreadMedicine);
        }

        var choice = PolicyFunctions.SelectMedicineMethod(new MedicinePlanningRequest { Review = medicalReview, Resource = medicine, Benches = Known<EquatableArray<GearBench>>([.. benches]) });
        return PolicyFunctions.DeclareMedicine(choice, benches);
    }

    private async Task<RoundsMedicalResult> HarvestMedicineAsync(ControlState state, StandardState goal, O.ColonyFactsSnapshot observed, Tables tables, MedicalReserveReview medicalReview, MedicalReserveObservation facts, Resource medicineResource, HashSet<string> stalledSources, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        if (!medicalReview.Replenish.TryGetValue(out var replenish))
        {
            return new RoundsMedicalResult { Verdict = Verdict.FieldUnavailable(BuildingRuntimePackage.UnreadMedicalFact(facts)) };
        }

        if (replenish <= 0)
        {
            return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        var review = p.Journal.LoadRounds(call);
        var (supplyGoal, workable) = p.Journal.Workable(review, PolicyFunctions.MaintainResource, call);
        if (workable && supplyGoal is not null)
        {
            var supply = await reviewer.ResourceSupplyAsync(state, review, supplyGoal, call).ConfigureAwait(false);
            if (supply.FieldRoute(medicineResource))
            {
                return new RoundsMedicalResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "medicine_field") };
            }
        }

        var sources = ObservationPackage.ColonyAcquisition(observed, tables, await CatalogAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false));
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
                if (progress.Action.Acquisition is { } acquisition && Standard.WorkOpen([progress]))
                {
                    held[acquisition.Thing] = true;
                }
            }
        }

        IReadOnlyList<AcquisitionSource> selected;
        try
        {
            selected = PolicyFunctions.SelectResourceAcquisition(sources, Known((double)replenish), Known(pending), medicineResource, held);
        }
        catch (DomainException)
        {
            return new RoundsMedicalResult { Verdict = Verdict.FieldUnavailable("acquisition_sources") };
        }

        if (selected.Count == 0)
        {
            if (pending >= replenish)
            {
                return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
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
        try
        {
            p.Journal.LoadMethod(goal.Standard.Id, goal.Standard.Episode, method, call);
            return new RoundsMedicalResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "acquire_method") };
        }
        catch (NotFoundException)
        {
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
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("harvestMedicine: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    private async Task<RoundsMedicalResult> PlanAmputationAsync(ControlState state, Rounds review, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var (incident, found) = BuildingRuntimePackage.IncidentDeficit(p.Journal, review, PolicyFunctions.CriticalMedicine, call);
        if (!found)
        {
            return default;
        }

        var expected = await BuildingRuntimePackage.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("planAmputation: boundary moved");
        }

        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, claims, call).ConfigureAwait(false);
        var window = expected.Tick.Value / BuildingRuntimePackage.AmputationRequeueTicks;
        if (!read.Projection.Facts.MedicalPawns.TryGetValue(out var pawns))
        {
            return default;
        }

        foreach (var pawn in pawns)
        {
            foreach (var surgery in PolicyFunctions.LifeSavingAmputations(pawn))
            {
                var method = new MethodId($"amputate-{surgery.Pawn}-{surgery.Part}-{window}");
                if (incident.Methods.Any(m => m.Method == method))
                {
                    continue;
                }

                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewSurgeryAction(new ActionId($"{id}-0"), surgery);
                var plan = new PlanSpec(id, new PlanRevision(1), [action]);
                p.Current(call, epoch);
                if (p.Session.State() != state)
                {
                    throw new ControlException("planAmputation: session state changed");
                }

                p.Journal.CommitIncidentMethod(incident.Incident.Id, method, "", plan, call);
                return new RoundsMedicalResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
            }
        }

        return default;
    }
}

public readonly record struct RoundsMedicalResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

internal static partial class BuildingRuntimePackage
{
    internal const long AmputationRequeueTicks = GameTime.TicksPerHour;

    internal static bool MedicalIssue(IReadOnlyList<O.ReadIssue> issues, string field) => issues.Any(issue => issue.Field == field);

    internal static string UnreadMedicalFact(MedicalReserveObservation facts)
    {
        if (!facts.Colonists.IsKnown)
        {
            return "colonists";
        }

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
