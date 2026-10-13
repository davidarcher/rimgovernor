using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public interface IOrderDeclarer
{
    Declared DeclareOrders(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches);
}

internal readonly record struct PurposeReport
{
    public Verdict Order { get; init; }

    public uint WorkTicks { get; init; }

    public Func<CancellationToken, Store.Store, StandardState, bool>? OpenWork { get; init; }
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

    public Dictionary<string, int>? Orphans { get; set; }

    public LedgerPending? Pending { get; set; }

    public List<LedgerPurpose> Reports { get; set; } = [];

    public GenerationSnapshot ReportsWorld { get; set; }

    public List<OrderSpec> Batches { get; set; } = [];

    public DispatchMemory Dispatch { get; set; } = new();

    public List<UnmetThroughput> Unmet { get; set; } = [];

    public int Colonists { get; set; }

    public LedgerView View { get; set; } = default!;

    public Dictionary<string, string>? BenchKinds { get; set; }

    public PlacementIndex Attempts { get; } = new();

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
                if (o.Mode == GearMode.Batch && seen.Add(o.Key()))
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

    internal IReadOnlyList<ConcernId> PurposeKeys()
    {
        lock (Ledger.Mu)
        {
            var keys = new List<ConcernId>();
            foreach (var d in Ledger.Declarers)
            {
                var (purpose, ok) = PurposeOf(d, true);
                if (ok)
                {
                    keys.Add(purpose.Key);
                }
            }

            return keys;
        }
    }

    internal IReadOnlyList<UnmetThroughput> LedgerUnmet()
    {
        lock (Ledger.Mu)
        {
            return Ledger.Unmet;
        }
    }

    internal IReadOnlyList<string> LedgerFurtherBenches(IReadOnlyList<GearBench> benches)
    {
        lock (Ledger.Mu)
        {
            return PolicyFunctions.FurtherBenchKinds(Ledger.Unmet, benches, Ledger.Colonists);
        }
    }

    public void AddOrderDeclarer(IOrderDeclarer d)
    {
        lock (Ledger.Mu)
        {
            Ledger.Declarers.Add(d);
        }
    }

    internal Fact<bool> ReviewLedger(CancellationToken call, GenerationSnapshot snapshot, ObservationIdentity expected, ColonyProjection projection)
    {
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
            l.BenchKinds = null;
            l.View = LedgerView.New(LedgerViewStatus.Inactive, projection.Identity.Tick);
        }

        if (declarers.Count == 0 || Native is not IRoundsWorkBenchSource native)
        {
            return Unknown<bool>();
        }

        IReadOnlyList<BenchRead> reads;
        try
        {
            (reads, _) = BenchSource(native, expected, false).ReadGearBenches(call, BoundaryPackage.Identity(snapshot));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            call.ThrowIfCancellationRequested();
            lock (l.Mu)
            {
                l.View = LedgerView.New(LedgerViewStatus.Unread, projection.Identity.Tick);
                foreach (var d in declarers)
                {
                    var (purpose, ok) = PurposeOf(d, true);
                    if (ok)
                    {
                        l.Reports.Add(purpose);
                    }
                }
            }

            return Unknown<bool>();
        }

        var benches = reads.Select(read => read.Bench).ToList();
        var declared = new List<Declared>();
        var named = new List<NamedDeclared>();
        var purposes = new List<LedgerPurpose>();
        foreach (var d in declarers)
        {
            var one = d.DeclareOrders(call, snapshot, projection, benches);
            declared.Add(one);
            named.Add(new NamedDeclared(DeclarerName(d), one));
            var (purpose, ok) = PurposeOf(d, false);
            if (ok)
            {
                purposes.Add(purpose);
            }
        }

        var (actual, known) = PolicyFunctions.LedgerActuals(benches);
        lock (l.Mu)
        {
            l.Reports = purposes;
            l.BenchKinds = benches.ToDictionary(b => b.Id, b => b.Def);
            l.NoteBatches(declared);
            if (!known)
            {
                l.View = LedgerView.New(LedgerViewStatus.Unread, projection.Identity.Tick);
                foreach (var d in named)
                {
                    l.View.Declarers.Add(PolicyFunctions.DeclarerView(d.Name, d.Declared));
                }

                return Unknown<bool>();
            }

            IReadOnlyList<WorkPawn> pawns = projection.WorkPawns.TryGetValue(out var rows) ? rows : [];
            var (copies, dispatched) = l.Dispatch.Dispatch(new DispatchInputs { Declared = declared, Benches = benches, Pawns = pawns, Stock = projection.Resources, Tick = projection.Identity.Tick });
            var plan = PolicyFunctions.ReconcileLedger(declared, actual, l.Orphans, copies);
            l.Orphans = plan.Orphans;
            var (placed, unplaced) = PolicyFunctions.PlaceLedgerOrders(plan.Place, benches);
            var (wanted, _) = PolicyFunctions.WantedOrders(declared);
            l.Unmet = [.. l.Dispatch.Unmet(dispatched, wanted, unplaced, benches)];
            l.Colonists = pawns.Count;
            l.View = PolicyFunctions.BuildLedgerView(new LedgerViewInput
            {
                Tick = projection.Identity.Tick,
                Declarers = named,
                Plan = plan,
                Placed = placed,
                Unplaced = unplaced,
                Dispatch = dispatched,
                Memory = l.Dispatch,
                Stock = projection.Resources,
                Unmet = l.Unmet,
                Further = PolicyFunctions.FurtherBenchKinds(l.Unmet, benches, l.Colonists),
            });
            var owed = PolicyFunctions.LedgerDiffOwed(plan);
            if (owed)
            {
                l.Pending = new LedgerPending { Snapshot = snapshot, Remove = plan.Remove, Place = placed, Unplaced = unplaced };
            }

            return Known(owed);
        }
    }

    internal IReadOnlyList<LedgerReport> HeldReports(Verdict v) => PurposeKeys().Select(key => new LedgerReport(key, v)).ToList();
}

