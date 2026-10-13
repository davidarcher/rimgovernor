using System.Globalization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The method a plan executes and the planner's reason for admitting it (Go <c>PlanMethod</c>).
/// </summary>
/// <param name="Concern">The Concern the method serves: a standard's kind or an incident's response kind.</param>
/// <param name="Reason">The planner's short reason, empty when it gave none.</param>
/// <param name="Incident">The occurrence that admitted the method, empty for a standard's.</param>
public sealed record PlanMethodBinding(ConcernId Concern, ulong Episode, MethodId Method, PlanId Plan, string Reason, IncidentId Incident);

public sealed partial class Store
{
    /// <summary>One complete, deterministically ordered active catalog snapshot.</summary>
    public IReadOnlyList<PlanState> LoadPlans(CancellationToken cancellationToken) => Run(LoadPlansIn, cancellationToken);

    public Task<IReadOnlyList<PlanState>> LoadPlansAsync(CancellationToken cancellationToken) => RunAsync(LoadPlansIn, cancellationToken);

    internal static IReadOnlyList<PlanState> LoadPlansIn(StoreTransaction tx) =>
        [.. tx.Query("SELECT id FROM plans WHERE retired=0 ORDER BY id", [], r => new PlanId(r.GetString(0))).Select(id => LoadIn(tx, id))];

    /// <summary>Inserts a fresh standard row for fixtures; the rounds mint every production standard.</summary>
    public void SeedStandard(Standard g, CancellationToken cancellationToken)
    {
        if (g.Status != StandardStatus.Open || g.Episode != 0 || g.Finding != Finding.Unclear || g.RecoveryObserved)
        {
            throw new DomainException("new standard must start without completion evidence");
        }
        Run(tx => CreateStandard(tx, g), cancellationToken);
    }

