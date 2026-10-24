using System.Globalization;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native gear-bench read the work planner counts bench work from.</summary>
public interface IRoundsWorkBenchSource
{
    Task<IReadOnlyList<GearBenchRead>> ReadGearBenchesAsync(C.Identity identity, CancellationToken ct);
}

/// <summary>
/// The admin/work-assignments Need's step (#3304): the colony's work priorities, pawn settings, reading, food and drug
/// policies and construction finishing-skill choices, committed as one Method under the Need. The MethodId names the
/// changes it writes, so the same changes are committed once; an open plan whose actions the fresh review no longer
/// wants has them cancelled first.
/// </summary>
public sealed partial class RoundsWorkPlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsWorkBenchSource _benches;

    public RoundsWorkPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("RoundsWorkPlanner: reviewer is missing");
        }
        _reviewer = reviewer;
        _benches = (IRoundsWorkBenchSource)reviewer.Native;
    }

    internal async Task<Verdict> StepAsync(NeedWork need, CancellationToken call)
    {
        var p = _reviewer.Player;
        var state = need.State;
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return BuildingReasonNoReview;
        }
        var open = OpenWorkPlans(p.Journal, need.Owner, call);
        Verdict Existing(Verdict unknown) => open.Count > 0 ? BuildingReasonExistingWork : unknown;

        var plans = p.Journal.LoadPlans(call);
        var definitions = RoundsProjectBill.RoundsProjectDefinitions(plans, state.Snapshot);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false);
        if (!read.Projection.WorkPawns.TryGetValue(out var pawns))
        {
            return Existing(FieldUnavailable("work_pawns"));
        }
        var construction = ConstructionSkillRules.ConstructionSkillChoices(read.Projection.Facts.CurrentConstruction, read.Projection.WorkPawns).ToList();
        if (construction.Count > 8)
        {
            construction = construction[..8];
        }
        if (!RoundsProjectBill.RoundsProjectWork(definitions, read.Projection.Definitions).TryGetValue(out var required))
        {
            return Existing(FieldUnavailable("project_work"));
        }
        var resourceTargets = _reviewer.ResourceTargets(state.Snapshot);
        // The resource targets are in deficit when the economy/resource Need's threshold reads short now.
        var deficit = ResearchRules.ResourceTargetNeed(resourceTargets, read.Projection.Facts.Resources).Recovered.TryGetValue(out var recovered) && !recovered;
        var targets = RoundsProjectBill.RoundsDeficitTargets(resourceTargets, deficit, read.Projection.Facts.Gear);
        var benchWork = await RoundsProjectBill.RoundsBenchWorkAsync(_reviewer.BenchSource(_benches, expected, false), state.Snapshot, plans, targets, targets.Count > 0, read.Projection.Facts.Items.Wort, call).ConfigureAwait(false);
        if (!benchWork.TryGetValue(out var benchRows))
        {
            return Existing(FieldUnavailable("bench_work"));
        }
        required = RoundsProjectBill.MergeWorkRequirements([.. required], benchRows);
        var needs = RoundsProjectBill.RoundsResearchNeeds(p.Journal, _reviewer.Policy, read.Projection.Facts.Items, state.Snapshot, _reviewer.EquipmentOwed(state.Snapshot), call);
        needs = _reviewer.CensusResearchNeeds(needs);
        required = RoundsProjectBill.MergeWorkRequirements([.. required], RoundsProjectBill.RoundsResearchWork(needs, read.Projection.Facts.Research));
        required = RoundsProjectBill.MergeWorkRequirements([.. required], RoundsFieldPlanner.FishingWork(read.Projection));
        if (!RoundsFieldPlanner.StudyWork(read.Projection, call).TryGetValue(out var study))
        {
            return Existing(FieldUnavailable("study_work"));
        }
        required = RoundsProjectBill.MergeWorkRequirements([.. required], study);
        var demand = RoundsDisease.RoundsDiseaseDemand(read.Projection, definitions, review, state.Snapshot);
        var sharedWork = p.Journal.WorkResources(state.Snapshot, call);
        demand = demand with { CommittedPawns = sharedWork.Claims.Pawns, WorkClaimsTruncated = sharedWork.Claims.Truncated };
        var decision = WorkRules.PlanWork(pawns, required, demand);
        if (!decision.Planned)
        {
            return Existing(FieldUnavailable("work_capacity"));
        }
        var byId = pawns.ToDictionary(pawn => pawn.Id);
        var work = new List<WorkAssignment>();
        foreach (var assignment in decision.Assignments)
        {
            var pawn = byId[assignment.Pawn];
            if (pawn.Work is not PawnWorkTable.Works { Manual: true })
            {
                continue;
            }
            var (changed, changedOk) = WorkRules.WorkChanges(pawn, assignment);
            if (!changedOk)
            {
                throw new ControlException("step: !ok");
            }
            if (changed.Count > 0) work.Add(WorkAssignment.NewWorkAssignment(assignment.Pawn, changed));
        }
        var settings = new List<PawnSettings>(HostilityRules.HostilityChanges(HostilityRows(pawns), read.Emergency.Threats));
        var reserve = MedicalRules.ReviewMedicalReserve(read.Projection.Facts.MedicalReserve, false, _reviewer.Policy.MedicalReserve);
        settings.AddRange(MedicineCarryRules.MedicineCarryChanges(MedicineCarryRules.MedicineCarryRows(pawns), reserve.Stock, reserve.Target));
        if (read.Projection.Facts.Population.TryGetValue(out var census))
        {
            settings.AddRange(PawnRules.NicknameChanges(census.Names));
        }
        var care = MedicalRules.MedicalCareChanges(read.Projection.Facts, _reviewer.Policy.MedicalReserve).ToList();
        if (care.Count > 8)
        {
            care = care[..8];
        }
        settings.AddRange(care);
        settings.AddRange(RoundsMech.RoundsMechSettings(read));
        var reading = new List<ReadingPolicy>();
        var readingChanges = RoundsReadingChanges(read.Projection.Policies, read.Projection.Books, read.Projection.Facts.Population, pawns, read.Projection.Facts.GoalTraining);
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
        var dietChanges = RoundsDietChanges(read.Projection.Facts.Ideology, read.Projection.Policies, read.Projection.Foods, read.Projection.FoodEaters, read.Projection.Facts.Population, pawns, read.Projection.Facts.FoodReserve, read.Projection.Facts.Preferences);
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
        var drug = new List<DrugPolicy>();
        var drugChanges = DrugPolicyPlanRules.Changes(read.Projection.Facts.Preferences, read.Projection.Facts.Items, pawns, OwnedDrugPolicies(read.Projection.Policies));
        if (drugChanges.Write is not null)
        {
            drug.Add(drugChanges.Write);
        }
        settings.AddRange(drugChanges.Assign);
        foreach (var openPlan in open)
        {
            CancelStaleWorkActions(p.Journal, openPlan, work, settings, drug, call);
            if (StorePackage.PlanOpen(p.Journal.LoadPlan(openPlan.Spec.Id, call)))
            {
                return BuildingReasonExistingWork;
            }
        }
        if (work.Count == 0 && settings.Count == 0 && reading.Count == 0 && food.Count == 0 && drug.Count == 0 && construction.Count == 0)
        {
            return FieldUnavailable("work_assignments");
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
                hash.Append(inv, $"schedule/{GoQuoteList(w.Schedule)}\n");
            }
        }
        foreach (var s in settings)
        {
            hash.Append(inv, $"{s.Kind}/{s.Pawn}/{s.Hostility}/{s.SelfTend}/{GoQuote(s.LeaveName)}/{s.MedicineCarry ?? 0}/{s.MedicalCare}/{GoQuote(s.ReadingPolicy ?? "")}/{GoQuote(s.DrugPolicy ?? "")}/{GoQuote(s.FoodPolicy ?? "")}/{GoQuote(s.MechWorkMode ?? "")}/{s.MechControlGroup ?? 0}\n");
        }
        foreach (var r in reading)
        {
            hash.Append(inv, $"reading/{GoQuote(r.Name)}/{GoQuoteList(r.Definitions)}\n");
        }
        foreach (var f in food)
        {
            hash.Append(inv, $"food/{GoQuote(f.Name)}/{GoQuoteList(f.Definitions)}\n");
        }
        foreach (var d in drug)
        {
            hash.Append(inv, $"drug/{GoQuote(d.Name)}/{string.Join(',', d.Entries.Select(e => $"{e.Drug}:{e.Joy}:{e.Scheduled}"))}\n");
        }
        var methodId = new MethodId("work-" + RoundsAcquisitionPlanner.HashHex16(hash.ToString()));
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
        foreach (var d in drug)
        {
            actions.Add(DomainAction.NewDrugPolicyAction(new ActionId($"{id}-{actions.Count}"), d));
        }
        foreach (var s in settings)
        {
            actions.Add(DomainAction.NewPawnSettingsAction(new ActionId($"{id}-{actions.Count}"), s));
        }
        foreach (var choice in construction)
        {
            var action = DomainAction.NewBuildingAction(new ActionId($"{id}-{actions.Count}"), choice.Site.Building ?? throw new InvalidOperationException("a construction choice names a site with a building"), ConstructionTierRules.AdoptedTier(choice.Site));
            actions.Add(action.WithFinishingSkill(choice.Minimum, choice.Site.Id));
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, need.Epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("work assignments: the session moved");
        }
        var commit = p.Journal.CommitClaimedMethod(need.Need.Id, need.Owner.Snapshot, methodId, "", spec, call);
        if (commit.Outcome == ClaimOutcome.Continued)
        {
            return WaitFor(Cause.MethodUsed, "work_method");
        }
        return AdminRounds.OutcomeVerdict([commit]) ?? BuildingReasonAdmitted;
    }
}

