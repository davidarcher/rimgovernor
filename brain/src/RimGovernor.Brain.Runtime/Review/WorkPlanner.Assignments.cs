using System.Globalization;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

/// <summary>The native gear-bench read the work planner counts bench work from.</summary>
public interface IReviewWorkBenchSource
{
    Task<IReadOnlyList<GearBenchRead>> ReadGearBenchesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct);
}

/// <summary>
/// The admin/work-assignments Need's step (#3304): the colony's work priorities, pawn settings, reading, food and drug
/// policies and construction finishing-skill choices, committed as one Method under the Need. The MethodId names the
/// changes it writes, so the same changes are committed once; an open plan whose actions the fresh review no longer
/// wants has them cancelled first.
/// </summary>
public sealed partial class WorkPlanner
{
    private readonly ColonyReview _reviewer;
    private readonly IReviewWorkBenchSource _benches;

    public WorkPlanner(ColonyReview reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("WorkPlanner: reviewer is missing");
        }
        _reviewer = reviewer;
        _benches = (IReviewWorkBenchSource)reviewer.Native;
    }

    internal async Task<Verdict> StepAsync(NeedWork need, CancellationToken call)
    {
        var p = _reviewer.Player;
        var state = need.State;
        var review = p.Journal.LoadReview(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return BuildingReasonNoReview;
        }
        var open = OpenWorkPlans(p.Journal, need.Owner, call);
        Verdict Existing(Verdict unknown) => open.Count > 0 ? BuildingReasonExistingWork : unknown;

        var plans = p.Journal.LoadPlans(call);
        var definitions = ProjectBillReads.ProjectDefinitions(plans, state.Snapshot);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false);
        var pawns = read.Reading.Facts.ForecastObserved.WorkPawns.Pawns;
        var construction = ConstructionSkillRules.ConstructionSkillChoices(read.Reading.Facts.CurrentConstruction, pawns).ToList();
        if (construction.Count > 8)
        {
            construction = construction[..8];
        }
        var required = WorkDemandRules.ProjectWork(definitions, read.Reading.Facts.Definitions);
        var resourceTargets = _reviewer.ResourceTargets(state.Snapshot);
        // The resource targets are in deficit when the economy/resource Need's threshold reads short now.
        var deficit = !ResearchRules.ResourceTargetNeed(resourceTargets, read.Reading.Facts.ObservedStock()).Recovered;
        var targets = WorkDemandRules.DeficitTargets(resourceTargets, deficit, read.Reading.Facts.Gear);
        var benchWork = await ProjectBillReads.BenchWorkAsync(_reviewer.BenchSource(_benches, expected, false), state.Snapshot, plans, targets, targets.Count > 0, read.Reading.Facts.Items.Wort, call).ConfigureAwait(false);

        required = WorkDemandRules.MergeWorkRequirements([.. required], benchWork);
        var needs = ProjectBillReads.ResearchNeeds(p.Journal, state.Snapshot, _reviewer.EquipmentOwed(state.Snapshot), call);
        needs = _reviewer.CensusResearchNeeds(needs);
        required = WorkDemandRules.MergeWorkRequirements([.. required], WorkDemandRules.ResearchWork(needs, read.Reading.Facts.Research));
        required = WorkDemandRules.MergeWorkRequirements([.. required], FieldPlanner.FishingWork(read.Reading));
        required = WorkDemandRules.MergeWorkRequirements([.. required], EntityStudyRules.StudyWork(read.Reading.Facts.Containment));
        var demand = Disease.DiseaseDemand(read.Reading, definitions, review, state.Snapshot);
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
        var reserve = MedicalRules.ReviewMedicalReserve(read.Reading.Facts.MedicalReserve, false, _reviewer.Policy.MedicalReserve);
        settings.AddRange(MedicineCarryRules.MedicineCarryChanges(MedicineCarryRules.MedicineCarryRows(pawns), reserve.Stock, reserve.Target));
        settings.AddRange(PawnRules.NicknameChanges(read.Reading.Facts.Population.Names));
        var care = MedicalRules.MedicalCareChanges(read.Reading.Facts, _reviewer.Policy.MedicalReserve).ToList();
        if (care.Count > 8)
        {
            care = care[..8];
        }
        settings.AddRange(care);
        settings.AddRange(Mech.MechSettings(read));
        var reading = new List<ReadingPolicy>();
        var readingChanges = ReadingChanges(read.Reading.Snapshot.Policies, read.Reading.Facts.Books, read.Reading.Facts.Population, pawns, read.Reading.Facts.GoalTraining);
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
        var dietChanges = DietChanges(read.Reading.Facts.Ideology, read.Reading.Snapshot.Policies, read.Reading.Facts.Foods, read.Reading.Facts.FoodEaters, read.Reading.Facts.Population, pawns, read.Reading.Facts.FoodReserve, read.Reading.Facts.Preferences);
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
        var drugChanges = DrugPolicyPlanRules.Changes(read.Reading.Facts.Preferences, read.Reading.Facts.Items, pawns, OwnedDrugPolicies(read.Reading.Snapshot.Policies));
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
            var hostility = s is PawnSettings.HostilitySetting h ? h.Mode : default;
            var selfTend = s is PawnSettings.SelfTendSetting t && t.On;
            var leaveName = s is PawnSettings.NicknameSetting nick ? nick.LeaveName : "";
            var carry = s is PawnSettings.MedicineCarrySetting c ? c.Count : 0;
            var medicalCare = s is PawnSettings.MedicalCareSetting mc ? mc.Care : default;
            var readingName = s is PawnSettings.ReadingPolicySetting rp ? rp.Name : "";
            var drugName = s is PawnSettings.DrugPolicySetting dp ? dp.Name : "";
            var foodName = s is PawnSettings.FoodPolicySetting fp ? fp.Name : "";
            var workMode = s is PawnSettings.MechWorkModeSetting wm ? wm.Mode : "";
            var group = s is PawnSettings.MechControlGroupSetting g ? g.Group : 0;
            hash.Append(
                inv,
                $"{s.Kind}/{s.Pawn}/{hostility}/{selfTend}/{GoQuote(leaveName)}/{carry}/{medicalCare}/{GoQuote(readingName)}/{GoQuote(drugName)}/{GoQuote(foodName)}/{GoQuote(workMode)}/{group}\n"
            );
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
        var methodId = new MethodId("work-" + AcquisitionPlanner.HashHex16(hash.ToString()));
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
        return AdminReview.OutcomeVerdict([commit]) ?? BuildingReasonAdmitted;
    }
}

