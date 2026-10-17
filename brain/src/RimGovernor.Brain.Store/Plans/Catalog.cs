using System.Globalization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The method a plan executes and the planner's reason for admitting it (Go <c>PlanMethod</c>).
/// </summary>
/// <param name="Concern">The Need the method serves, as its filing key.</param>
/// <param name="Reason">The planner's short reason, empty when it gave none.</param>
public sealed record PlanMethodBinding(ConcernId Concern, MethodId Method, PlanId Plan, string Reason);

public sealed partial class Store
{
    /// <summary>One complete, deterministically ordered active catalog snapshot.</summary>
    public IReadOnlyList<PlanState> LoadPlans(CancellationToken cancellationToken) => Run(LoadPlansIn, cancellationToken);

    public Task<IReadOnlyList<PlanState>> LoadPlansAsync(CancellationToken cancellationToken) => RunAsync(LoadPlansIn, cancellationToken);

    internal static IReadOnlyList<PlanState> LoadPlansIn(StoreTransaction tx) =>
        [.. tx.Query("SELECT id FROM plans WHERE retired=0 ORDER BY id", [], r => new PlanId(r.GetString(0))).Select(id => LoadIn(tx, id))];

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
    /// The method a plan executes, retired or not; null for a plan no Need admitted (a player building submission).
    /// A Need's method files under its NeedId (#3250), the key its planner reasons use.
    /// </summary>
    public PlanMethodBinding? PlanMethod(PlanId plan, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var (row, found) = tx.QueryFirst(
                    "SELECT need_id, method_id, reason FROM need_methods WHERE plan_id=$plan",
                    [("$plan", plan)],
                    r => (Need: r.GetString(0), Method: r.GetString(1), Reason: r.Text(2))
                );
                return found ? new PlanMethodBinding(new ConcernId(row.Need), new MethodId(row.Method), plan, row.Reason ?? "") : null;
            },
            cancellationToken
        );

    public Task<PlanMethodBinding?> PlanMethodAsync(PlanId plan, CancellationToken cancellationToken) => Task.Run(() => PlanMethod(plan, cancellationToken), cancellationToken);
}
