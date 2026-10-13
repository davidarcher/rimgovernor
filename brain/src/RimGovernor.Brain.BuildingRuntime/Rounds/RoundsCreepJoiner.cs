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

    internal Fact<bool> Review(GenerationSnapshot current, Tick tick, ColonyProjection facts, BridgeRoundsFrame frame, DefinitionCatalog? catalog, CreepJoinerRecord record)
    {
        var owed = Unknown<bool>();
        var work = new CreepJoinerWork();
        if (CreepJoinerHands(frame) is { } hands)
        {
            var defs = catalog ?? throw new ControlException("creepJoiner review: no definition catalog");
            var downsides = defs.CreepJoinerDownsides();
            (work.Drops, owed) = downsides.WeaponDrops(hands);
            work.Inspections = downsides.Inspections(hands, defs.SurgicalInspectionRecipes(), record);
            if (work.Inspections.Owed(record))
            {
                owed = Known(true);
            }
            facts.Facts.IsolationArea.TryGetValue(out var area);
            work.Isolation = downsides.IsolationMoves(hands, work.Inspections.Record, area ?? "", IsolationReady(facts));
            if (work.Isolation.Count > 0)
            {
                owed = Known(true);
            }
        }
        if (facts.Facts.Prisoners.TryGetValue(out var prisoners))
        {
            try
            {
                var disarm = (catalog ?? throw new ControlException("creepJoiner review: no definition catalog")).CreepJoinerDisarm();
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

public sealed class RoundsCreepJoinerResult : IPlannerMethod
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

    internal async Task<RoundsCreepJoinerResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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
        var review = await p.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsCreepJoinerResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = await p.Journal.WorkableAsync(review, PolicyApi.ManageCreepJoiners, call).ConfigureAwait(false);
        if (!workable || goal is null)
        {
            return new RoundsCreepJoinerResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan0 = await p.Journal.LoadPlanAsync(method.Plan, call).ConfigureAwait(false);
            if (StorePackage.PlanOpen(plan0))
            {
                return new RoundsCreepJoinerResult { Verdict = BuildingReasonExistingWork };
            }
        }
        if (!_memory.Take(StockpileWorld(state.Snapshot), review.Tick, out var work))
        {
            return new RoundsCreepJoinerResult { Verdict = BuildingReasonNoReview };
        }
        var was = CreepJoinerRecord.Parse(goal.Standard.Record ?? "");
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        var claimed = new List<PawnId>();
        ActionId Next() => new(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}"));
        foreach (var drop in work.Drops)
        {
            var pawn = drop.Pawn;
            if (!arbiter.TryClaim([pawn]))
            {
                continue;
            }
            claimed.Add(pawn);
            actions.Add(DomainAction.NewDropEquipmentAction(Next(), new DropEquipment((Domain.PawnId)pawn, drop.Weapon)));
        }
        foreach (var move in work.Isolation)
        {
            var pawn = move.Pawn;
            if (!arbiter.TryClaim([pawn]))
            {
                continue;
            }
            claimed.Add(pawn);
            var assignment = WorkAssignment.NewAreaAssignment((Domain.PawnId)pawn, move.Area == "", move.Area);
            actions.Add(DomainAction.NewWorkAssignmentAction(Next(), assignment));
        }
        var ordered = work.Inspections.Record;
        foreach (var order in work.Inspections.Orders)
        {
            var pawn = order.Pawn;
            if (!arbiter.TryClaim([pawn]))
            {
                ordered = ordered with { Inspections = ordered.Inspections.Where(entry => entry.Key != order.Pawn).ToDictionary(entry => entry.Key, entry => entry.Value) };
                continue;
            }
            claimed.Add(pawn);
            actions.Add(DomainAction.NewSurgeryAction(Next(), new Surgery((Domain.PawnId)pawn, order.Recipe, order.Part, false)));
        }
        foreach (var order in work.Disarm.Orders)
        {
            var pawn = order.Pawn;
            if (!arbiter.TryClaim([pawn]))
            {
                continue;
            }
            claimed.Add(pawn);
            actions.Add(DomainAction.NewSurgeryAction(Next(), new Surgery((Domain.PawnId)pawn, order.Recipe, order.Part, false)));
        }
        if (actions.Count == 0)
        {
            if (ordered.Encode() != was.Encode())
            {
                p.Current(call, epoch);
                await p.Journal.RecordStandardAsync(goal.Standard.Id, goal.Revision, ordered.Encode(), call).ConfigureAwait(false);
                return new RoundsCreepJoinerResult { Verdict = WaitFor(Cause.MethodUsed, "creepjoiner_order_recorded") };
            }
            if (work.Inspections.Waiting.Count > 0)
            {
                return new RoundsCreepJoinerResult { Verdict = AwaitingPlan("creepjoiner_inspection", "") };
            }
            return new RoundsCreepJoinerResult { Verdict = WaitFor(Cause.MethodUsed, "creepjoiner_order") };
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }
        var methodId = NextWaveMethod(goal, "creepjoiner-drop-");
        var reason = $"creepjoiner: [{string.Join(' ', claimed.Select(c => c.Value))}] are held back (weapon dropped, surgical inspection, isolation room, arrested ones disarmed) until their downside shows";
        await p.Journal.CommitMethodRecordAsync(goal.Standard.Id, goal.Revision, methodId, reason, spec, ordered.Encode(), call).ConfigureAwait(false);
        return new RoundsCreepJoinerResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The creep joiner record of the standard bound to the creep joiner concern; empty while there is no review or standard.</summary>
    internal static CreepJoinerRecord CreepJoinerRecordOf(StoreJournal journal, CancellationToken ct)
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
            return CreepJoinerRecord.Parse(state.Standard.Record ?? "");
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
        return frame.Pawns.Pawns.Select(CreepJoinerHandOf).ToList();
    }

    /// <summary>Go bridge.CreepJoinerHand: lifts a combat-detail pawn row into the weapon drop's facts (the downside check's facts, whether the colonist can take an order, its weapon, surgery operations and bills, allowed area and hunger).</summary>
    internal static CreepJoinerHand CreepJoinerHandOf(PbObservations.PawnState row)
    {
        var hand = new CreepJoinerHand
        {
            Pawn = new PawnId(row.Pawn?.Id ?? ""),
            Facts = BridgeApi.CreepJoinerPawn(row),
            Available = Unknown<bool>(),
            Weapon = Unknown<string>(),
            Operations = Unknown<EquatableArray<SurgeryOperation>>(),
            QueuedSurgeries = Unknown<int>(),
            Area = Unknown<string>(),
            Hungry = Unknown<bool>(),
        };
        if (row.Health is { } health && !Boundary.IssueField(row.Issues, "health"))
        {
            // The weapon drop and inspections read recipe names only, no part items.
            hand = hand with
            {
                Operations = BridgeApi.SurgeryFacts(health, null).Operations,
                QueuedSurgeries = BridgeApi.QueuedSurgeries(health),
                QueuedRecipes = BridgeApi.QueuedSurgeryRecipes(health) is { } recipes ? EquatableArray.From(recipes) : null,
            };
        }
        // An unset allowed area is an unrestricted pawn (native AllowedArea).
        if (row.Settings is { } settings && !Boundary.IssueField(settings.Issues, "allowed_area_id"))
        {
            hand = hand with { Area = Known((settings.AllowedAreaId ?? "")) };
        }
        if (row.Needs is { HungerCategory: not null } needs && !Boundary.IssueField(needs.Issues, "hunger_category"))
        {
            hand = hand with { Hungry = Known(needs.HungerCategory.GetValueOrDefault() != RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed) };
        }
        var mental = BridgeApi.CellPresence(row.MentalState is not null ? (row.MentalState ?? "") : null, row.Issues, "mental_state", false);
        if (row.Dead.GetValueOrDefault() || row.Downed.GetValueOrDefault() || row.Drafted.GetValueOrDefault())
        {
            hand = hand with { Available = Known(false) };
        }
        else if (mental.TryGetValue(out var inMental) && row.Dead is not null && row.Downed is not null && row.Drafted is not null)
        {
            hand = hand with { Available = Known(!inMental) };
        }
        if (row.Equipment is { Armed: not null } equipment && !Boundary.IssueField(equipment.Issues, "armed"))
        {
            if (!equipment.Armed.GetValueOrDefault())
            {
                hand = hand with { Weapon = Known("") };
            }
            else if (equipment.PrimaryId is not null)
            {
                hand = hand with { Weapon = Known((equipment.PrimaryId ?? "")) };
            }
        }
        return hand;
    }

    internal static IsolationPlanning CreepJoinerIsolation(ColonyProjection facts, BridgeRoundsFrame frame, DefinitionCatalog? catalog, CreepJoinerRecord record)
    {
        var output = new IsolationPlanning { Pawns = Unknown<EquatableArray<PawnId>>(), Beds = facts.Shapes.Furniture.SleepingBeds() };
        if (CreepJoinerHands(frame) is { } hands)
        {
            var defs = catalog ?? throw new ControlException("creepJoinerIsolation: no definition catalog");
            output = output with { Pawns = defs.CreepJoinerDownsides().Isolated(hands, record) };
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
