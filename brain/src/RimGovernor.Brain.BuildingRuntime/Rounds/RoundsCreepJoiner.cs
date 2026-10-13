using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The creep joiner work one review found: weapon drops, surgical inspections, isolation moves and arrested ones to disarm.</summary>
internal sealed class CreepJoinerWork
{
    public IReadOnlyList<WeaponDrop> Drops = [];
    public Inspections Inspections = new();
    public IReadOnlyList<IsolationMove> Isolation = [];
    public DisarmWork Disarm = new();
}

/// <summary>The creep joiner work of the latest review, handed to the planner of the same tick.</summary>
internal sealed class CreepJoinerMemory
{
    private readonly object _mu = new();
    private string _world = "";
    private Tick _tick;
    private CreepJoinerWork _work = new();
    private bool _known;

    internal Fact<bool> Review(GenerationSnapshot current, Tick tick, ColonyProjection facts, BridgeRoundsFrame frame, CreepJoinerRecord record)
    {
        var owed = Unknown<bool>();
        var work = new CreepJoinerWork();
        if (CreepJoinerHands(frame) is { } hands)
        {
            var downsides = frame.Catalog.CreepJoinerDownsides();
            (work.Drops, owed) = downsides.WeaponDrops(hands);
            work.Inspections = downsides.Inspections(hands, frame.Catalog.SurgicalInspectionRecipes(), record);
            if (work.Inspections.Owed(record))
            {
                owed = Known(true);
            }
            facts.Facts.IsolationArea.TryGetValue(out var area);
            work.Isolation = downsides.IsolationMoves(hands, work.Inspections.Record, area, IsolationReady(facts));
            if (work.Isolation.Count > 0)
            {
                owed = Known(true);
            }
        }
        if (facts.Facts.Prisoners.TryGetValue(out var prisoners))
        {
            try
            {
                var disarm = frame.Catalog.CreepJoinerDisarm();
                work.Disarm = disarm.Orders(prisoners);
                if (work.Disarm.Orders.Count > 0)
                {
                    owed = Known(true);
                }
            }
            catch (DomainException)
            {
                // The disarm recipe is not in the catalog: nothing to order.
            }
        }
        lock (_mu)
        {
            _world = StockpileWorld(current);
            _tick = tick;
            _work = work;
            _known = owed.IsKnown;
        }
        return owed;
    }

    internal bool Take(string world, Tick tick, out CreepJoinerWork work)
    {
        lock (_mu)
        {
            if (_world != world || _tick != tick || !_known)
            {
                work = new CreepJoinerWork();
                return false;
            }
            work = _work;
            return true;
        }
    }
}

public sealed class RoundsCreepJoinerResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

/// <summary>Holds back the creep joiners until their downside shows: weapon drop, surgical inspection, isolation and disarm orders.</summary>
public sealed class RoundsCreepJoinerPlanner
{
    private readonly Rounder _reviewer;
    private readonly CreepJoinerMemory _memory;

    public RoundsCreepJoinerPlanner(Rounder reviewer)
    {
        if (reviewer is null || !reviewer.MethodEnabled(PolicyApi.ManageCreepJoiners))
        {
            throw new ControlException("NewRoundsCreepJoinerPlanner: reviewer == nil || !reviewer.methodEnabled(policy.ManageCreepJoiners)");
        }
        reviewer.CreepJoiners = new CreepJoinerMemory();
        _reviewer = reviewer;
        _memory = reviewer.CreepJoiners;
    }