public sealed class RoundsLedgerPlanner
{
    private readonly Rounder reviewer;

    private RoundsLedgerPlanner(Rounder reviewer) => this.reviewer = reviewer;

    public static RoundsLedgerPlanner NewRoundsLedgerPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsLedgerPlanner: reviewer == null");
        }

        return new RoundsLedgerPlanner(reviewer);
    }

    internal RoundsLedgerResult Step(CancellationToken call, CancellationToken epoch, StepArbiter? arbiter)
    {
        var r = reviewer;
        var p = r.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsLedgerResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled, Reports = r.HeldReports(BuildingRuntimePackage.BuildingReasonDisabled) };
        }

        if (!state.ObservationKnown)
        {
            throw new ControlException("step: !state.ObservationKnown");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsLedgerResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (reports, ticks) = PurposeReports(call, state.Snapshot, review);
        var result = Commit(call, epoch, arbiter, state, review);
        return result with { Reports = reports, NativeWorkTicks = ticks };
    }

    private (IReadOnlyList<LedgerReport> Reports, uint Ticks) PurposeReports(CancellationToken call, GenerationSnapshot snapshot, Rounds review)
    {
        List<LedgerPurpose> purposes;
        lock (reviewer.Ledger.Mu)
        {
            purposes = reviewer.Ledger.ReportsWorld == snapshot ? [.. reviewer.Ledger.Reports] : [];
        }

        var reports = new List<LedgerReport>();
        uint ticks = 0;
        foreach (var purpose in purposes)
        {
            var (verdict, lent) = PurposeVerdict(call, review, purpose);
            reports.Add(new LedgerReport(purpose.Key, verdict));
            ticks = Math.Max(ticks, lent);
        }

        return (reports, ticks);
    }

    private (Verdict Verdict, uint Ticks) PurposeVerdict(CancellationToken call, Rounds review, LedgerPurpose purpose)
    {
        var journal = reviewer.Player.Journal;
        StandardState owner;
        bool workable;
        try
        {
            (owner, workable) = journal.Workable(call, review, purpose.Need);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (BuildingRuntimePackage.FieldUnavailable("purpose_owner"), 0);
        }

        if (!workable)
        {
            return (BuildingRuntimePackage.BuildingReasonNoDeficit, 0);
        }

        if (purpose.Report.OpenWork is { } openWork)
        {
            bool open;
            try
            {
                open = openWork(call, journal, owner);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return (BuildingRuntimePackage.FieldUnavailable("purpose_plans"), 0);
            }

            if (open)
            {
                return (BuildingRuntimePackage.BuildingReasonExistingWork, 0);
            }
        }

        return (purpose.Report.Order, purpose.Report.WorkTicks);
    }

    private RoundsLedgerResult Commit(CancellationToken call, CancellationToken epoch, StepArbiter? arbiter, ControlState state, Rounds review)
    {
        var r = reviewer;
        var p = r.Player;
        var (owner, workable) = p.Journal.Workable(call, review, ConcernId.MaintainWorkLedger);
        if (!workable)
        {
            return new RoundsLedgerResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method in owner.Methods)
        {
            var plan0 = p.Journal.LoadPlan(call, method.Plan);
            if (StorePackage.PlanOpen(plan0))
            {
                return new RoundsLedgerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        LedgerPending? pending;
        lock (r.Ledger.Mu)
        {
            pending = r.Ledger.Pending;
        }

        if (pending is null || pending.Snapshot != state.Snapshot)
        {
            return new RoundsLedgerResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        if (pending.Actions() == 0)
        {
            return new RoundsLedgerResult { Verdict = BuildingRuntimePackage.AwaitingPlan("ledger_bench", "") };
        }

        var id = PlanIds.MintPlanId();
        var (actions, claims, key) = LedgerActions(id, pending);
        if (arbiter is not null && !arbiter.TryClaim([], [.. claims]))
        {
            return new RoundsLedgerResult { Verdict = BuildingRuntimePackage.ClaimHeld("bench") };
        }

        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }

        p.Journal.CommitOwnerMethod(call, owner, LedgerMethod(owner, key), "", plan);
        lock (r.Ledger.Mu)
        {
            r.Ledger.Pending = null;
        }

        foreach (var action in actions)
        {
            r.NotePlacement(id, action, default);
        }

        return new RoundsLedgerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
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

            var removal = RemoveProductionBill.NewRemoveProductionBill(b.Bench, b.Id);
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
        if (s.Mode == GearMode.HumanButcherForever)
        {
            return ProductionBill.NewHumanButcherBill(p.Bench, s.Recipe, s.Worker);
        }

        var bill = ProductionBill.NewProductionBill(p.Bench, s.Recipe, s.Mode, s.Target, [.. s.Ingredients]);
        return s.Worker != "" ? bill.PinWorker(s.Worker) : bill;
    }

    internal static MethodId LedgerMethod(StandardState owner, string key)
    {
        var tried = owner.History.Select(m => m.Method).ToHashSet();
        for (var attempt = 0; ; attempt++)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"ledger/{key}/{attempt}"));
            var method = new MethodId(Rounder.LedgerMethodPrefix + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
            if (!tried.Contains(method))
            {
                return method;
            }
        }
    }
}

public readonly record struct RoundsLedgerResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public IReadOnlyList<LedgerReport> Reports { get; init; }

    public uint NativeWorkTicks { get; init; }
}
