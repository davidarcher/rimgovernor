using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

public interface IOrderDeclarer
{
    Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct);
}

internal readonly record struct PurposeReport
{
    public Verdict Order { get; init; }

    /// <summary>The Need a Need-keyed purpose serves, derived from the declaring projection; null when it could not be.</summary>
    public Need? Need { get; init; }
}

internal interface IPurposeReporter : IOrderDeclarer
{
    (ConcernId Key, ConcernId Need) ReportTarget();

    PurposeReport Report();
}

internal readonly record struct LedgerPurpose(ConcernId Key, ConcernId Need, PurposeReport Report);

public readonly record struct LedgerReport(ConcernId Key, Verdict Verdict);

internal sealed class LedgerPending
{
    public GenerationSnapshot Snapshot { get; init; }

    public IReadOnlyList<ActualBill> Remove { get; init; } = [];

    public IReadOnlyList<LedgerPlacement> Place { get; init; } = [];

    public IReadOnlyList<OrderSpec> Unplaced { get; init; } = [];

    public int Actions() => Remove.Count + Place.Count;
}

internal sealed class WorkLedger
{
    public object Mu { get; } = new();

    public List<IOrderDeclarer> Declarers { get; set; } = [];

    public GenerationSnapshot World { get; set; }

    public IReadOnlyDictionary<string, int>? Orphans { get; set; }

    public LedgerPending? Pending { get; set; }

    /// <summary>The latest review's reconcile in <see cref="World"/>: a bill to place or remove, none, or unread.</summary>
    public LedgerDiff Owed { get; set; } = LedgerDiff.Unread;

    public List<LedgerPurpose> Reports { get; set; } = [];

    public GenerationSnapshot ReportsWorld { get; set; }

    public List<OrderSpec> Batches { get; set; } = [];

    public DispatchMemory Dispatch { get; set; } = new();

    public List<UnmetThroughput> Unmet { get; set; } = [];

    public int Colonists { get; set; }

    public LedgerView View { get; set; } = default!;

    public void NoteBatches(IReadOnlyList<Declared> declared)
    {
        var batches = new List<OrderSpec>();
        var seen = new HashSet<string>();
        foreach (var d in declared)
        {
            if (d.Abstained())
            {
                return;
            }

            foreach (var o in d.Orders)
            {
                if (o.Mode == BillMode.GearBatch && seen.Add(o.Key()))
                {
                    batches.Add(o);
                }
            }
        }

        Batches = batches;
    }

    public IReadOnlyList<OrderSpec> DeclaredBatches()
    {
        lock (Mu)
        {
            return [.. Batches];
        }
    }
}

public sealed partial class Rounder
{
    internal const string LedgerMethodPrefix = "ledger-";

    internal readonly WorkLedger Ledger = new();

    internal static (LedgerPurpose Purpose, bool Ok) PurposeOf(IOrderDeclarer d, bool unread)
    {
        if (d is not IPurposeReporter reporter)
        {
            return (default, false);
        }

        var (key, need) = reporter.ReportTarget();
        if (unread)
        {
            return (new LedgerPurpose(key, need, new PurposeReport { Order = FieldUnavailable("work_benches") }), true);
        }

        return (new LedgerPurpose(key, need, reporter.Report()), true);
    }

    /// <summary>The latest review's ledger diff in <paramref name="world"/> (the admin/work-ledger Need's read); Unread for another world or before a review read it.</summary>
    internal LedgerDiff LedgerOwedIn(GenerationSnapshot world)
    {
        lock (Ledger.Mu)
        {
            return Ledger.World == world ? Ledger.Owed : LedgerDiff.Unread;
        }
    }

    /// <summary>The latest review's ledger view (the station planner's work-ledger orders); LedgerViewNone before any review.</summary>
    public LedgerView WorkLedgerView()
    {
        LedgerView view;
        lock (Ledger.Mu)
        {
            view = Ledger.View;
        }

        return view is null || view.Status == "" ? WorkRules.NewLedgerView(WorkRules.LedgerViewNone) : view;
    }

    internal IReadOnlyList<string> LedgerFurtherBenches(IReadOnlyList<GearBench> benches)
    {
        lock (Ledger.Mu)
        {
            return WorkRules.FurtherBenchKinds(Ledger.Unmet, benches, Ledger.Colonists);
        }
    }

    public void AddOrderDeclarer(IOrderDeclarer d)
    {
        lock (Ledger.Mu)
        {
            Ledger.Declarers.Add(d);
        }
    }