    internal RoundsCreepJoinerResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsCreepJoinerResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsCreepJoinerResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.ManageCreepJoiners, call);
        if (!workable)
        {
            return new RoundsCreepJoinerResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan0 = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan0))
            {
                return new RoundsCreepJoinerResult { Verdict = BuildingReasonExistingWork };
            }
        }
        if (!_memory.Take(StockpileWorld(state.Snapshot), review.Tick, out var work))
        {
            return new RoundsCreepJoinerResult { Verdict = BuildingReasonNoReview };
        }
        var was = PolicyApi.ParseCreepJoinerRecord(goal.Standard.Record);
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        var claimed = new List<PawnId>();
        ActionId Next() => new(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}"));
        foreach (var drop in work.Drops)
        {
            var pawn = new PawnId(drop.Pawn);
            if (!arbiter.TryClaim([pawn]))
            {
                continue;
            }
            claimed.Add(pawn);
            actions.Add(DomainAction.NewDropEquipmentAction(Next(), new DropEquipment(pawn, drop.Weapon)));
        }
        foreach (var move in work.Isolation)
        {
            var pawn = new PawnId(move.Pawn);
            if (!arbiter.TryClaim([pawn]))
            {
                continue;
            }
            claimed.Add(pawn);
            var assignment = new AreaAssignment(pawn, move.Area == "", move.Area);
            actions.Add(DomainAction.NewWorkAssignmentAction(Next(), assignment));
        }
        var ordered = work.Inspections.Record;
        foreach (var order in work.Inspections.Orders)
        {
            var pawn = new PawnId(order.Pawn);
            if (!arbiter.TryClaim([pawn]))
            {
                ordered.Inspections.Remove(order.Pawn);
                continue;
            }
            claimed.Add(pawn);
            actions.Add(DomainAction.NewSurgeryAction(Next(), new Surgery(pawn, order.Recipe, order.Part, false)));
        }
        foreach (var order in work.Disarm.Orders)
        {
            var pawn = new PawnId(order.Pawn);
            if (!arbiter.TryClaim([pawn]))
            {
                continue;
            }
            claimed.Add(pawn);
            actions.Add(DomainAction.NewSurgeryAction(Next(), new Surgery(pawn, order.Recipe, order.Part, false)));
        }
        if (actions.Count == 0)
        {
            if (ordered.Encode() != was.Encode())
            {
                p.Current(call, epoch);
                p.Journal.RecordStandard(goal.Standard.Id, goal.Revision, ordered.Encode(), call);
                return new RoundsCreepJoinerResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "creepjoiner_order_recorded") };
            }
            if (work.Inspections.Waiting.Count > 0)
            {
                return new RoundsCreepJoinerResult { Verdict = AwaitingPlan("creepjoiner_inspection", "") };
            }
            return new RoundsCreepJoinerResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "creepjoiner_order") };
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }
        var methodId = NextWaveMethod(goal, "creepjoiner-drop-");
        var reason = $"creepjoiner: [{string.Join(' ', claimed.Select(c => c.Value))}] are held back (weapon dropped, surgical inspection, isolation room, arrested ones disarmed) until their downside shows";
        p.Journal.CommitMethodRecord(goal.Standard.Id, goal.Revision, methodId, reason, spec, ordered.Encode(), call);
        return new RoundsCreepJoinerResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The creep joiner record of the standard bound to the creep joiner concern; empty while there is no review or standard.</summary>
    internal static CreepJoinerRecord CreepJoinerRecordOf(CancellationToken ct, StoreJournal journal)
    {
        RoundsReview review;
        try
        {
            review = journal.LoadRounds(ct);
        }
        catch (NotFoundException)
        {
            return new CreepJoinerRecord();
        }
        foreach (var binding in review.Standards)
        {
            if (binding.Concern != PolicyApi.ManageCreepJoiners)
            {
                continue;
            }
            var state = journal.LoadStandard(binding.Standard, ct);
            return PolicyApi.ParseCreepJoinerRecord(state.Standard.Record);
        }
        return new CreepJoinerRecord();
    }

    /// <summary>The hands of the colonists when the frame's pawn census is complete; null otherwise.</summary>
    internal static List<CreepJoinerHand>? CreepJoinerHands(BridgeRoundsFrame frame)
    {
        if (frame.Pawns is null || !frame.Emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete
            || frame.Pawns.Pawns.Count != frame.Emergency.Facts.Colonists.Count)
        {
            return null;
        }
        return frame.Pawns.Pawns.Select(BridgeApi.CreepJoinerHand).ToList();
    }

    internal static IsolationPlanning CreepJoinerIsolation(ColonyProjection facts, BridgeRoundsFrame frame, CreepJoinerRecord record)
    {
        var output = new IsolationPlanning { Pawns = Unknown<IReadOnlyList<PawnId>>(), Beds = facts.Shapes.Furniture.SleepingBeds() };
        if (CreepJoinerHands(frame) is { } hands)
        {
            output.Pawns = frame.Catalog.CreepJoinerDownsides().Isolated(hands, record);
        }
        return output;
    }

    internal static bool IsolationReady(ColonyProjection facts)
    {
        var (need, owed) = IsolationRoomNeed(facts);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var (ground, gk) = ColonyGround(facts);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!owed || !pk || !gk || !ck || !census!.Colony)
        {
            return false;
        }
        return PolicyApi.IsolationRoomStanding(plan!, plan!.GroundWithRock(ground, NaturalRock(facts)), census.Buildings, need, FurnitureDefinitions(facts));
    }
}
