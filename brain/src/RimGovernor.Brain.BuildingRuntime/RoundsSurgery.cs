using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsSurgeryResult : IPlannerMethod
{
    public Verdict Verdict { get; set; }
    public PlanId Plan { get; set; }
    public IReadOnlyList<Pol.SurgeryWant> Wants { get; set; } = [];

    /// <summary>Set when the plan carries an organ harvest or an artificial part recovery, or a peg-leg step.</summary>
    public bool Harvest { get; set; }
}

/// <summary>
/// The medical/surgery Need's work (#3258): one plan of surgery actions, at most one per patient, from a fresh pawn
/// care read, under the Need and admitted by claims (a patient's operation claims its recipe and part). A patient
/// with an open surgery action of the Need is in flight. The Need settles on the health change (the operation
/// leaving the census), never on the bill. When nothing can be queued the first want (part short or no capable
/// doctor) is the step's reason.
/// </summary>
public sealed class RoundsSurgeryPlanner
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
            expected = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
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
                if (progress.Action.Surgery is { } s && Standard.WorkOpen([progress]))
                {
                    inFlight[s.Pawn] = true;
                }
            }
        }
        if (!read.Projection.Facts.MedicalPawns.TryGetValue(out _))
        {
            return new RoundsSurgeryResult { Verdict = FieldUnavailable("medical_pawns") };
        }
        var surgery = read.Projection.SurgeryContext();
        var selection = SelectSurgery(read.Projection.Facts.MedicalPawns, inFlight.Keys.ToHashSet(), surgery);
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
        if (read.Projection.Facts.Resources.TryGetValue(out var resources))
        {
            foreach (var row in resources)
            {
                stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
            }
        }
        RoundsSilverShort(read.Projection.Facts, _reviewer.Policy, review.Latches.MedicalReserve).TryGetValue(out var shortfall);
        var needs = OrganNeeds(read.Projection.Facts.MedicalPawns, selection.Wants, shortfall, stock, read.Projection.Facts.Recipes);
        var harvest = SelectOrganHarvest(read.Projection.Facts.Prisoners, read.Projection.Facts.PrisonerColony, needs, inFlight);
        if (harvest is null)
        {
            // Artificial part recovery: the same one-at-a-time slot.
            var parts = PartRecoveryNeeds(read.Projection.Facts.MedicalPawns, selection.Wants, read.Projection.Facts.Recipes);
            harvest = SelectPartRecovery(read.Projection.Facts.Prisoners, read.Projection.Facts.PrisonerColony, parts, inFlight, read.Projection.Facts.Recipes);
        }
        // Prisoners stay on herbal: a cut only better medicine could serve is refused; the review asks for herbal instead.
        if (harvest is null)
        {
            // Peg-leg cycling: training, control or a reinstall.
            var facts = read.Projection.Facts;
            harvest = SelectPegCycle(facts.Prisoners, facts.PrisonerColony, facts.FoodDays, _reviewer.Policy.Prisoners(), selection.Wants, facts.Recipes, inFlight);
        }
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
            text.Append(CultureInfo.InvariantCulture, $"harvest/{harvest.Prisoner}/{harvest.Recipe}/{harvest.Part}/{harvest.Surgeon}\n");
        }
        var method2 = new MethodId("restore-" + HashHex16(text.ToString()));
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
            if (!harvest.Surgeon.IsEmpty)
            {
                cut = cut.WithSurgeon(harvest.Surgeon); // peg-leg training's doctor
            }
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

internal static partial class BuildingRuntimePackage
{
    /// <summary>The first 16 bytes of the SHA-256 of the text as lowercase hex (Go's "%x" of hash.Sum(nil)[:16]).</summary>
}
