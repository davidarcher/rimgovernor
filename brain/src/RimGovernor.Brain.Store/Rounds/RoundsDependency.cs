using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Store.StorePackage;

namespace RimGovernor.Brain.Store;

/// <summary>One open action's cost in a <see cref="DependencyRecord"/>'s resource (Go <c>DependencyCost</c>).</summary>
public sealed record DependencyCost(ActionId Action, long Count);

/// <summary>
/// One admitted method's shortfall a planner observed at admission: the dependent Episode and method, the
/// resource its open actions cost, each action's cost, and the stock it was measured against. It is the one
/// persisted source of admitted-method construction demand (<c>ConstructionDemand</c>): the review keeps the
/// record while the Episode stays active and any of the actions stays open, for at most one game day
/// (<see cref="Store.RoundsDependencies"/>).
/// </summary>
public sealed record DependencyRecord
{
    public ConcernId Need { get; init; }

    /// <summary>The row the method binds to.</summary>
    public StandardId Standard { get; init; }

    public ulong Episode { get; init; }

    public MethodId Method { get; init; }

    public PlanId Plan { get; init; }

    public Resource Resource { get; init; }

    public EquatableArray<DependencyCost> Costs { get; init; }

    /// <summary>The usable stock the admission measured.</summary>
    public long Available { get; init; }

    public Tick Observed { get; init; }

    /// <summary>Throws <see cref="DomainException"/> when the record is not well formed (Go <c>validate</c>).</summary>
    public void Validate()
    {
        DomainException.Require(
            !Need.IsEmpty && !Standard.IsEmpty && !Method.IsEmpty && !Plan.IsEmpty && !Resource.IsEmpty && Costs.Count != 0 && Costs.Count <= 256 && Available >= 0 && Observed.Value >= 0,
            "invalid dependency record"
        );
        foreach (var c in Costs)
        {
            DomainException.Require(!c.Action.IsEmpty && c.Count > 0 && c.Count <= 1_000_000, "invalid dependency cost");
        }
    }

    /// <summary>
    /// The record for a method admitted under stock that does not cover its costs in <paramref name="resource"/>, or
    /// null when the stock covers them: a shelter shell is admitted without a stock check, and its frames then wait
    /// for the difference (Go <c>ShortfallDependency</c>).
    /// </summary>
    public static DependencyRecord? ShortfallDependency(
        ConcernId need,
        Standard goal,
        MethodId method,
        PlanId plan,
        IReadOnlyList<Preview> previews,
        StockObservation stock,
        Resource resource,
        Tick tick
    )
    {
        var available = -1L;
        foreach (var s in stock.Values)
        {
            if (s.Resource == resource && s.Available.TryGetValue(out var v))
            {
                available = Math.Max(0, v);
            }
        }
        if (available < 0)
        {
            return null;
        }
        var costs = new List<DependencyCost>();
        long total = 0;
        foreach (var p in previews)
        {
            if (!p.Costs.TryGetValue(out var amounts))
            {
                continue;
            }
            foreach (var c in amounts)
            {
                if (c.Resource == resource && c.Count > 0)
                {
                    costs.Add(new DependencyCost(p.Action.Id, c.Count));
                    total += c.Count;
                }
            }
        }
        if (total <= available || costs.Count > 256)
        {
            return null;
        }
        return new DependencyRecord
        {
            Need = need,
            Standard = goal.Id,
            Episode = goal.Episode,
            Method = method,
            Plan = plan,
            Resource = resource,
            Costs = EquatableArray.From(costs),
            Available = available,
            Observed = tick,
        };
    }
}

public sealed partial class Store
{
    internal const int MaxDependencyRecords = 64;

    /// <summary>
    /// Stores <paramref name="rec"/> in the review at <paramref name="revision"/>, replacing any record of the same
    /// goal, method and resource. A review that has moved past <paramref name="revision"/> is <see cref="ConflictException"/>.
    /// </summary>
    public void RecordDependency(ulong revision, DependencyRecord rec, CancellationToken cancellationToken)
    {
        rec.Validate();
        Run(
            tx =>
            {
                var review = LoadRounds(tx);
                if (review.Revision != revision)
                {
                    throw new ConflictException($"rounds revision {review.Revision}, dependency under {revision}");
                }
                var kept = new List<DependencyRecord> { rec };
                foreach (var d in review.Dependencies)
                {
                    if (d.Standard != rec.Standard || d.Method != rec.Method || d.Resource != rec.Resource)
                    {
                        kept.Add(d);
                    }
                }
                if (kept.Count > MaxDependencyRecords)
                {
                    kept.RemoveRange(MaxDependencyRecords, kept.Count - MaxDependencyRecords);
                }
                PutSingleton(tx, "rounds", review with { Dependencies = EquatableArray.From(SortDependencies(kept)) }, 1024 * 1024);
            },
            cancellationToken
        );
    }

