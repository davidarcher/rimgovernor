using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsSurgeryResult : IPlannerMethod
{
    public Verdict Verdict { get; set; }
    public PlanId Plan { get; set; }
    public IReadOnlyList<Pol.SurgeryWant> Wants { get; set; } = [];

    /// <summary>Set when the plan carries an organ harvest or an artificial part recovery.</summary>
    public bool Harvest { get; set; }
}

/// <summary>
/// The medical/surgery Need's work (#3258): one plan of surgery actions, at most one per patient, from a fresh pawn
/// care read, under the Need and admitted by claims (a patient's operation claims its recipe and part). A patient
/// with an open surgery action of the Need is in flight. The Need settles on the health change (the operation
/// leaving the census), never on the bill. When nothing can be queued the first want (part short or no capable
/// doctor) is the step's reason.
/// </summary>
public sealed partial class RoundsSurgeryPlanner
{
    private readonly Rounder _reviewer;

    private RoundsSurgeryPlanner(Rounder reviewer) => _reviewer = reviewer;

    public static RoundsSurgeryPlanner Create(Rounder? reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsSurgeryPlanner: reviewer == nil");
        }
        return new RoundsSurgeryPlanner(reviewer);
    }

    internal async Task<RoundsSurgeryResult> StepAsync(NeedWork work, CancellationToken call)
    {
        var p = _reviewer.Player;
        var state = work.State;
        var arbiter = work.Arbiter!;
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsSurgeryResult { Verdict = BuildingReasonNoReview };
        }
        Observation.Identity expected;
        try
        {
            expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, call).ConfigureAwait(false);
        var inFlight = new Dictionary<PawnId, bool>();
        foreach (var method in work.Owner.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.Surgery is { } s && WorkProgress.Open([progress]))
                {
                    inFlight[s.Pawn] = true;
                }
            }
        }

        var surgery = read.Projection.SurgeryContext();
        var selection = SurgeryRules.SelectSurgery(read.Projection.Facts.MedicalPawns, inFlight.Keys.ToHashSet(), surgery);
        var result = new RoundsSurgeryResult { Wants = selection.Wants };
        var queue = new List<Pol.SurgeryChoice>();
        foreach (var choice in selection.Queue)
        {
            if (arbiter.TryClaim([choice.Pawn]))
            {
                queue.Add(choice);
            }
        }
        // Organ harvest: at most one, from a prisoner the colony would not recruit, for a colonist's part-short
        // organ or a silver runway deficit, when its gain outweighs the mood and goodwill cost.
        var stock = new Dictionary<Pol.Resource, long>();
        foreach (var row in read.Projection.Facts.Resources)
        {
            stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
        }
        var shortfall = SilverGapRules.RoundsSilverShort(read.Projection.Facts, _reviewer.Policy, review.Latches.MedicalReserve);
        var needs = OrganHarvestRules.OrganNeeds(read.Projection.Facts.MedicalPawns, selection.Wants, shortfall, stock, read.Projection.Facts.Recipes);
        var harvest = OrganHarvestRules.SelectOrganHarvest(read.Projection.Facts.Population, needs, inFlight);
        if (harvest is null)
        {
            // Artificial part recovery: the same one-at-a-time slot.
            var parts = OrganHarvestRules.PartRecoveryNeeds(read.Projection.Facts.MedicalPawns, selection.Wants, read.Projection.Facts.Recipes);
            harvest = OrganHarvestRules.SelectPartRecovery(read.Projection.Facts.Population, parts, inFlight, read.Projection.Facts.Recipes);
        }
        // Prisoners stay on herbal: a cut only better medicine could serve is refused; the review asks for herbal instead.
        var harvesting = harvest is not null && arbiter.TryClaim([harvest.Prisoner]);
        result.Harvest = harvesting;
        if (queue.Count == 0 && !harvesting)
        {
            if (inFlight.Count > 0)
            {
                result.Verdict = BuildingReasonExistingWork;
            }
            else if (selection.Wants.Count > 0)
            {
                result.Verdict = AwaitingMethod(selection.Wants[0].Reason);
            }
            else
            {
                result.Verdict = WaitFor(Pol.Cause.MethodUsed, "restore_queue");
            }
            return result;
        }
        // The tick keys the method: a failed surgery leaves the part missing, and the same recipe is queued again on a later review.
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{work.Need.Id.Value}/{expected.Tick.Value}\n");
        foreach (var choice in queue)
        {
            text.Append(CultureInfo.InvariantCulture, $"{choice.Pawn}/{choice.Recipe}/{choice.Part}\n");
        }
        if (harvest is not null && harvesting)
        {
            text.Append(CultureInfo.InvariantCulture, $"harvest/{harvest.Prisoner}/{harvest.Recipe}/{harvest.Part}\n");
        }
        var method2 = new MethodId("restore-" + RoundsAcquisitionPlanner.HashHex16(text.ToString()));
        if (p.Journal.OwnerMethodPlan(work.Owner, method2, call) is not null)
        {
            result.Verdict = WaitFor(Pol.Cause.MethodUsed, "restore_method");
            return result;
        }
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>(queue.Count);
        for (var i = 0; i < queue.Count; i++)
        {
            var choice = queue[i];
            var s = new Surgery(choice.Pawn, choice.Recipe, choice.Part, false);
            actions.Add(DomainAction.NewSurgeryAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), s));
        }
        if (harvest is not null && harvesting)
        {
            var cut = new Surgery(harvest.Prisoner, harvest.Recipe, harvest.Part, harvest.Violation);
            actions.Add(DomainAction.NewSurgeryAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), cut));
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, work.Epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }
        var commit = p.Journal.CommitOwnerWork(work.Owner, method2, "", spec, call);
        result.Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit);
        result.Plan = commit.Outcome == Sto.ClaimOutcome.Admitted ? id : default;
        return result;
    }
}

partial class RoundsSurgeryPlanner
{
    /// <summary>The first 16 bytes of the SHA-256 of the text as lowercase hex.</summary>
}