    // The review's parts are lapped onto fine under derive/ledger/ (#3566): benches (the mirrored bench read), declare
    // (the declarers, which run together: each declares from the same reading and writes only its own memory, so
    // their hops share frames; each one's own wall rides under its name), reconcile (the diff under the ledger lock).
    internal async Task<LedgerDiff> ReviewLedgerAsync(GenerationSnapshot snapshot, Identity expected, ColonyProjection projection, CancellationToken call, StepStages? fine = null)
    {
        var inner = new StepStages();
        var l = Ledger;
        List<IOrderDeclarer> declarers;
        lock (l.Mu)
        {
            declarers = [.. l.Declarers];
            if (declarers.Count == 0)
            {
                l.Batches = [];
            }

            l.Pending = null;
            l.Owed = LedgerDiff.Unread;
            l.Reports = [];
            l.ReportsWorld = snapshot;
            if (l.World != snapshot && (l.World.Colony != snapshot.Colony || l.World.Load != snapshot.Load || l.World.Map != snapshot.Map))
            {
                l.Orphans = null;
                l.Dispatch = new DispatchMemory();
            }

            l.World = snapshot;
            l.Unmet = [];
            l.Colonists = 0;
            l.View = WorkRules.NewLedgerView(WorkRules.LedgerViewInactive);
        }

        if (declarers.Count == 0 || Native is not IRoundsWorkBenchSource native)
        {
            return LedgerDiff.Unread;
        }

        IReadOnlyList<GearBenchRead> reads;
        try
        {
            reads = await BenchSource(native, expected, false).ReadGearBenchesAsync(Boundary.Identity(snapshot), call).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            call.ThrowIfCancellationRequested();
            lock (l.Mu)
            {
                l.View = WorkRules.NewLedgerView(WorkRules.LedgerViewUnread);
                foreach (var d in declarers)
                {
                    var (purpose, ok) = PurposeOf(d, true);
                    if (ok)
                    {
                        l.Reports.Add(purpose);
                    }
                }
            }

            return LedgerDiff.Unread;
        }

        inner.Lap("derive/ledger/benches");
        var benches = reads.Select(read => read.Bench).ToList();
        var timed = new Task<(Declared Declared, double Ms)>[declarers.Count];
        for (var i = 0; i < declarers.Count; i++)
        {
            timed[i] = DeclareTimedAsync(declarers[i], snapshot, projection, benches, call);
        }

        // Awaited in composition order, so the declared list (and the first failure) keeps the order the sequential loop had.
        var declared = new List<Declared>(declarers.Count);
        var purposes = new List<LedgerPurpose>();
        for (var i = 0; i < declarers.Count; i++)
        {
            var (orders, ms) = await timed[i].ConfigureAwait(false);
            declared.Add(orders);
            inner.Add("derive/ledger/" + LapName(declarers[i]), ms);
            var (purpose, ok) = PurposeOf(declarers[i], false);
            if (ok)
            {
                purposes.Add(purpose);
            }
        }

        inner.Lap("derive/ledger/declare");
        var (actual, known) = WorkRules.LedgerActuals(benches);
        LedgerDiff result;
        lock (l.Mu)
        {
            l.Reports = purposes;
            l.NoteBatches(declared);
            if (!known)
            {
                l.View = WorkRules.NewLedgerView(WorkRules.LedgerViewUnread);
                result = LedgerDiff.Unread;
            }
            else
            {
                EquatableArray<WorkPawn> pawns = projection.WorkPawns is WorkPawnsRead.Read { Pawns: var rows } ? rows : [];
                var (copies, dispatched) = l.Dispatch.Dispatch(new DispatchInputs { Declared = declared, Benches = benches, Pawns = pawns, Stock = projection.Resources, Tick = projection.Identity.Tick.Value });
                var plan = WorkRules.ReconcileLedger(declared, actual, l.Orphans ?? new Dictionary<string, int>(), copies);
                l.Orphans = plan.Orphans;
                var (placed, unplaced) = WorkRules.PlaceLedgerOrders(plan.Place, benches);
                var (wanted, _) = WorkRules.WantedOrders(declared);
                l.Unmet = [.. l.Dispatch.Unmet(dispatched, wanted, unplaced, benches)];
                l.Colonists = pawns.Count;
                l.View = WorkRules.BuildLedgerView(wanted);
                var owed = WorkRules.LedgerDiffOwed(plan, placed);
                if (owed)
                {
                    l.Pending = new LedgerPending { Snapshot = snapshot, Remove = plan.Remove, Place = placed, Unplaced = unplaced };
                }

                result = owed ? LedgerDiff.Owed : LedgerDiff.Settled;
            }
        }

        inner.Lap("derive/ledger/reconcile");
        fine?.Add(inner);
        return result;
    }

