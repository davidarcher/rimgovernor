using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native gear-bench read the work planner counts bench work from.</summary>
public interface IRoundsWorkBenchSource
{
    Task<(IReadOnlyList<GearBenchRead> Reads, BridgeResult Result)> ReadGearBenchesAsync(C.Identity identity, CancellationToken ct);
}

public sealed class RoundsWorkResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

public sealed class RoundsWorkPlanner
{
    private readonly Rounder _reviewer;
    internal Rounder Reviewer => _reviewer;
    private readonly IRoundsWorkBenchSource _benches;

    public RoundsWorkPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsWorkPlanner: reviewer == nil");
        }
        _reviewer = reviewer;
        _benches = (IRoundsWorkBenchSource)reviewer.Native;
    }

    internal async Task<RoundsWorkResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsWorkResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown)
        {
            throw new ControlException("step: !state.ObservationKnown");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsWorkResult { Verdict = BuildingReasonNoReview };
        }
        var deficit = false;
        foreach (var binding in review.Standards)
        {
            if (binding.Concern != PolicyApi.MaintainResource)
            {
                continue;
            }
            var resource = p.Journal.LoadStandard(binding.Standard, call);
            deficit = resource.Deficit;
        }
        var (goal, deficitProject) = p.Journal.Workable(review, PolicyApi.EnsureWorkAssignments, call);
        if (goal is null || !deficitProject)
        {
            return new RoundsWorkResult { Verdict = BuildingReasonNoDeficit };
        }
        var open = new List<PlanState>();
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                open.Add(plan);
            }
        }
        RoundsWorkResult Existing(RoundsWorkResult unknown) => open.Count > 0 ? new RoundsWorkResult { Verdict = BuildingReasonExistingWork } : unknown;

        var plans = p.Journal.LoadPlans(call);
        var playerPlans = p.Journal.PlayerPlans(Player.PlayerWorld(state.Snapshot), call);
        var definitions = RoundsProjectDefinitions(plans, state.Snapshot, playerPlans);
        var expected = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false);
        if (!read.Projection.WorkPawns.TryGetValue(out var pawns))
        {
            return Existing(new RoundsWorkResult { Verdict = FieldUnavailable("work_pawns") });
        }
        var construction = PolicyApi.ConstructionSkillChoices(read.Projection.Facts.CurrentConstruction, read.Projection.WorkPawns).ToList();
        if (construction.Count > 8)
        {
            construction = construction[..8];
        }
        if (!RoundsProjectWork(definitions, read.Projection.Definitions).TryGetValue(out var required))
        {
            return Existing(new RoundsWorkResult { Verdict = FieldUnavailable("project_work") });
        }
        var resourceTargets = _reviewer.ResourceTargets(state.Snapshot);
        var targets = RoundsDeficitTargets(resourceTargets, deficit, read.Projection.Facts.Gear);
        var benchWork = await RoundsBenchWorkAsync(_reviewer.BenchSource(_benches, expected, false), state.Snapshot, plans, playerPlans, targets, targets.Count > 0, read.Projection.Facts.Items.Wort, call).ConfigureAwait(false);
        if (!benchWork.TryGetValue(out var benchRows))
        {
            return Existing(new RoundsWorkResult { Verdict = FieldUnavailable("bench_work") });
        }
        required = MergeWorkRequirements([.. required], benchRows);
        var needs = RoundsResearchNeeds(p.Journal, _reviewer.Policy, read.Projection.Facts.Items, state.Snapshot, _reviewer.EquipmentOwed(state.Snapshot), call);
        needs = _reviewer.CensusResearchNeeds(needs);
        required = MergeWorkRequirements([.. required], RoundsResearchWork(needs, read.Projection.Facts.Research));
        required = MergeWorkRequirements([.. required], FishingWork(read.Projection));
        if (!StudyWork(read.Projection, call).TryGetValue(out var study))
        {
            return Existing(new RoundsWorkResult { Verdict = FieldUnavailable("study_work") });
        }
        required = MergeWorkRequirements([.. required], study);
        var demand = RoundsDiseaseDemand(read.Projection, definitions, review, state.Snapshot);
        var sharedWork = p.Journal.WorkResources(state.Snapshot, call);
        demand = demand with { CommittedPawns = sharedWork.Claims.Pawns, WorkClaimsTruncated = sharedWork.Claims.Truncated };
        var decision = PolicyApi.PlanWork(pawns, required, demand);
        if (!decision.Capacity.IsKnown)
        {
            return Existing(new RoundsWorkResult { Verdict = FieldUnavailable("work_capacity") });
        }
        var byId = pawns.ToDictionary(pawn => pawn.Id);
        var work = new List<WorkAssignment>();
        foreach (var assignment in decision.Assignments)
        {
            var pawn = byId[assignment.Pawn];
            var mk = pawn.Manual.TryGetValue(out var manual);
            var ck = pawn.Work.IsKnown;
            if (!mk || !ck || !manual)
            {
                continue;
            }
            var (changed, changedOk) = PolicyApi.WorkChanges(pawn, assignment);
            if (!changedOk)
            {
                throw new ControlException("step: !ok");
            }
            if (changed.Count > 0) work.Add(WorkAssignment.NewWorkAssignment(assignment.Pawn, changed));
        }
        var settings = new List<PawnSettings>(PolicyApi.HostilityChanges(HostilityRows(pawns), read.Emergency.Threats));
        var reserve = PolicyApi.ReviewMedicalReserve(read.Projection.Facts.MedicalReserve, false, _reviewer.Policy.MedicalReserve);
        settings.AddRange(PolicyApi.MedicineCarryChanges(PolicyApi.MedicineCarryRows(pawns), reserve.Stock, reserve.Target));
        if (read.Projection.Facts.OwnedNames.TryGetValue(out var ownedNames))
        {
            settings.AddRange(PolicyApi.NicknameChanges(ownedNames));
        }
        var care = PolicyApi.MedicalCareChanges(read.Projection.Facts, _reviewer.Policy.MedicalReserve).ToList();
        if (care.Count > 8)
        {
            care = care[..8];
        }
        settings.AddRange(care);
        settings.AddRange(RoundsMechSettings(read));
        var reading = new List<ReadingPolicy>();
        var readingChanges = RoundsReadingChanges(read.Projection.Policies, read.Projection.Facts.OwnedNames, pawns);
        if (readingChanges.Count > 0)
        {
            foreach (var c in readingChanges.Take(8))
            {
                if (c.Write is not null)
                {
                    reading.Add(c.Write);
                }
                if (c.Assign is not null)
                {
                    settings.Add(c.Assign);
                }
            }
        }
        var food = new List<FoodPolicy>();
        var dietChanges = RoundsDietChanges(read.Projection.Facts.Ideology, read.Projection.Policies, read.Projection.Facts.OwnedNames, pawns, read.Projection.Facts.FoodReserve);
        if (dietChanges.Count > 0)
        {
            foreach (var c in dietChanges.Take(8))
            {
                if (c.Write is not null)
                {
                    food.Add(c.Write);
                }
                if (c.Assign is not null)
                {
                    settings.Add(c.Assign);
                }
            }
        }
        foreach (var openPlan in open)
        {
            CancelStaleWorkActions(p.Journal, goal, openPlan, work, settings, call);
            if (StorePackage.PlanOpen(p.Journal.LoadPlan(openPlan.Spec.Id, call)))
            {
                return new RoundsWorkResult { Verdict = BuildingReasonExistingWork };
            }
        }
        if (work.Count == 0 && settings.Count == 0 && reading.Count == 0 && food.Count == 0 && construction.Count == 0)
        {
            return new RoundsWorkResult { Verdict = FieldUnavailable("work_assignments") };
        }
        if (work.Count > 8)
        {
            work = work[..8];
        }
        var inv = CultureInfo.InvariantCulture;
        var hash = new StringBuilder();
        foreach (var choice in construction)
        {
            hash.Append(inv, $"construction/{choice.Site.Id}/{choice.Minimum}\n");
        }
        foreach (var w in work)
        {
            hash.Append(inv, $"{w.Pawn}/{string.Join(',', w.Settings.Select(s => $"{s.Definition}:{s.Priority}"))}\n");
            if (w.Schedule.Count > 0)
            {
                hash.Append(inv, $"schedule/{GoQuoteList(w.Schedule)}/{goal.Methods.Count}\n");
            }
        }
        foreach (var s in settings)
        {
            hash.Append(inv, $"{s.Kind}/{s.Pawn}/{s.Hostility}/{s.SelfTend}/{GoQuote(s.LeaveName)}/{s.MedicineCarry ?? 0}/{s.MedicalCare}/{GoQuote(s.ReadingPolicy ?? "")}/{GoQuote(s.DrugPolicy ?? "")}/{GoQuote(s.FoodPolicy ?? "")}/{GoQuote(s.MechWorkMode ?? "")}/{s.MechControlGroup ?? 0}/{goal.Methods.Count}\n");
        }
        foreach (var r in reading)
        {
            hash.Append(inv, $"reading/{GoQuote(r.Name)}/{GoQuoteList(r.Definitions)}/{goal.Methods.Count}\n");
        }
        foreach (var f in food)
        {
            hash.Append(inv, $"food/{GoQuote(f.Name)}/{GoQuoteList(f.Definitions)}/{goal.Methods.Count}\n");
        }
        var methodId = new MethodId("work-" + HashHex16(hash.ToString()));
        try
        {
            p.Journal.LoadOwnerMethod(goal, methodId, call);
            return new RoundsWorkResult { Verdict = WaitFor(Cause.MethodUsed, "work_method") };
        }
        catch (NotFoundException)
        {
        }
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        for (var i = 0; i < work.Count; i++)
        {
            actions.Add(DomainAction.NewWorkAssignmentAction(new ActionId($"{id}-{i}"), work[i]));
        }
        foreach (var r in reading)
        {
            actions.Add(DomainAction.NewReadingPolicyAction(new ActionId($"{id}-{actions.Count}"), r));
        }
        foreach (var f in food)
        {
            actions.Add(DomainAction.NewFoodPolicyAction(new ActionId($"{id}-{actions.Count}"), f));
        }
        foreach (var s in settings)
        {
            actions.Add(DomainAction.NewPawnSettingsAction(new ActionId($"{id}-{actions.Count}"), s));
        }
        foreach (var choice in construction)
        {
            var action = DomainAction.NewBuildingAction(new ActionId($"{id}-{actions.Count}"), choice.Site.Building ?? throw new InvalidOperationException("a construction choice names a site with a building"), PolicyApi.AdoptedTier(choice.Site));
            actions.Add(action.WithFinishingSkill(choice.Minimum, choice.Site.Id));
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }
        p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, methodId, "", spec, call);
        return new RoundsWorkResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Cancels the not-yet-dispatched work and settings actions of an open plan that the fresh review no longer wants.</summary>
    internal static void CancelStaleWorkActions(StoreJournal journal, RimGovernor.Brain.Store.StandardState owner, PlanState plan, IReadOnlyList<WorkAssignment> fresh, IReadOnlyList<PawnSettings> settings, CancellationToken ct)
    {
        var routineOwned = owner.Standard.Kind == PolicyApi.EnsureWorkAssignments
            && owner.Methods.Any(m => m.Plan == plan.Spec.Id && m.MethodId == plan.Method);
        var wantedSettings = new HashSet<PawnSettings>(settings);
        var wanted = new Dictionary<Domain.PawnId, WorkAssignment>();
        foreach (var w in fresh)
        {
            wanted[w.Pawn] = w;
        }
        var assignments = new Dictionary<ActionId, WorkAssignment>();
        var staleSettings = new Dictionary<ActionId, bool>();
        foreach (var action in plan.Spec.Actions)
        {
            if (action.WorkAssignment is { } w)
            {
                assignments[action.Id] = w;
            }
            if (action.PawnSettings is { } s)
            {
                staleSettings[action.Id] = !wantedSettings.Contains(s);
            }
            if (routineOwned && action.DrugPolicy is not null) staleSettings[action.Id] = true;
        }
        foreach (var progress in plan.Progress)
        {
            var v = progress.View;
            if (v.Stage != Stage.Pending && v.Stage != Stage.Prepared)
            {
                continue;
            }
            var isSetting = staleSettings.TryGetValue(v.Action, out var stale);
            if (assignments.TryGetValue(v.Action, out var w))
            {
                stale = WorkActionStale(w, wanted);
            }
            else if (!isSetting)
            {
                continue;
            }
            if (!stale)
            {
                continue;
            }
            journal.Cancel(plan.Spec.Id, v.Action, ct);
        }
    }

    /// <summary>Whether a planned assignment differs from the fresh one for its pawn (or the pawn has none).</summary>
    internal static bool WorkActionStale(WorkAssignment w, Dictionary<Domain.PawnId, WorkAssignment> wanted)
    {
        if (!wanted.TryGetValue(w.Pawn, out var now))
        {
            return true;
        }
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var setting in now.Settings)
        {
            values[setting.Definition] = setting.Priority;
        }
        foreach (var setting in w.Settings)
        {
            if (!values.TryGetValue(setting.Definition, out var want) || want != setting.Priority)
            {
                return true;
            }
        }
        return false;
    }

    internal static List<HostilityPawn> HostilityRows(IReadOnlyList<WorkPawn> pawns) => [.. pawns.Select(p => p.HostilityPawn())];

    internal static Fact<EquatableArray<HostilityPawn>> HostilityPawns(Fact<EquatableArray<WorkPawn>> pawns) =>
        pawns.TryGetValue(out var rows) ? Known<EquatableArray<HostilityPawn>>([.. HostilityRows(rows)]) : Unknown<EquatableArray<HostilityPawn>>();

    /// <summary>The reading-policy writes and assignments the review's policies and books call for.</summary>
    internal static IReadOnlyList<ReadingPolicyChange> RoundsReadingChanges(Fact<ObservationPolicies> policies, Fact<EquatableArray<OwnedName>> names, IReadOnlyList<WorkPawn> pawns)
    {
        var ok = policies.TryGetValue(out var p);
        var named = names.TryGetValue(out var owned);
        if (!ok || !named || p!.Books.Count == 0)
        {
            return [];
        }
        var entries = p.Reading.Select(e => new ReadingPolicyEntry { Id = e.ID, Label = e.Label, Pawns = e.Pawns, Allowed = e.Allowed }).ToList();
        return PolicyApi.ReadingPolicyChanges(pawns, owned, entries, p.Books);
    }

    /// <summary>The food-policy writes and assignments the review's policies and reserve call for.</summary>
    internal static IReadOnlyList<FoodPolicyChange> RoundsDietChanges(Fact<Ideoligion> ideology, Fact<ObservationPolicies> policies, Fact<EquatableArray<OwnedName>> names, IReadOnlyList<WorkPawn> pawns, Fact<FoodReserveReview> reserve)
    {
        var ok = policies.TryGetValue(out var p);
        var named = names.TryGetValue(out var owned);
        if (!ok || !named || p!.Foods.Count == 0)
        {
            return [];
        }
        var entries = p.Food.Select(e => new FoodPolicyEntry { Id = e.ID, Label = e.Label, Pawns = e.Pawns, Allowed = e.Allowed }).ToList();
        reserve.TryGetValue(out var stock);
        return PolicyApi.DietPolicyChanges(ideology, pawns, p.FoodEaters, owned, entries, p.Foods, stock?.IsShort ?? false);
    }

    /// <summary>Go's <c>%q</c> of a string.</summary>
    internal static string GoQuote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Go's <c>%q</c> of a string slice.</summary>
    internal static string GoQuoteList(IEnumerable<string> items) => "[" + string.Join(' ', items.Select(GoQuote)) + "]";
}
