namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the colony admin family (#3264, #3304): its full read (the owned rounds census, or the rooms
/// census when burial is composed, the stored creepjoiner record and the review's event-loot cohort) and the work
/// steps of its Needs, each committing under the Need owner and admitted by claims: the creepjoiner orders, the
/// supply-safety batches, the burial rooms and graves, the work ledger's bill reconcile and the colony's work
/// assignments.
/// </summary>
public sealed partial class AdminRounds
{
    private readonly Rounder _reviewer;
    private readonly AdminNeeds _needs;
    private readonly FacadeNativeClient? _supplies;
    private readonly RoundsBuildingPlanner? _building;
    private readonly RoundsStockpilePlanner? _stockpiles;
    private readonly RoundsWorkPlanner? _work;

    public AdminRounds(Rounder reviewer, bool creepJoiners) : this(reviewer, new AdminNeeds { CreepJoiners = creepJoiners }, null)
    {
    }

    /// <summary>
    /// The family with <paramref name="needs"/> composed; supply safety reads and writes through <paramref name="supplies"/>,
    /// the stockpiles Need its zone edits through <paramref name="stockpiles"/>.
    /// </summary>
    public AdminRounds(Rounder reviewer, AdminNeeds needs, FacadeNativeClient? supplies, FacadeNativeClient? stockpiles = null)
    {
        _reviewer = reviewer ?? throw new ControlException("AdminRounds: reviewer is missing");
        _needs = needs ?? throw new ControlException("AdminRounds: needs are missing");
        if (needs.SupplySafety && supplies is null)
        {
            throw new ControlException("AdminRounds: supply safety needs a supply source");
        }
        _supplies = supplies;
        if (needs.Stockpiles)
        {
            _stockpiles = new RoundsStockpilePlanner(reviewer, stockpiles ?? throw new ControlException("AdminRounds: stockpiles need a stockpile source"));
        }
        if (needs.WorkAssignments)
        {
            _work = new RoundsWorkPlanner(reviewer);
        }
        if (needs.Burial && reviewer.Native is IRoundsBuildingSource source)
        {
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source };
        }
        // A creepjoiner held apart owes the isolation room: the review reads the record before the child rooms.
        reviewer.CreepJoinerIsolation = needs.CreepJoiners;
    }

    /// <summary>
    /// An optional entry woken by the outcomes of its surgeries, area moves, supply batches, burial buildings, zone edits,
    /// work assignments, pawn settings and policies.
    /// The runner is critical while supply safety is composed, as its own planner was.
    /// </summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Optional,
        Kinds = [ActionKind.Surgery, ActionKind.WorkAssignment, ActionKind.SupplyAllow, ActionKind.SupplyForbid, ActionKind.Building,
            ActionKind.ProductionBill, ActionKind.RemoveProductionBill, ActionKind.ZoneCreate, ActionKind.ZoneCellEdit, ActionKind.ZoneDelete, ActionKind.StockpilePatch,
            ActionKind.PawnSettings, ActionKind.ReadingPolicy, ActionKind.DrugPolicy, ActionKind.FoodPolicy, ActionKind.PawnActivities],
    };

    /// <summary>The family's scheduler entry; with the work ledger composed it also files the ledger's purpose verdicts.</summary>
    public FamilyRunner Runner() => new FamilyRunner<AdminFacts>(new AdminFamily(_needs), ReadAsync,
        _needs.SupplySafety ? Schedule with { Class = PlannerClass.Critical } : Schedule, WorkAsync, LedgerPurposeReports);

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world: the owned rounds census (the rooms census when
    /// burial is composed) and, for the creepjoiner Need, the stored inspection record and the work the census owes
    /// against it; for supply safety the review's event-loot cohort, unknown while the census has no loot read; for
    /// burial whether a tomb, grave or morgue step is due. Throws when the read is from another world or older than
    /// the review.
    /// </summary>
    internal async Task<AdminFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        if (scope.Immediate)
        {
            // No SeriousHarm Needs: the immediate pass reads nothing.
            return new AdminFacts();
        }
        var journal = _reviewer.Player.Journal;
        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScopeAsync(_reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("admin read: the scope is another world or older than the review");
        }
        var reading = _needs.Burial
            ? await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), BurialDefinitions, ct).ConfigureAwait(false)
            : await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), ct).ConfigureAwait(false);
        var facts = new AdminFacts();
        if (_needs.CreepJoiners)
        {
            var record = await journal.LoadCreepJoinersAsync(Player.PlayerWorld(scope.World), ct).ConfigureAwait(false);
            facts = facts with { CreepJoiners = CreepJoinerWorkOf(reading.Projection, reading.Frame, reading.Catalog, record) };
        }
        if (_needs.SupplySafety)
        {
            facts = facts with
            {
                SupplySafety = reading.Projection.Facts.EventLoot.IsKnown
                    ? Known(EquatableArray.From(review.EventLoot.Pending))
                    : Unknown<EquatableArray<StartingSupply>>(),
            };
        }
        if (_needs.Burial)
        {
            facts = facts with { Burial = BurialOwed(reading.Projection) };
        }
        if (_needs.WorkLedger)
        {
            facts = facts with { WorkLedger = _reviewer.LedgerOwedIn(scope.World) };
        }
        if (_needs.Stockpiles)
        {
            // Whether the review's stockpile proposal is active in this world; unknown until it reviewed this world.
            facts = facts with { Stockpiles = _reviewer.Stockpiles.Active(scope.World) };
        }
        if (_needs.WorkAssignments)
        {
            // Whether the review owed work assignments in this world; unknown until it reviewed this world.
            facts = facts with { WorkAssignments = _reviewer.WorkAssignments.Get(scope.World) };
        }
        return facts;
    }

    /// <summary>The asynchronous step of a ready Need: its orders, batches or buildings; a Need without a step has none.</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, AdminFacts facts, CancellationToken ct)
    {
        if (work.Need.Id == AdminFamily.CreepJoiners && facts.CreepJoiners is { } creep)
        {
            return await Task.Run(() => CreepJoinerStep(_reviewer.Player.Journal, work, creep, ct), ct).ConfigureAwait(false);
        }
        if (work.Need.Id == AdminFamily.SupplySafety && facts.SupplySafety is { } loot && loot.TryGetValue(out var cohort) && _supplies is not null)
        {
            return await SupplyStepAsync(work, cohort, ct).ConfigureAwait(false);
        }
        if (work.Need.Id == AdminFamily.Burial && _needs.Burial)
        {
            return await BurialStepAsync(work, ct).ConfigureAwait(false);
        }
        if (work.Need.Id == AdminFamily.WorkLedger && _needs.WorkLedger)
        {
            return await WorkLedgerStepAsync(work, ct).ConfigureAwait(false);
        }
        if (work.Need.Id == AdminFamily.Stockpiles && _stockpiles is not null)
        {
            return await _stockpiles.StepAsync(work, ct).ConfigureAwait(false);
        }
        if (work.Need.Id == AdminFamily.WorkAssignments && _work is not null)
        {
            return await _work.StepAsync(work, ct).ConfigureAwait(false);
        }
        return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
    }

    /// <summary>
    /// One Method per order: <c>creepjoiner-isolate-&lt;pawn&gt;</c> or <c>creepjoiner-release-&lt;pawn&gt;</c> (an
    /// area-only assignment), <c>creepjoiner-inspect-&lt;pawn&gt;</c> and <c>creepjoiner-disarm-&lt;pawn&gt;</c> (a surgery
    /// bill). Each pawn is claimed once on the step's arbiter, so a pawn gets one order per step as before. An
    /// inspection's record entry is written with its Method; one not admitted leaves the pawn unordered. The record
    /// changes a review alone finds (an ended bill, a pawn gone) are saved after the orders.
    /// </summary>
    internal static Verdict CreepJoinerStep(JournalStore journal, NeedWork work, CreepJoinerWork creep, CancellationToken ct)
    {
        if (creep.Drops.Count > 0)
        {
            return Verdict.AwaitingPlan(AdminFamily.UnsupportedDisarm, AdminFamily.UnsupportedDisarmDetail);
        }
        var need = work.Need.Id;
        var world = work.Owner.Snapshot;
        bool Claim(PawnId pawn) => work.Arbiter?.TryClaim([pawn]) ?? true;
        var outcomes = new List<(ClaimOutcome Outcome, ClaimConflict? Conflict)>();

        void Commit(string method, string reason, Func<ActionId, DomainAction> action)
        {
            var id = PlanIds.MintPlanId();
            var spec = new PlanSpec(id, new PlanRevision(1), [action(new ActionId(id.Value + "-0"))]);
            var commit = journal.CommitClaimedMethod(need, world, new MethodId(method), reason, spec, ct);
            outcomes.Add((commit.Outcome, commit.Conflict));
        }

        foreach (var move in creep.Isolation)
        {
            if (!Claim(move.Pawn))
            {
                continue;
            }
            var release = move.Area.Length == 0;
            Commit((release ? "creepjoiner-release-" : "creepjoiner-isolate-") + move.Pawn.Value,
                release ? $"creepjoiner: {move.Pawn.Value} leaves the isolation area" : $"creepjoiner: {move.Pawn.Value} is held in the isolation area until its downside shows",
                a => DomainAction.NewWorkAssignmentAction(a, WorkAssignment.NewAreaAssignment(move.Pawn, release, move.Area)));
        }

        // The orders' pawns start unordered; each admitted (or still open) inspection marks its pawn ordered.
        var ordering = creep.Inspections.Orders.Select(o => o.Pawn).ToHashSet();
        var record = new Dictionary<PawnId, InspectionStage>(creep.Inspections.Record.Inspections.Where(e => !ordering.Contains(e.Key)));
        foreach (var order in creep.Inspections.Orders)
        {
            if (!Claim(order.Pawn))
            {
                continue;
            }
            var next = new Dictionary<PawnId, InspectionStage>(record) { [order.Pawn] = InspectionStage.Ordered };
            var id = PlanIds.MintPlanId();
            var spec = new PlanSpec(id, new PlanRevision(1), [DomainAction.NewSurgeryAction(new ActionId(id.Value + "-0"), new Surgery(order.Pawn, order.Recipe, order.Part, false))]);
            var commit = journal.CommitCreepJoinerMethod(need, world, new MethodId("creepjoiner-inspect-" + order.Pawn.Value),
                $"creepjoiner: {order.Pawn.Value} gets a surgical inspection while its downside is hidden", spec, new CreepJoinerRecord { Inspections = next }, ct);
            outcomes.Add((commit.Outcome, commit.Conflict));
            if (commit.Outcome != ClaimOutcome.Conflict)
            {
                record = next;
            }
        }

        foreach (var order in creep.Disarm.Orders)
        {
            if (!Claim(order.Pawn))
            {
                continue;
            }
            Commit("creepjoiner-disarm-" + order.Pawn.Value, $"creepjoiner: arrested {order.Pawn.Value} is disarmed ({order.Recipe})",
                a => DomainAction.NewSurgeryAction(a, new Surgery(order.Pawn, order.Recipe, order.Part, false)));
        }

        var left = new CreepJoinerRecord { Inspections = record };
        var recorded = false;
        if (left.Encode() != journal.LoadCreepJoiners(Player.PlayerWorld(world), ct).Encode())
        {
            journal.SaveCreepJoiners(Player.PlayerWorld(world), left, ct);
            recorded = true;
        }

        if (OutcomeVerdict(outcomes) is { } verdict)
        {
            return verdict;
        }
        if (recorded)
        {
            return WaitFor(Cause.MethodUsed, "creepjoiner_order_recorded");
        }
        if (creep.Inspections.Waiting.Count > 0)
        {
            return AwaitingPlan("creepjoiner_inspection", "");
        }
        return WaitFor(Cause.MethodUsed, "creepjoiner_order");
    }

    /// <summary>What a run's claimed commits came to: admitted if any was, else open work if any continued, else the first held claim; null when nothing was committed.</summary>
    internal static Verdict? OutcomeVerdict(IReadOnlyList<(ClaimOutcome Outcome, ClaimConflict? Conflict)> outcomes)
    {
        if (outcomes.Any(o => o.Outcome == ClaimOutcome.Admitted))
        {
            return BuildingReasonAdmitted;
        }
        if (outcomes.Any(o => o.Outcome == ClaimOutcome.Continued))
        {
            return BuildingReasonExistingWork;
        }
        if (outcomes.FirstOrDefault(o => o.Conflict is not null).Conflict is { } conflict)
        {
            return Verdict.ClaimHeld(conflict.Claim);
        }
        return null;
    }
}