partial class RoundsWorkPlanner
{
    /// <summary>A Method of this planner: <c>work-&lt;hash&gt;</c>.</summary>
    internal static bool IsWorkMethod(MethodId method) => method.Value.StartsWith("work-", StringComparison.Ordinal);

    /// <summary>
    /// The planner's own open plans under the admin/work-assignments Need. The coupled Methods share the Need (#3304)
    /// and hold their pawns for whole reviews, so counting them as this planner's existing work left the priorities and
    /// finishing-skill choices unwritten (#3557).
    /// </summary>
    internal static List<PlanState> OpenWorkPlans(JournalStore journal, NeedOwner owner, CancellationToken call)
    {
        var open = new List<PlanState>();
        foreach (var method in owner.Methods)
        {
            if (!IsWorkMethod(method.Method))
            {
                continue;
            }
            var plan = journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                open.Add(plan);
            }
        }
        return open;
    }

    /// <summary>
    /// Cancels the not-yet-dispatched work and settings actions of an open plan of the admin/work-assignments Need that
    /// the fresh review no longer wants.
    /// </summary>
    internal static void CancelStaleWorkActions(StoreJournal journal, PlanState plan, IReadOnlyList<WorkAssignment> fresh, IReadOnlyList<PawnSettings> settings, IReadOnlyList<DrugPolicy> drug, CancellationToken ct)
    {
        var wantedSettings = new HashSet<PawnSettings>(settings);
        var wantedDrug = new HashSet<DrugPolicy>(drug);
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
            if (action.DrugPolicy is { } policy)
            {
                staleSettings[action.Id] = !wantedDrug.Contains(policy);
            }
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

    /// <summary>The reading-policy writes and assignments the review's policies, books and skill goals call for.</summary>
    internal static IReadOnlyList<ReadingPolicyChange> RoundsReadingChanges(PbObservations.Policies policies, IReadOnlyList<Book> books, Fact<PrisonerCensus> population, IReadOnlyList<WorkPawn> pawns, IReadOnlyList<SkillGoal> training)
    {
        if (!population.TryGetValue(out var census) || books.Count == 0)
        {
            return [];
        }
        var entries = policies.Reading.Select(e => new ReadingPolicyEntry { Id = e.Id, Label = e.Label, Allowed = e.AllowedDefs }).ToList();
        return ReadingPolicyRules.ReadingPolicyChanges(pawns, census.Names, entries, books, training);
    }

    /// <summary>The food-policy writes and assignments the review's policies and reserve call for.</summary>
    internal static IReadOnlyList<FoodPolicyChange> RoundsDietChanges(Fact<Ideoligion> ideology, PbObservations.Policies policies, IReadOnlyList<Food> foods, IReadOnlyList<FoodEater> eaters, Fact<PrisonerCensus> population, IReadOnlyList<WorkPawn> pawns, Fact<FoodReserveReview> reserve, PreferencesRecord preferences)
    {
        if (!population.TryGetValue(out var census) || foods.Count == 0)
        {
            return [];
        }
        var entries = policies.Food.Select(e => new FoodPolicyEntry { Label = e.Label, Pawns = e.Pawns, Allowed = e.AllowedDefs }).ToList();
        reserve.TryGetValue(out var stock);
        return FoodPolicyRules.DietPolicyChanges(ideology, pawns, eaters, census.Names, entries, foods, stock?.IsShort ?? false, preferences);
    }

    /// <summary>Every native drug policy as the drug planners read it: its label, holders and the entries that allow anything.</summary>
    internal static List<OwnedDrugPolicy> OwnedDrugPolicies(PbObservations.Policies policies) =>
        [.. policies.Drug.Select(e => new OwnedDrugPolicy(e.Label, e.Pawns, [.. e.DrugEntries.Select(d => new DrugPolicyEntry(d.DrugDef, d.AllowedForJoy, d.AllowedForAddiction, d.AllowScheduled, d.DaysFrequency, d.OnlyIfMoodBelow, d.OnlyIfJoyBelow, d.TakeToInventory))]))];

    /// <summary>A string in double quotes, with backslashes and quotes escaped.</summary>
    internal static string GoQuote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Strings each in double quotes, separated by spaces, in brackets.</summary>
    internal static string GoQuoteList(IEnumerable<string> items) => "[" + string.Join(' ', items.Select(GoQuote)) + "]";
}
