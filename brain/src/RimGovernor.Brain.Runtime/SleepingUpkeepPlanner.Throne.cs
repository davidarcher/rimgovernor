using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

// The throne room: the shelter family's sleeping planner (shelter/sleeping) shells the planned room, places the title's throne and
// furnishes it to the title's impressiveness (Policy NextThroneStep, ThroneRoomTargets). The layout review
// (ReplanLayout) plans the room from the title's minimum area. Once it stands, the throne is assigned to its
// holder through the generic Assign action; the sleeping Need stays unmet until the royalty read lists the
// holder as its owner.
public sealed partial class ColonyReview
{
    /// <summary>Remembers the review's royalty read for the planners' throne definitions and the defense round's calls.</summary>
    internal void ReviewRoyalty(ReviewReading reading) => Census.RememberRoyalty(reading.Reading.Facts.Royalty);

    /// <summary>The throne definitions a planner's read names beside its own: the ladder's, from the review's royalty read.</summary>
    internal IReadOnlyList<string> RememberedThrones() =>
        Census.Remembered() is { } facts ? SleepingUpkeepPlanner.ThroneThings(facts) : [];
}

partial class SleepingUpkeepPlanner
{
    /// <summary>Every throne definition the ladder names, sorted.</summary>
    internal static List<string> ThroneThings(Pol.RoyaltyFacts f)
    {
        var names = new List<string>();
        foreach (var rung in f.Ladder)
        {
            foreach (var thing in rung.Throne.Things)
            {
                if (!names.Contains(thing))
                {
                    names.Add(thing);
                }
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>The throne room the colony owes: a colony without Royalty owes none.</summary>
    internal static (Pol.ThroneNeed Need, bool Owed) ThroneNeed(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        SleepingPlanningRules.ThroneNeed(facts.Facts);

    /// <summary>
    /// The bestowing-ceremony quests the title claim gate allows to accept now (NextTitleClaim, ClaimQuests);
    /// none without Royalty or before the layout plan and construction census are available.
    /// </summary>
    internal static IReadOnlyList<QuestId> TitleClaimQuests(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        SleepingPlanningRules.TitleClaimQuests(facts.Facts, facts.Window is { } window ? window.Value.Cells : null);

    /// <summary>The projection's next throne step; none before the layout plan or construction census is available.</summary>
    internal static Pol.ThroneStep ThroneStepOf(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        SleepingPlanningRules.ThroneStepOf(facts.Facts, facts.Window is { } window ? window.Value.Cells : null);

    /// <summary>
    /// Marks the standing throne room in the flooring census with the title's flooring requirement, so the
    /// flooring review measures it against the required terrain tags. The census rooms are copied: the
    /// projection's own stay untouched. No requirement, no standing room or an unlisted room leaves the census
    /// as read.
    /// </summary>
    internal static Pol.FlooringObservation WithThroneFloor(RimGovernor.Brain.GameAccess.ColonyReading facts, Pol.FlooringObservation v) =>
        SleepingPlanningRules.WithThroneFloor(facts.Facts, v);
}

public sealed partial class SleepingUpkeepPlanner
{
    /// <summary>
    /// Answers a due throne step: the room through the shared build side (ReconcileRoom: the ring, floor and
    /// furniture the template and the plan still owe, installed from packed stock first), the assignment, the
    /// refuel. Furnishing beyond the template follows through the room upgrade.
    /// </summary>
    internal async Task<BuildingResult> StageThroneAsync(StepArbiter arbiter, PackedStock stock, ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, ReviewReading reading, Pol.ThroneStep step, CancellationToken call, CancellationToken epoch)
    {
        switch (step.Kind)
        {
            case Pol.ThroneStepKind.Reconcile:
                var inside = step.Room.Region.Bounds();
                return await Building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile
                {
                    Room = step.Room,
                    Template = step.Template,
                    Forbidden = step.Need.Forbids,
                    Tags = step.Need.FloorTags,
                    Name = string.Create(CultureInfo.InvariantCulture, $"throne-{inside.X}-{inside.Z}"),
                    Reason = "throne room",
                }, call, epoch).ConfigureAwait(false);
            case Pol.ThroneStepKind.Refuel:
                return RefuelThrone(arbiter, state, review, goal, reading, step, call, epoch);
            case Pol.ThroneStepKind.Assign:
                return AssignThrone(state, review, goal, step, call, epoch);
        }
        return new BuildingResult { Verdict = FieldUnavailable("throne_step") };
    }

    /// <summary>
    /// Commits one Assign of the standing throne to its holder, once per holder and throne per Episode
    /// (refused attempts retry within AssignMethod's bound). The royalty read refreshes on its own cadence, so
    /// a throne assigned this epoch reads unowned until then and the method is used.
    /// </summary>
    internal BuildingResult AssignThrone(ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, Pol.ThroneStep step, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var (method, barred) = AssignMethod(goal, $"throne-assign-{step.Need.Holder}-{step.Throne}", state.Snapshot, call);
        if (!barred.IsZero)
        {
            return new BuildingResult { Verdict = barred };
        }
        if (method.Value == "")
        {
            return new BuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "throne_assignment") };
        }
        var previous = step.PreviousThrone != "" ? PreviousAssignment.KnownPrevious(step.PreviousThrone) : PreviousAssignment.ClearPrevious();
        var assign = new Domain.Assign(step.Need.Holder, step.Throne, previous);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewAssignAction(new ActionId($"{id}-0"), assign);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("assignThrone: p.session.State() != state");
        }
        var latest = p.Journal.LoadReview(call);
        if (latest.Revision != review.Revision || !latest.Enabled)
        {
            throw new ControlException("assignThrone: latest.Revision != review.Revision || !latest.Enabled");
        }
        var commit = p.Journal.CommitOwnerWork(goal, method, "", plan, call);
        return new BuildingResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit) };
    }
}
