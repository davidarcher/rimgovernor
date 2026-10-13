using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What a planner's step produced. Only a proposal competes for the step's shared claims; every other kind is the
/// planner reporting why it has nothing to admit this step.
/// </summary>
public enum PlanResultKind
{
    /// <summary>Carries a <see cref="Proposal"/> for the coordinator to arbitrate.</summary>
    Proposed,

    /// <summary>The goal has no deficit, or the work it needs is already in flight.</summary>
    Satisfied,

    /// <summary>The planner waits on the named dependency (a review, a selection slot, a retry budget, a native read).</summary>
    Waiting,

    /// <summary>Native work the planner cannot hurry is in progress; NativeWorkTicks is the window it asks for.</summary>
    NativeWait,

    /// <summary>The planner is disabled or the composition lacks what it needs.</summary>
    Unsupported,
}

/// <summary>A proposal or the reason no work can be admitted. Verdict uses the planner family's outcome vocabulary.</summary>
public sealed record PlanResult
{
    public PlanResultKind Kind { get; init; }

    public Proposal? Proposal { get; init; }

    public string Dependency { get; init; } = "";

    public Tick NativeWorkTicks { get; init; }

    public Verdict Verdict { get; init; }
}

/// <summary>
/// What a proposal needs held for it exclusively while it commits: the pawns it assigns, the entities it takes
/// whole (namespaced as the arbiter's resources are) and the quantities it consumes.
/// </summary>
public sealed record ResourceClaims
{
    public IReadOnlyList<PawnId> Pawns { get; init; } = [];

    public IReadOnlyList<string> Entities { get; init; } = [];

    public IReadOnlyList<Amount> Quantities { get; init; } = [];

    public override string ToString()
    {
        var parts = new List<string>();
        parts.AddRange(Pawns.Select(p => "pawn:" + p.Value));
        parts.AddRange(Entities);
        parts.AddRange(Quantities.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Resource}:{a.Count}")));
        return string.Join(',', parts);
    }
}

/// <summary>
/// One planner's admissible plan for this step, with everything the coordinator needs to rank it and check it
/// against its peers before anything is committed. The commit stays with the planner.
/// </summary>
public sealed record Proposal
{
    /// <summary>Stable for the same goal, epoch and method across steps, so a tie resolves the same way every time.</summary>
    public required string Id { get; init; }

    public required string Planner { get; init; }

    public ConcernId Concern { get; init; }

    public int Priority { get; init; }

    /// <summary>The goal's own priority class: lower is more urgent, the same sense as <see cref="Priority"/>.</summary>
    public int Urgency { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public IReadOnlyList<FactFamily> Facts { get; init; } = [];

    public ResourceClaims Claims { get; init; } = new();

    /// <summary>The review tick the proposal was planned from.</summary>
    public Tick ValidTick { get; init; }

    public IReadOnlyList<DomainAction> Actions { get; init; } = [];

    /// <summary>Runs the family's existing admission path.</summary>
    public required Func<CancellationToken, Task<(PlanId Plan, Verdict Reason)>> Commit { get; init; }
}

/// <summary>
/// One proposal's fate on the step row: admitted with its plan, waiting on the claim a peer holds, or expired
/// against the step it reached. A loser is never an error; it proposes again next step.
/// </summary>
public sealed record ProposalOutcome
{
    public string ProposalId { get; init; } = "";

    public string Planner { get; init; } = "";

    public ConcernId Concern { get; init; }

    public bool Admitted { get; init; }

    public PlanId Plan { get; init; }

    /// <summary>Names the claim that refused the proposal and who holds it.</summary>
    public string Waiting { get; init; } = "";

    /// <summary>Names the dependency the coordinator found changed since the proposal was evaluated.</summary>
    public string Stale { get; init; } = "";

    public Verdict Verdict { get; init; }

    /// <summary>The quantity the step could not cover for the proposal. Null unless the verdict is demand.</summary>
    public IReadOnlyList<Amount>? Demand { get; init; }
}

/// <summary>One <see cref="PlanResult"/> as the wave delivered it, and the callback that writes it to the step's result.</summary>
internal sealed record ProposalArrival(string Planner, PlanResult Result, Action<ProposalOutcome>? Settle);

/// <summary>What the coordinator checks a proposal's quantity claims against: the count the rounds observed for each bounded resource.</summary>
internal sealed record ProposalBudget(IReadOnlyDictionary<Resource, long> Stock);

/// <summary>
/// Carries the proposals that reached their step's arbiter after its cutoff to the next step's coordinator, which
/// revalidates them against its own scope.
/// </summary>
internal sealed class LateProposals
{
    private readonly Lock _mu = new();
    private List<ProposalArrival> _arrivals = [];