partial class WorkPlanner
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

    internal static List<HostilityPawn> HostilityRows(IReadOnlyList<WorkPawn> pawns) => [.. pawns.Select(p => p.HostilityPawn()).OfType<HostilityPawn>()];

    internal static EquatableArray<HostilityPawn> HostilityPawns(WorkPawnsRead pawns) =>
        [.. HostilityRows(pawns.Pawns)];

    /// <summary>The reading-policy writes and assignments the review's policies, books and skill goals call for.</summary>
    internal static IReadOnlyList<ReadingPolicyChange> ReadingChanges(RimGovernor.Brain.GameFacts.Observations.Policies policies, IReadOnlyList<Book> books, PrisonerCensus census, IReadOnlyList<WorkPawn> pawns, IReadOnlyList<SkillGoal> training)
    {
        if (books.Count == 0)
        {
            return [];
        }
        var entries = policies.Reading.Select(e => new ReadingPolicyEntry { Id = e.Id, Label = e.Label, Allowed = e.AllowedDefs }).ToList();
        return ReadingPolicyRules.ReadingPolicyChanges(pawns, census.Names, entries, books, training);
    }

    /// <summary>The food-policy writes and assignments the review's policies and reserve call for.</summary>
    internal static IReadOnlyList<FoodPolicyChange> DietChanges(IdeologyMode ideology, RimGovernor.Brain.GameFacts.Observations.Policies policies, IReadOnlyList<Food> foods, IReadOnlyList<FoodEaterDiet> eaters, PrisonerCensus census, IReadOnlyList<WorkPawn> pawns, FoodReserveState reserve, PreferencesRecord preferences)
    {
        if (foods.Count == 0)
        {
            return [];
        }
        var entries = policies.Food.Select(e => new FoodPolicyEntry { Label = e.Label, Pawns = e.Pawns, Allowed = e.AllowedDefs }).ToList();
        return FoodPolicyRules.DietPolicyChanges(ideology, pawns, eaters, census.Names, entries, foods, reserve is FoodReserveState.Reviewed { Review.IsShort: true }, preferences);
    }

    /// <summary>Every native drug policy as the drug planners read it: its label, holders and the entries that allow anything.</summary>
    internal static List<OwnedDrugPolicy> OwnedDrugPolicies(RimGovernor.Brain.GameFacts.Observations.Policies policies) =>
        [.. policies.Drug.Select(e => new OwnedDrugPolicy(e.Label, e.Pawns, [.. e.DrugEntries.Select(d => new DrugPolicyEntry(d.DrugDef, d.AllowedForJoy, d.AllowedForAddiction, d.AllowScheduled, d.DaysFrequency, d.OnlyIfMoodBelow, d.OnlyIfJoyBelow, d.TakeToInventory))]))];

    /// <summary>A string in double quotes, with backslashes and quotes escaped.</summary>
    internal static string GoQuote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Strings each in double quotes, separated by spaces, in brackets.</summary>
    internal static string GoQuoteList(IEnumerable<string> items) => "[" + string.Join(' ', items.Select(GoQuote)) + "]";
}