    /// <summary>
    /// Records <paramref name="method"/> on a committed plan without a methods row, for fixtures seeding the
    /// history <see cref="PlanHistoryWithMethods"/> reads.
    /// </summary>
    public void SeedPlanMethod(PlanId plan, MethodId method, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                if (tx.Execute("UPDATE plans SET method_id=$method WHERE id=$id", ("$method", method), ("$id", plan)) != 1)
                {
                    throw new NotFoundException();
                }
            },
            cancellationToken
        );

    /// <summary>
    /// The most recently committed plans, retired or not, admitted under a method id matching one of the GLOB
    /// patterns (the stored method, never the minted plan id), newest first and at most <paramref name="limit"/>
    /// of them. Unlike <see cref="LoadPlans"/> this is a window over history, not complete accounting: a shell
    /// planner reads the rings it ordered earlier in this world so it can recognise one of them from the walls
    /// still standing natively, whatever its shape. An older plan outside the window is simply not recognised.
    /// </summary>
    public IReadOnlyList<PlanState> PlanHistoryWithMethods(int limit, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
    {
        if (patterns.Count == 0 || patterns.Any(p => p.Length == 0))
        {
            throw new ArgumentException("method pattern required", nameof(patterns));
        }
        if (limit is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "plan history limit must be 1..256");
        }
        return Run(
            tx =>
            {
                var parameters = patterns.Select((p, i) => ("$p" + i.ToString(CultureInfo.InvariantCulture), (object?)p)).Append(("$limit", (object?)limit)).ToArray();
                var clauses = string.Join(" OR ", patterns.Select((_, i) => "method_id GLOB $p" + i.ToString(CultureInfo.InvariantCulture)));
                var ids = tx.Query("SELECT id FROM plans WHERE " + clauses + " ORDER BY rowid DESC LIMIT $limit", parameters, r => new PlanId(r.GetString(0)));
                return (IReadOnlyList<PlanState>)[.. ids.Select(id => LoadIn(tx, id))];
            },
            cancellationToken
        );
    }

    /// <summary>
    /// The method a plan executes, retired or not; null for a plan no standard or incident admitted (a player
    /// building submission). An incident's method names its response kind as the concern and the incident.
    /// </summary>
    public PlanMethodBinding? PlanMethod(PlanId plan, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var (row, found) = tx.QueryFirst(
                    "SELECT m.kind, m.owner_id, i.kind, json_extract(g.payload,'$.Kind'), m.episode, m.method_id, m.reason FROM plan_methods m LEFT JOIN incidents i ON m.kind='incident' AND i.id=m.owner_id LEFT JOIN standards g ON m.kind='standard' AND g.id=m.owner_id WHERE m.plan_id=$plan",
                    [("$plan", plan)],
                    r => (Kind: r.GetString(0), Owner: r.GetString(1), IncidentKind: r.Text(2), StandardKind: r.Text(3), Episode: r.Text(4), Method: r.GetString(5), Reason: r.Text(6))
                );
                if (!found)
                {
                    return null;
                }
                ulong episode = 0;
                if (row.Episode is not null && !ulong.TryParse(row.Episode, NumberStyles.None, CultureInfo.InvariantCulture, out episode))
                {
                    throw new Codec.StoreCorruptException("invalid method episode");
                }
                return row.Kind switch
                {
                    "standard" => new PlanMethodBinding(new ConcernId(row.StandardKind ?? ""), episode, new MethodId(row.Method), plan, row.Reason ?? "", default),
                    "incident" => new PlanMethodBinding(new ConcernId(row.IncidentKind ?? ""), episode, new MethodId(row.Method), plan, row.Reason ?? "", new IncidentId(row.Owner)),
                    _ => new PlanMethodBinding(default, episode, new MethodId(row.Method), plan, row.Reason ?? "", default),
                };
            },
            cancellationToken
        );

    public Task<PlanMethodBinding?> PlanMethodAsync(PlanId plan, CancellationToken cancellationToken) => Task.Run(() => PlanMethod(plan, cancellationToken), cancellationToken);

    /// <summary>Reads an exact historical binding, including retired plans. History is never fed back into active capacity or dependency accounting.</summary>
    public Method LoadMethod(StandardId standard, ulong epoch, MethodId method, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var g = LoadStandard(tx, standard);
                var plan = tx.ScalarText(
                    "SELECT plan_id FROM standard_methods WHERE standard_id=$id AND episode=$episode AND method_id=$method",
                    ("$id", standard),
                    ("$episode", epoch.ToString(CultureInfo.InvariantCulture)),
                    ("$method", method)
                ) ?? throw new NotFoundException();
                var m = new Method(standard, epoch, method, new PlanId(plan));
                m.Validate();
                return epoch > g.Standard.Episode ? throw new DomainException("invalid historical Episode") : m;
            },
            cancellationToken
        );

    public Task<Method> LoadMethodAsync(StandardId standard, ulong epoch, MethodId method, CancellationToken cancellationToken) =>
        Task.Run(() => LoadMethod(standard, epoch, method, cancellationToken), cancellationToken);

    /// <summary>
    /// The plan a standard last bound to <paramref name="method"/> in any epoch: plan ids are minted, so planners
    /// whose work outlives an epoch turnover (tidy re-sites, zone shelves) load it by this stored key.
    /// </summary>
    public PlanId LatestMethodPlan(StandardId standard, MethodId method, CancellationToken cancellationToken) =>
        Run(
            tx =>
                tx.ScalarText(
                    "SELECT plan_id FROM standard_methods WHERE standard_id=$id AND method_id=$method ORDER BY CAST(episode AS INTEGER) DESC LIMIT 1",
                    ("$id", standard),
                    ("$method", method)
                ) is { } plan
                    ? new PlanId(plan)
                    : throw new NotFoundException(),
            cancellationToken
        );

    public Task<PlanId> LatestMethodPlanAsync(StandardId standard, MethodId method, CancellationToken cancellationToken) =>
        Task.Run(() => LatestMethodPlan(standard, method, cancellationToken), cancellationToken);

    /// <summary>The methods of one epoch, retired bindings included. The bounded history is evidence only; it cannot restore retired work to execution or accounting.</summary>
    public IReadOnlyList<Method> LoadMethods(StandardId standard, ulong epoch, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var g = LoadStandard(tx, standard);
                if (epoch > g.Standard.Episode)
                {
                    throw new DomainException("invalid historical Episode");
                }
                var result = tx.Query(
                    "SELECT method_id,plan_id FROM standard_methods WHERE standard_id=$id AND episode=$episode ORDER BY method_id LIMIT 257",
                    [("$id", standard), ("$episode", epoch.ToString(CultureInfo.InvariantCulture))],
                    r => new Method(standard, epoch, new MethodId(r.GetString(0)), new PlanId(r.GetString(1)))
                );
                foreach (var m in result)
                {
                    m.Validate();
                }
                return result.Count > 256 ? throw new DomainException("standard method history exceeds bound") : (IReadOnlyList<Method>)result;
            },
            cancellationToken
        );

    public Task<IReadOnlyList<Method>> LoadMethodsAsync(StandardId standard, ulong epoch, CancellationToken cancellationToken) =>
        Task.Run(() => LoadMethods(standard, epoch, cancellationToken), cancellationToken);
}