    public void Add(ProposalArrival arrival)
    {
        lock (_mu)
        {
            _arrivals.Add(arrival);
        }
    }

    public List<ProposalArrival> Drain()
    {
        lock (_mu)
        {
            var arrivals = _arrivals;
            _arrivals = [];
            return arrivals;
        }
    }
}

internal static class Proposals
{
    /// <summary>Orders proposals: planner priority, then goal urgency, then the stable ID.</summary>
    public static readonly IComparer<Proposal> RankComparer = Comparer<Proposal>.Create((a, b) =>
    {
        var c = a.Priority.CompareTo(b.Priority);
        if (c != 0)
        {
            return c;
        }
        c = a.Urgency.CompareTo(b.Urgency);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    });

    /// <summary>
    /// Names the first dependency of <paramref name="p"/> that no longer holds under <paramref name="scope"/>, empty
    /// when it is still valid: the world and generation, then the plan revision. Its age never expires it.
    /// </summary>
    public static string Stale(ReadValidity scope, Proposal p)
    {
        if (!scope.IsKnown)
        {
            return "";
        }
        var stale = scope.Stale(ReadScope.Of(p.Snapshot));
        if (stale != "")
        {
            return stale;
        }
        var have = p.Snapshot;
        return have.Plan != scope.Plan || have.Revision != scope.Revision
            ? $"plan {have.Plan.Value}@{have.Revision.Value}, step is {scope.Plan.Value}@{scope.Revision.Value}"
            : "";
    }
}

/// <summary>Combines existing pawn/entity claims with the proposal quantity ledger. Resources absent from the budget are unbounded here.</summary>
internal sealed class ClaimIndex(StepArbiter arbiter, ProposalBudget budget)
{
    private readonly Dictionary<Resource, long> _used = [];
    private readonly Dictionary<string, string> _holders = new(StringComparer.Ordinal);

    /// <summary>The first pawn or entity claim of <paramref name="p"/> already held, and by whom; empty when every one is free.</summary>
    public string Refusal(Proposal p)
    {
        foreach (var pawn in p.Claims.Pawns)
        {
            if (arbiter.Pawns.Contains(pawn))
            {
                return "pawn:" + pawn.Value + Holder("pawn:" + pawn.Value);
            }
        }
        foreach (var entity in p.Claims.Entities)
        {
            if (arbiter.Resources.Contains(entity))
            {
                return entity + Holder(entity);
            }
        }
        return "";
    }

    /// <summary>What <paramref name="p"/>'s quantity claims need beyond the stock left after earlier claims; null when covered.</summary>
    public (string Refused, IReadOnlyList<Amount>? Demand) Shortfall(Proposal p)
    {
        var refused = "";
        List<Amount>? demand = null;
        foreach (var amount in p.Claims.Quantities)
        {
            if (!budget.Stock.TryGetValue(amount.Resource, out var limit))
            {
                continue;
            }
            var free = limit - _used.GetValueOrDefault(amount.Resource);
            if (amount.Count <= free)
            {
                continue;
            }
            (demand ??= []).Add(new Amount(amount.Resource, amount.Count - free));
            if (refused == "")
            {
                refused = string.Create(CultureInfo.InvariantCulture, $"{amount.Resource}:{amount.Count} of {free}") + Holder(amount.Resource.ToString());
            }
        }
        return (refused, demand);
    }

    private string Holder(string key) => _holders.TryGetValue(key, out var who) && who != "" ? " held by " + who : "";

    /// <summary>Records p's claims as held by it.</summary>
    public void Take(Proposal p)
    {
        foreach (var pawn in p.Claims.Pawns)
        {
            arbiter.Pawns.Add(pawn);
            _holders["pawn:" + pawn.Value] = p.Id;
        }
        foreach (var entity in p.Claims.Entities)
        {
            arbiter.Resources.Add(entity);
            _holders[entity] = p.Id;
        }
        foreach (var amount in p.Claims.Quantities)
        {
            _used[amount.Resource] = _used.GetValueOrDefault(amount.Resource) + amount.Count;
            _holders[amount.Resource.ToString()] = p.Id;
        }
    }
}
