using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native read of an excavation site.</summary>
public interface IRoundsExcavationSource
{
    Task<(BridgeExcavationSite Site, BridgeResult Result)> ReadExcavationSiteAsync(C.Identity identity, IReadOnlyList<Cell> cells, Cell access, CancellationToken ct);
}

/// <summary>The state one excavation step works from.</summary>
internal sealed class ExcavationStep
{
    public ControlState State;
    public RoundsReview Review = new();
    public required StandardState Owner;
    public ColonyProjection Facts = new();
    public ColonyReading Read = new();
    public ExcavationTarget Target = new();
}

public sealed partial class RoundsBuildingPlanner
{
    internal const string ExcavationStagePrefix = "excavation-stage-";
    internal const int ExcavationStageLimit = 8;
    internal const long ExcavationStallTicks = GameTime.TicksPerHour;
    internal const int ExcavationCandidates = 4;

    internal ExcavationTarget? ExcavationProject(StandardState goal, CancellationToken call)
    {
        var methods = Reviewer.Player.Journal.LoadOwnerMethods(goal, call);
        var (latest, _) = ExcavationProgress(methods);
        return latest;
    }

    internal async Task<BridgeExcavationSite> ReadExcavationSiteAsync(GenerationSnapshot snapshot, Tick tick, string purpose, ExcavationTarget target, IReadOnlyList<Cell> cells, Cell access, Action check, CancellationToken call)
    {
        check();
        var (site, _) = await Excavation!.ReadExcavationSiteAsync(Boundary.Identity(snapshot), cells, access, call).ConfigureAwait(false);
        check();
        try
        {
            var current = Boundary.Context(site.Context, snapshot);
            if (current.Native != snapshot.Native || new Tick(site.Context?.Tick ?? 0) < tick)
            {
                throw new ControlException("readExcavationSite: err != nil || current.Native != snapshot.Native || domain.Tick(site.Context.GetTick()) < tick");
            }
        }
        catch (ControlException)
        {
            throw new ControlException("readExcavationSite: err != nil || current.Native != snapshot.Native || domain.Tick(site.Context.GetTick()) < tick");
        }
        SnapshotApi.NoteSite(purpose, target, cells, site);
        return site;
    }

    internal async Task<bool> VerifyExcavationAsync(GenerationSnapshot snapshot, Tick tick, ExcavationTarget target, Action check, CancellationToken call)
    {
        var site = await ReadExcavationSiteAsync(snapshot, tick, "verify", target, target.Cells(), target.Access, check, call).ConfigureAwait(false);
        return ExcavationVerified(target, site);
    }