    private static async Task<(Declared Declared, double Ms)> DeclareTimedAsync(IOrderDeclarer d, GenerationSnapshot snapshot, ColonyProjection projection, List<GearBench> benches, CancellationToken call)
    {
        var at = Stopwatch.GetTimestamp();
        var declared = await d.DeclareOrdersAsync(snapshot, projection, benches, call).ConfigureAwait(false);
        return (declared, Stopwatch.GetElapsedTime(at).TotalMilliseconds);
    }

    /// <summary>The fine lap name of a declarer: its type name without the Rounds prefix and the Declarer or Planner suffix, in lower case.</summary>
    internal static string LapName(IOrderDeclarer d)
    {
        var name = d.GetType().Name;
        if (name.StartsWith("Rounds", StringComparison.Ordinal))
        {
            name = name["Rounds".Length..];
        }
        if (name.EndsWith("Declarer", StringComparison.Ordinal))
        {
            name = name[..^"Declarer".Length];
        }
        else if (name.EndsWith("Planner", StringComparison.Ordinal))
        {
            name = name[..^"Planner".Length];
        }
        return name.ToLowerInvariant();
    }
}

public sealed partial class AdminRounds
{
    /// <summary>
    /// The verdicts the work ledger files beside the family's Needs (#3304): one per declaring purpose under its
    /// key, read from the review's ledger reports. Empty unless the ledger is composed and the reports are this
    /// world's.
    /// </summary>
    internal IReadOnlyList<LedgerReport> LedgerPurposeReports(GenerationSnapshot world, CancellationToken call)
    {
        if (!_needs.WorkLedger)
        {
            return [];
        }

        var review = _reviewer.Player.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != world)
        {
            return [];
        }

        List<LedgerPurpose> purposes;
        lock (_reviewer.Ledger.Mu)
        {
            purposes = _reviewer.Ledger.ReportsWorld == world ? [.. _reviewer.Ledger.Reports] : [];
        }