    public Task RecordDependencyAsync(ulong revision, DependencyRecord rec, CancellationToken cancellationToken) =>
        Task.Run(() => RecordDependency(revision, rec, cancellationToken), cancellationToken);

    /// <summary>The records ordered by goal, method and resource (a stable sort).</summary>
    internal static List<DependencyRecord> SortDependencies(IEnumerable<DependencyRecord> records) =>
        [
            .. records
                .OrderBy(d => d.Standard.Value, StringComparer.Ordinal)
                .ThenBy(d => d.Method.Value, StringComparer.Ordinal)
                .ThenBy(d => d.Resource.Value, StringComparer.Ordinal),
        ];

    /// <summary>
    /// Keeps the records still live this review and flattens the open costs of their actions into the
    /// admitted-method demand (<c>ConstructionDemand</c>). A record drops when its goal is no longer the need's
    /// active Episode, every one of its actions has settled (the dependency is satisfied, cancelled or replaced),
    /// or its evidence is older than a game day. The shortfall is measured against the stock of the moment by the
    /// demand itself.
    /// </summary>
    internal static (List<DependencyRecord> Kept, List<AdmittedCost> Admitted) RoundsDependencies(
        StoreTransaction tx,
        IReadOnlyList<DependencyRecord> records,
        IReadOnlyList<RoundsStandard> bindings,
        IReadOnlyList<StandardState> states,
        Tick tick
    )
    {
        var active = new Dictionary<ConcernId, StandardState>();
        for (var i = 0; i < bindings.Count; i++)
        {
            active[bindings[i].Concern] = states[i];
        }
        var kept = new List<DependencyRecord>();
        var admitted = new List<AdmittedCost>();
        foreach (var rec in records)
        {
            if (
                !active.TryGetValue(rec.Need, out var g)
                || g.Standard.Id != rec.Standard
                || g.Standard.Episode != rec.Episode
                || !g.OwnerActive
                || tick < rec.Observed
                || tick.Value - rec.Observed.Value > GameTime.TicksPerDay
            )
            {
                continue;
            }
            PlanState plan;
            try
            {
                plan = LoadIn(tx, rec.Plan);
            }
            catch (NotFoundException)
            {
                continue;
            }
            if (plan.Retired)
            {
                continue;
            }
            var open = new HashSet<ActionId>();
            foreach (var p in plan.Progress)
            {
                if (ProgressOpen(plan, p))
                {
                    _ = open.Add(p.View.Action);
                }
            }
            var live = false;
            foreach (var c in rec.Costs)
            {
                if (open.Contains(c.Action))
                {
                    live = true;
                    admitted.Add(new AdmittedCost { Resource = rec.Resource, Action = c.Action, Count = c.Count });
                }
            }
            if (live)
            {
                kept.Add(rec);
            }
        }
        return (kept, admitted);
    }

    /// <summary>
    /// The last review's still-live admitted-method costs against its goal bindings, read before <c>DetectRounds</c>
    /// so an open shortfall raises its MaintainResource floor (Go <c>priorAdmitted</c>).
    /// </summary>
    internal static List<AdmittedCost> PriorAdmitted(StoreTransaction tx, Rounds previous, Tick tick)
    {
        if (previous.Dependencies.Count == 0)
        {
            return [];
        }
        var bindings = new List<RoundsStandard>();
        var states = new List<StandardState>();
        foreach (var b in previous.Standards)
        {
            try
            {
                states.Add(LoadStandard(tx, b.Standard));
                bindings.Add(b);
            }
            catch (NotFoundException)
            {
                // The binding's row is gone: nothing of it is live.
            }
        }
        return RoundsDependencies(tx, previous.Dependencies, bindings, states, tick).Admitted;
    }
}