    internal async Task<RoundsBuildingResult> StepExcavationAsync(ExcavationStep s, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var journal = p.Journal;
        var methods = journal.LoadOwnerMethods(s.Owner, call);
        var (_, stage) = ExcavationProgress(methods);
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != s.State)
            {
                throw new ControlException("stepExcavation: p.session.State() != s.state");
            }
        }
        var snapshot = s.State.Snapshot with { Revision = new PlanRevision(1) };
        var site = await ReadExcavationSiteAsync(snapshot, s.Facts.Identity.Tick, "stage", s.Target, s.Target.Cells(), s.Target.Access, Check, call).ConfigureAwait(false);
        var definitions = new Dictionary<Cell, string>();
        foreach (var cell in site.Cells)
        {
            definitions[cell.Cell] = cell.Definition;
        }
        var (review, reason, door) = ExcavationNext(s.Target, site);
        if (door)
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "excavation_door") };
        }
        if (!reason.IsZero)
        {
            return new RoundsBuildingResult { Verdict = reason };
        }
        var next = review.Stage;
        if (site.Support != ExcavationSupport.Supported)
        {
            var stageSite = await ReadExcavationSiteAsync(snapshot, s.Facts.Identity.Tick, "stage-support", s.Target, next, s.Target.Access, Check, call).ConfigureAwait(false);
            switch (stageSite.Support)
            {
                case ExcavationSupport.Supported:
                    break;
                case ExcavationSupport.Unsupported:
                    if (stageSite.CollapsePending)
                    {
                        return new RoundsBuildingResult { Verdict = CollapsePending("excavation_site") };
                    }
                    return new RoundsBuildingResult { Verdict = ExcavationBlocked("roof_unsupported") };
                default:
                    return new RoundsBuildingResult { Verdict = FieldUnavailable("excavation_support") };
            }
        }
        if (!site.WorkerAvailable)
        {
            return new RoundsBuildingResult { Verdict = NoWorker("excavation") };
        }
        snapshot = snapshot with { Plan = PlanIds.MintPlanId() };
        var actions = new List<DomainAction>(next.Count);
        for (var i = 0; i < next.Count; i++)
        {
            var cell = next[i];
            definitions.TryGetValue(cell, out var def);
            var excavation = new Excavation(cell, def ?? "");
            actions.Add(DomainAction.NewExcavationAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-{i}")), excavation));
        }
        var plan = new PlanSpec(snapshot.Plan, new PlanRevision(1), actions);
        return await AdmitExcavationAsync(s, snapshot, ExcavationStageMethod(stage, s.Target), plan, null, new StockObservation { Snapshot = snapshot, Tick = s.Facts.Identity.Tick }, Check, call, epoch).ConfigureAwait(false);
    }

    internal async Task<RoundsBuildingResult> AdmitExcavationAsync(ExcavationStep s, GenerationSnapshot snapshot, MethodId method, PlanSpec plan, IReadOnlyList<Preview>? previews, StockObservation stock, Action check, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var (last, _) = await Reviewer.Native.IdentityAsync(call).ConfigureAwait(false);
        Identity actual;
        try
        {
            actual = ObservationApi.DecodeIdentity(last);
        }
        catch (ControlException)
        {
            throw new ControlException("admitExcavation: err != nil || !roundsBuildingBoundary(actual, s.state.Snapshot, s.facts.Identity.Tick)");
        }
        if (!RoundsBuildingBoundary(actual, s.State.Snapshot, s.Facts.Identity.Tick))
        {
            throw new ControlException("admitExcavation: err != nil || !roundsBuildingBoundary(actual, s.state.Snapshot, s.facts.Identity.Tick)");
        }
        var now = Reviewer.Clock.Now();
        if (now < s.Read.StartedAt || now - s.Read.StartedAt > Reviewer.MaxAge)
        {
            throw new ObservationStaleException();
        }
        check();
        var latest = p.Journal.LoadRounds(call);
        if (latest.Revision != s.Review.Revision || !latest.Enabled)
        {
            throw new ControlException("admitExcavation: latest.Revision != s.review.Revision || !latest.Enabled");
        }
        var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
        {
            Owner = s.Owner,
            Method = method,
            Plan = plan,
            Current = snapshot,
            Tick = s.Facts.Identity.Tick,
            Bounds = Known(s.Facts.Bounds),
            Stock = stock,
            Previews = previews ?? [],
            Purpose = Purpose.Rounds,
        }, call).ConfigureAwait(false);
        var reason = AdmissionLog.AdmissionRefused(decision);
        if (decision.Admitted)
        {
            reason = BuildingReasonAdmitted;
        }
        return new RoundsBuildingResult { Verdict = reason, Decision = decision };
    }

    internal static MethodId ExcavationStageMethod(int stage, ExcavationTarget target) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{ExcavationStagePrefix}{stage}@{target.Key()}"));

    /// <summary>The stage number and target a method id names; not an excavation method otherwise.</summary>
    public static (int Stage, ExcavationTarget Target, bool Ok) ExcavationMethod(MethodId method)
    {
        var at = method.Value.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            return (0, new ExcavationTarget(), false);
        }
        var head = method.Value[..at];
        var key = method.Value[(at + 1)..];
        if (!head.StartsWith(ExcavationStagePrefix, StringComparison.Ordinal))
        {
            return (0, new ExcavationTarget(), false);
        }
        var n = head[ExcavationStagePrefix.Length..];
        if (!int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < 0 || v.ToString(CultureInfo.InvariantCulture) != n)
        {
            return (0, new ExcavationTarget(), false);
        }
        try
        {
            return (v, PolicyApi.ParseExcavationKey(key), true);
        }
        catch (DomainException)
        {
            return (0, new ExcavationTarget(), false);
        }
    }

    public static bool IsExcavationMethod(MethodId method) => ExcavationMethod(method).Ok;

    /// <summary>The target of the latest stage among the methods and the number of the next stage.</summary>
    internal static (ExcavationTarget? Latest, int Next) ExcavationProgress(IEnumerable<Method> methods)
    {
        ExcavationTarget? latest = null;
        var next = 0;
        foreach (var m in methods)
        {
            var (stage, target, ok) = ExcavationMethod(m.MethodId);
            if (ok && stage >= next)
            {
                next = stage + 1;
                latest = target;
            }
        }
        return (latest, next);
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static bool ExcavationVerified(ExcavationTarget target, BridgeExcavationSite site)
    {
        if ((site.Support == ExcavationSupport.Unsupported && !site.CollapsePending) || !site.AccessReachable || !site.WorkerAvailable)
        {
            return false;
        }
        var review = PolicyApi.ReviewExcavation(target, ExcavationStates(site), RoundsBuildingPlanner.ExcavationStageLimit);
        return review.Kept.Count == 0 && review.Corridor;
    }

    internal static List<ExcavationCellState> ExcavationStates(BridgeExcavationSite site)
    {
        var states = new List<ExcavationCellState>(site.Cells.Count);
        foreach (var cell in site.Cells)
        {
            var cleared = !cell.Fogged && cell.Definition == "" && cell.Walkable;
            states.Add(new ExcavationCellState
            {
                Cell = cell.Cell,
                Fogged = cell.Fogged,
                Cleared = cleared,
                Blocked = !cell.Fogged && !cleared && !cell.Eligible,
                Eligible = cell.Eligible,
                Rock = cell.Definition,
            });
        }
        return states;
    }

    internal static Verdict ExcavationBlocked(string why) => SiteBlocked("excavation_site", why);

    /// <summary>The next excavation stage; a verdict when the site blocks it, and the door flag when the corridor is complete.</summary>
    internal static (ExcavationReview Review, Verdict Reason, bool Door) ExcavationNext(ExcavationTarget target, BridgeExcavationSite site)
    {
        var review = PolicyApi.ReviewExcavation(target, ExcavationStates(site), RoundsBuildingPlanner.ExcavationStageLimit);
        if (site.CollapsePending)
        {
            return (review, CollapsePending("excavation_site"), false);
        }
        if (!review.Corridor || !site.AccessReachable)
        {
            return (review, ExcavationBlocked("way_in_closed"), false);
        }
        if (review.Complete)
        {
            return (review, default, true);
        }
        if (site.Support == ExcavationSupport.Unsupported)
        {
            return (review, ExcavationBlocked("roof_unsupported"), false);
        }
        if (review.Stage.Count == 0 && review.Unknown)
        {
            return (review, FieldUnavailable("excavation_cells"), false);
        }
        if (review.Stage.Count == 0)
        {
            return (review, NoSpace("excavation_next_stage"), false);
        }
        return (review, default, false);
    }

    /// <summary>Cancels the pending excavation actions held for an hour or more by an unsupported roof, a changed geometry or a not-ready hold.</summary>
    internal static void CancelStalledExcavation(StoreJournal journal, StandardState goal, Tick now, CancellationToken ct)
    {
        foreach (var method in goal.Methods)
        {
            if (!RoundsBuildingPlanner.IsExcavationMethod(method.MethodId))
            {
                continue;
            }
            var plan = journal.LoadPlan(method.Plan, ct);
            if (!StorePackage.PlanOpen(plan))
            {
                continue;
            }
            foreach (var progress in plan.Progress)
            {
                var v = progress.View;
                if (progress.Action.Kind != ActionKind.Excavation || (v.Stage != Stage.Pending && v.Stage != Stage.Prepared))
                {
                    continue;
                }
                if (!v.TryGetFreshHold(out var hold) || now.Value - hold.Since.Value < RoundsBuildingPlanner.ExcavationStallTicks)
                {
                    continue;
                }
                var stalled = false;
                foreach (var reason in hold.Reasons())
                {
                    stalled = stalled || reason == HeldReason.ExcavationUnsupported || reason == HeldReason.ExcavationGeometryChanged || reason == HeldReason.NotReady;
                }
                if (!stalled)
                {
                    continue;
                }
                journal.Cancel(method.Plan, v.Action, ct);
            }
        }
    }
}