        return [.. purposes.Select(purpose => new LedgerReport(purpose.Key, PurposeVerdict(purpose)))];
    }

    private static Verdict PurposeVerdict(LedgerPurpose purpose)
    {
        // A purpose keyed by a NeedId reads its Need's standing: met is nothing to do, and unmet (or no Need
        // reported) is the order's own verdict. Its bills share the ledger, so no owner's open work gates it.
        if (new NeedId(purpose.Need.Value).IsValid)
        {
            return purpose.Report.Need?.Satisfaction == Satisfaction.Met ? Verdict.BuildingReasonNoDeficit : purpose.Report.Order;
        }

        // A purpose keyed by anything else has no owner to work it.
        return Verdict.BuildingReasonNoDeficit;
    }

    /// <summary>
    /// The admin/work-ledger work (#3304): commits the review's pending reconcile (removals first, then the chosen
    /// placements, at most <c>MaxBillPlanActions</c>) as one Method named by the diff it carries, under the Need
    /// owner and admitted by claims. Each bill claims its bench and recipe and each removal its bill, so a diff over
    /// other benches is admitted beside an open one, the same diff continues, and an overlapping one is refused with
    /// a claim conflict until the open one ends.
    /// </summary>
    private Task<Verdict> WorkLedgerStepAsync(NeedWork work, CancellationToken call)
    {
        var r = _reviewer;
        var p = r.Player;
        var state = work.State;
        LedgerPending? pending;
        lock (r.Ledger.Mu)
        {
            pending = r.Ledger.Pending;
        }

        if (pending is null || pending.Snapshot != state.Snapshot)
        {
            return Task.FromResult(BuildingReasonNoDeficit);
        }

        if (pending.Actions() == 0)
        {
            return Task.FromResult(Verdict.AwaitingPlan("ledger_bench", ""));
        }

        var id = PlanIds.MintPlanId();
        var materials = LedgerMaterialChoicesRules.SelectLedgerMaterials(pending.Place, p.Journal.ProductionMaterials(state.Snapshot, call));
        var selectionReason = $"material options: safety={materials.Comparison.Priority.Safety}, required={materials.Comparison.Priority.Required}, maintenance={materials.Comparison.Priority.Maintenance}, material_value={materials.Comparison.Priority.MaterialCost}, recipe_work={materials.Comparison.Priority.Work}, generated={materials.Generated}, searched={materials.Comparison.Expansions}, generation_truncated={materials.GenerationTruncated}, search_truncated={materials.Comparison.SearchTruncated}, findings={materials.Comparison.Findings.Count}, kinds={string.Join(",", materials.Comparison.Findings.Select(f => f.Split(':')[0]).Distinct())}";
        var chosen = new LedgerPending { Snapshot = pending.Snapshot, Remove = pending.Remove, Place = materials.Selected };
        var (actions, claims, key) = LedgerActions(id, chosen);
        if (actions.Count == 0)
        {
            return Task.FromResult(Verdict.AwaitingPlan("production_material", selectionReason));
        }

        if (work.Arbiter is not null && !work.Arbiter.TryClaim([], [.. claims]))
        {
            return Task.FromResult(Verdict.ClaimHeld("bench"));
        }

        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, work.Epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("work ledger: the session moved");
        }

        var commit = p.Journal.CommitClaimedMethod(work.Need.Id, work.Owner.Snapshot, LedgerMethod(key), selectionReason, plan, call);
        if (commit.Outcome != ClaimOutcome.Admitted)
        {
            return Task.FromResult(OutcomeVerdict([commit]) ?? BuildingReasonExistingWork);
        }

        lock (r.Ledger.Mu)
        {
            var removed = Math.Min(pending.Remove.Count, actions.Count);
            var committed = materials.Selected.Take(actions.Count - removed).ToArray();
            r.Ledger.Pending = new LedgerPending { Snapshot = pending.Snapshot,
                Remove = pending.Remove.Skip(removed).ToArray(),
                Place = pending.Place.Where(p => !committed.Any(c => c.Bench == p.Bench
                    && (c.Spec.Key() == p.Spec.Key() || WorkRules.IsMaterialSpecialization(p.Spec, c.Spec)))).ToArray(), Unplaced = pending.Unplaced };
        }

        return Task.FromResult(BuildingReasonAdmitted);
    }

    internal static (List<DomainAction> Actions, List<string> Claims, string Key) LedgerActions(PlanId plan, LedgerPending p)
    {
        var actions = new List<DomainAction>();
        var claims = new List<string>();
        var seen = new HashSet<string>();
        void Claim(string k)
        {
            if (seen.Add(k))
            {
                claims.Add(k);
            }
        }

        var keys = new List<string>();
        foreach (var b in p.Remove)
        {
            if (actions.Count >= StorePackage.MaxBillPlanActions)
            {
                break;
            }

            var removal = new RemoveProductionBill(b.Bench, b.Id);
            actions.Add(DomainAction.NewRemoveProductionBillAction(new ActionId($"{plan}-{actions.Count}"), removal));
            Claim("bench:" + b.Bench);
            Claim("remove-bill:" + b.Id);
            keys.Add("remove/" + b.Bench + "/" + b.Id);
        }

        foreach (var placement in p.Place)
        {
            if (actions.Count >= StorePackage.MaxBillPlanActions)
            {
                break;
            }

            var bill = LedgerBill(placement);
            actions.Add(DomainAction.NewProductionBillAction(new ActionId($"{plan}-{actions.Count}"), bill));
            Claim("bench:" + placement.Bench);
            keys.Add("place/" + placement.Bench + "/" + placement.Spec.Key());
        }

        keys.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var k in keys)
        {
            sb.Append(k).Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return (actions, claims, Convert.ToHexStringLower(digest.AsSpan(0, 16)));
    }

    internal static ProductionBill LedgerBill(LedgerPlacement p)
    {
        var s = p.Spec;
        if (s.Mode == BillMode.HumanButcherForever)
        {
            return ProductionBill.NewHumanButcherBill(p.Bench, s.Recipe, s.Worker);
        }

        var bill = new ProductionBill(p.Bench, s.Recipe, s.Mode, s.Target, [.. s.Ingredients]);
        return s.Worker != "" ? bill.PinWorker(s.Worker) : bill;
    }

    /// <summary>The diff's MethodId: the same diff continues its open Method and is admitted again as the next attempt once that one is released.</summary>
    internal static MethodId LedgerMethod(string key) =>
        new(Rounder.LedgerMethodPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"ledger/{key}")).AsSpan(0, 16)));
}
