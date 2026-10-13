using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// Ready work describes stages that can consume worker time. Unmet needs and unresolved effects alone do not
// occupy workers.
//
// ProjectReadyWork is a pure projection stored as Rounds.ReadyWork diagnostics; admission does not consume it.
// Explicit adapters identify stages, work, claims and parallelism. An action kind without one reports
// Awaiting with reason eligibility_unknown and no work: its eligibility is not observed.

/// <summary>What a candidate can do this review.</summary>
public enum ReadyState
{
    None = 0,

    /// <summary>The goal has no plan or proposal to offer.</summary>
    [WireName("no_method")]
    NoMethod,

    /// <summary>A known prerequisite or eligibility fact fails.</summary>
    [WireName("blocked")]
    Blocked,

    /// <summary>A fact the stage needs is unknown; never ready.</summary>
    [WireName("awaiting_observation")]
    Awaiting,

    /// <summary>Admitted work whose effect is unresolved but which needs no worker until its next stage's prerequisites hold (a bill waiting on ingredients, a crop still growing).</summary>
    [WireName("open_effect")]
    OpenEffect,

    /// <summary>The stage can consume worker time now.</summary>
    [WireName("runnable")]
    Runnable,
}

/// <summary>A resource or cell a candidate would use. Candidates with the same stage, work and claims are one piece of work. Kind is "cell", "thing" or "bench".</summary>
public readonly record struct ReadyClaim(string Kind, string Key);

/// <summary>Order-independent: world, stage, work and claims (or the plan action when there are none). A world change changes every ID.</summary>
public readonly record struct ReadyWorkId(string Value);

public sealed record ReadyWork
{
    public ReadyWorkId ID { get; init; }

    /// <summary>Concerns served; more than one when shared work was deduplicated.</summary>
    public EquatableArray<ConcernId> Concerns { get; init; }

    public MethodId Method { get; init; }

    public PlanId Plan { get; init; }

    public ActionId Action { get; init; }

    /// <summary>The concrete method stage ("bill:Make_Kibble", "grow:Hay"), never the goal's broad profile.</summary>
    public string Stage { get; init; } = "";

    /// <summary>The native work type the stage consumes; empty when none is known.</summary>
    public EquatableArray<WorkType> Work { get; init; }

    public ReadyState State { get; init; }

    public string Reason { get; init; } = "";

    /// <summary>Names unmet prerequisites (plan actions or earlier stages).</summary>
    public EquatableArray<string> Requires { get; init; }

    public EquatableArray<ReadyClaim> Claims { get; init; }

    /// <summary>Groups interchangeable methods: at most one per group is counted as demand. Sequential steps use Requires, never this.</summary>
    public string Alternative { get; init; } = "";

    /// <summary>How many workers the stage usefully takes now; zero unless runnable.</summary>
    public int Parallelism { get; init; }
}

/// <summary>Caps one projection pass.</summary>
public readonly record struct ReadyBounds(int Candidates, int PerConcern, int Discovery);

public sealed record ReadyDeferral(ConcernId Concern, string Reason, int Count);

public sealed record ReadyWorkReport
{
    public ColonyId Colony { get; init; }

    public MapId Map { get; init; }

    public LoadId Load { get; init; }

    public Tick Tick { get; init; }

    public EquatableArray<ReadyWork> Candidates { get; init; }

    public EquatableArray<ReadyDeferral> Deferred { get; init; }

    /// <summary>The first input the discovery bound skipped.</summary>
    public string Continuation { get; init; } = "";

    /// <summary>Whether the report describes the world s names; a report from another colony, map or load describes nothing.</summary>
    public bool Current(GenerationSnapshot s) => Colony == s.Colony && Map == s.Map && Load == s.Load;

    /// <summary>
    /// The runnable worker demand per work type, counting one alternative per group (the largest) so
    /// alternatives are never both reserved.
    /// </summary>
    public Dictionary<WorkType, int> Demand()
    {
        var demand = new Dictionary<WorkType, int>();
        var groups = new Dictionary<string, ReadyWork>(StringComparer.Ordinal);
        foreach (var c in Candidates)
        {
            if (c.State != ReadyState.Runnable || c.Parallelism == 0 || c.Work.Count == 0)
            {
                continue;
            }
            if (c.Alternative.Length == 0)
            {
                demand[c.Work[0]] = demand.GetValueOrDefault(c.Work[0]) + c.Parallelism;
                continue;
            }
            if (!groups.TryGetValue(c.Alternative, out var prev) || c.Parallelism > prev.Parallelism)
            {
                groups[c.Alternative] = c;
            }
        }
        foreach (var c in groups.Values)
        {
            demand[c.Work[0]] = demand.GetValueOrDefault(c.Work[0]) + c.Parallelism;
        }
        return demand;
    }
}

/// <summary>One existing plan with its progress.</summary>
public sealed record ReadyPlan
{
    public ConcernId Concern { get; init; }

    public MethodId Method { get; init; }

    public required PlanSpec Spec { get; init; }

    public IReadOnlyList<Progress> Progress { get; init; } = [];

    /// <summary>The observed next-stage prerequisite of an admitted action whose work comes in stages: a crop ready to sow or harvest. Missing means unknown.</summary>
    public IReadOnlyDictionary<ActionId, Fact<bool>> Inputs { get; init; } = new Dictionary<ActionId, Fact<bool>>();
}

/// <summary>A not-yet-admitted stage from side-effect-free discovery (the adapters below).</summary>
public sealed record ReadyProposal
{
    public ConcernId Concern { get; init; }

    public MethodId Method { get; init; }

    public string Stage { get; init; } = "";

    public WorkType Work { get; init; }

    public IReadOnlyList<ReadyClaim> Claims { get; init; } = [];

    public IReadOnlyList<string> Requires { get; init; } = [];

    public string Alternative { get; init; } = "";

    /// <summary>The stage's current native eligibility; unknown awaits.</summary>
    public Fact<bool> Eligible { get; init; }

    public string Reason { get; init; } = "";

    public int Parallelism { get; init; }
}

public sealed record ReadyRequest
{
    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public IReadOnlyList<ReadyPlan> Plans { get; init; } = [];

    public IReadOnlyList<ReadyProposal> Proposals { get; init; } = [];

    /// <summary>Goals that have neither; each reads no_method.</summary>
    public IReadOnlyList<ConcernId> Unserved { get; init; } = [];

    public ReadyBounds Bounds { get; init; }

    /// <summary>The building census: an applied building intent stays open, and its dependents wait, until a built row carries its key.</summary>
    public Fact<CurrentConstruction> Construction { get; init; }

    /// <summary>Place a bill's recipe at a work type; a bill with a recipe it does not know reports eligibility_unknown.</summary>
    public RecipeFacts Recipes { get; init; } = new();
}

public static partial class PolicyFunctions
{
    // Deferral reasons, deterministic when a bound cuts the pass.
    public const string ReadyDeferredDiscovery = "discovery_bound";

    public const string ReadyDeferredPerConcern = "per_standard_bound";

    public const string ReadyDeferredCandidate = "candidate_bound";

    public static ReadyClaim CellClaim(Cell c) => new("cell", c.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + c.Z.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static ReadyBounds DefaultReadyBounds() => new(64, 8, 1024);

    /// <summary>
    /// Projects plans and proposals into bounded candidates. Every open action of a plan is examined, not
    /// only the first, so an independent runnable action beside a blocked one is visible.
    /// </summary>
    public static ReadyWorkReport ProjectReadyWork(ReadyRequest r)
    {
        var b = r.Bounds == default ? DefaultReadyBounds() : r.Bounds;
        var continuation = "";
        var deferred = new Dictionary<(string Concern, string Reason), int>();
        var perConcern = new Dictionary<ConcernId, int>();
        var byId = new Dictionary<ReadyWorkId, int>();
        var output = new List<ReadyWork>();
        var examined = 0;

        void Add(ReadyWork c)
        {
            c = c with { Concerns = EquatableArray.From(SortedConcerns(c.Concerns)), Claims = EquatableArray.From(SortedClaims(c.Claims)) };
            c = c with { ID = ReadyId(r.Snapshot, c) };
            if (byId.TryGetValue(c.ID, out var i))
            {
                output[i] = output[i] with { Concerns = EquatableArray.From(SortedConcerns(output[i].Concerns.Concat(c.Concerns))) };
                return;
            }
            byId[c.ID] = output.Count;
            output.Add(c);
        }

        bool Budget(string key)
        {
            if (examined >= b.Discovery)
            {
                if (continuation.Length == 0)
                {
                    continuation = key;
                }
                return false;
            }
            examined++;
            return true;
        }

        void Defer(string concern, string reason) => deferred[(concern, reason)] = deferred.GetValueOrDefault((concern, reason)) + 1;

        var plans = r.Plans.OrderBy(p => p.Spec.Id).ToList();
        foreach (var p in plans)
        {
            var byAction = new Dictionary<ActionId, Progress>();
            foreach (var v in p.Progress)
            {
                byAction[v.View.Action] = v;
            }
            foreach (var a in p.Spec.Actions)
            {
                if (!Budget(p.Spec.Id.Value + "/" + a.Id.Value))
                {
                    Defer(p.Concern.Value, ReadyDeferredDiscovery);
                    continue;
                }
                var (c, ok) = ReadyAction(p, a, byAction, r.Construction, r.Recipes);
                if (!ok)
                {
                    continue;
                }
                Add(c);
            }
        }
        var proposals = r.Proposals
            .OrderBy(p => p.Concern)
            .ThenBy(p => p.Method)
            .ThenBy(p => p.Stage, StringComparer.Ordinal)
            .ThenBy(p => ClaimsText(SortedClaims(p.Claims)), StringComparer.Ordinal)
            .ToList();
        foreach (var p in proposals)
        {
            if (!Budget(p.Concern.Value + "/" + p.Method.Value + "/" + p.Stage))
            {
                Defer(p.Concern.Value, ReadyDeferredDiscovery);
                continue;
            }
            Add(ReadyProposalWork(p));
        }
        foreach (var g in r.Unserved)
        {
            Add(new ReadyWork { Concerns = EquatableArray.From([g]), Stage = "none", State = ReadyState.NoMethod, Reason = "no plan or proposal" });
        }

        var sorted = output.OrderBy(c => ReadyRank(c.State)).ThenBy(c => c.ID.Value, StringComparer.Ordinal).ToList();
        // Per-goal lookahead after sorting, so the kept candidates do not depend on input order.
        var kept = new List<ReadyWork>();
        foreach (var c in sorted)
        {
            var g = c.Concerns[0];
            if (perConcern.GetValueOrDefault(g) >= b.PerConcern)
            {
                Defer(g.Value, ReadyDeferredPerConcern);
                continue;
            }
            perConcern[g] = perConcern.GetValueOrDefault(g) + 1;
            kept.Add(c);
        }
        if (kept.Count > b.Candidates)
        {
            foreach (var c in kept.Skip(b.Candidates))
            {
                Defer(c.Concerns[0].Value, ReadyDeferredCandidate);
            }
            kept = kept.Take(b.Candidates).ToList();
        }
        var deferrals = deferred
            .Select(kv => new ReadyDeferral(new ConcernId(kv.Key.Concern), kv.Key.Reason, kv.Value))
            .OrderBy(d => d.Concern)
            .ThenBy(d => d.Reason, StringComparer.Ordinal)
            .ToList();
        return new ReadyWorkReport
        {
            Colony = r.Snapshot.Colony,
            Map = r.Snapshot.Map,
            Load = r.Snapshot.Load,
            Tick = r.Tick,
            Candidates = EquatableArray.From(kept),
            Deferred = EquatableArray.From(deferrals),
            Continuation = continuation,
        };
    }

    private static int ReadyRank(ReadyState s) => s switch
    {
        ReadyState.Runnable => 0,
        ReadyState.Awaiting => 1,
        ReadyState.Blocked => 2,
        ReadyState.OpenEffect => 3,
        _ => 4,
    };

    /// <summary>An action kind's concrete stage.</summary>
    private sealed record ReadyStage(string Stage, WorkType Work, IReadOnlyList<ReadyClaim> Claims, bool Staged);

    private static (ReadyStage Stage, bool Ok) ReadyActionStage(DomainAction a, RecipeFacts recipes)
    {
        if (a.ConstructionTarget.Length != 0)
        {
            return (new ReadyStage("construction_setting", WorkType.None, [], false), true);
        }
        if (a.Building is { } building)
        {
            return (new ReadyStage("building:" + building.Definition, WorkType.Construction, [CellClaim(building.Cell)], false), true);
        }
        if (a.Haul is { } haul)
        {
            return (new ReadyStage("haul:" + haul.Definition, WorkType.Hauling, [new ReadyClaim("thing", haul.Thing)], false), true);
        }
        if (a.CutPlant is { } cut)
        {
            return (new ReadyStage("cut_plant:" + cut.Definition, WorkType.PlantCutting, [new ReadyClaim("thing", cut.Plant)], false), true);
        }
        if (a.AreaPlantCut is { } area)
        {
            return (new ReadyStage("area_plant_cut", WorkType.PlantCutting, area.Cells.Select(CellClaim).ToList(), false), true);
        }
        if (a.ProductionBill is { } bill)
        {
            // A recipe no catalog row places at a bench has no known work: the action reports eligibility_unknown.
            var (work, known) = recipes.BillWorkOf(bill.Recipe);
            if (known)
            {
                return (new ReadyStage("bill:" + bill.Recipe, work, [new ReadyClaim("bench", bill.Bench)], false), true);
            }
            return (new ReadyStage("", WorkType.None, [], false), false);
        }
        if (a.GrowerCrop is not null)
        {
            return (new ReadyStage("grow", WorkType.Growing, [], true), true);
        }
        if (a.Kind is ActionKind.ZoneCreate or ActionKind.SupplyAllow or ActionKind.SupplyForbid)
        {
            return (new ReadyStage(Wire.Name(a.Kind), WorkType.None, [], false), true);
        }
        return (new ReadyStage("", WorkType.None, [], false), false);
    }

    private static (ReadyWork Work, bool Ok) ReadyAction(
        ReadyPlan p, DomainAction a, Dictionary<ActionId, Progress> byAction, Fact<CurrentConstruction> census, RecipeFacts recipes)
    {
        var has = byAction.TryGetValue(a.Id, out var prog);
        var v = has ? prog!.View : new ProgressView();
        var blueprint = has && AppliedBuildingOpen(prog!, census);
        if (has && !blueprint && (v.Stage is Stage.Completed or Stage.Unsuccessful or Stage.Cancelled) && !v.Unresolved)
        {
            return (new ReadyWork(), false);
        }
        var c = new ReadyWork { Concerns = EquatableArray.From([p.Concern]), Method = p.Method, Plan = p.Spec.Id, Action = a.Id };
        var (st, migrated) = ReadyActionStage(a, recipes);
        if (!migrated)
        {
            st = new ReadyStage(Wire.Name(a.Kind), WorkType.None, [], false);
        }
        c = c with { Stage = st.Stage, Claims = EquatableArray.From(st.Claims) };
        if (st.Work != WorkType.None)
        {
            c = c with { Work = EquatableArray.From([st.Work]) };
        }
        ReadyWork Runnable(string reason)
        {
            c = c with { State = ReadyState.Runnable };
            if (reason.Length != 0 && c.Reason.Length == 0)
            {
                c = c with { Reason = reason };
            }
            if (c.Work.Count > 0)
            {
                c = c with { Parallelism = 1 };
            }
            return c;
        }
        ReadyWork Set(ReadyState s, string reason)
        {
            c = c with { State = s };
            if (c.Reason.Length == 0 || s != ReadyState.Runnable)
            {
                c = c with { Reason = reason };
            }
            return c;
        }
        if (!has || v.Stage is Stage.Pending or Stage.Prepared)
        {
            var requires = new List<string>();
            foreach (var d in p.Spec.Dependencies)
            {
                if (d.Action != a.Id)
                {
                    continue;
                }
                var ok = byAction.TryGetValue(d.Requires, out var req);
                var rv = ok ? req!.View : new ProgressView();
                var known = rv.Effect.TryGetValue(out var effect);
                if (!ok || rv.Stage != Stage.Completed || rv.Unresolved || !known || effect != Effect.Completed || AppliedBuildingOpen(req!, census))
                {
                    requires.Add(d.Requires.Value);
                }
            }
            if (requires.Count > 0)
            {
                requires.Sort(StringComparer.Ordinal);
                c = c with { Requires = EquatableArray.From(requires) };
                return (Set(ReadyState.Blocked, Wire.Name(HeldReason.DependencyBlocked)), true);
            }
            if (has && v.TryGetFreshHold(out var hold))
            {
                var reasons = hold.Reasons().Select(x => Wire.Name(x));
                return (Set(ReadyState.Blocked, string.Join(",", reasons)), true);
            }
            if (!migrated)
            {
                // No stage adapter: nothing observed says a worker can take it, and the goal's labor profile
                // is not a guess to stand in.
                return (Set(ReadyState.Awaiting, "eligibility_unknown"), true);
            }
            return (Runnable("admission"), true);
        }
        // An applied building whose blueprint or frame still stands.
        if (blueprint)
        {
            return (Set(ReadyState.Awaiting, "blueprint_open"), true);
        }
        // Dispatched or awaiting: the action is admitted and its effect open.
        if (!v.Effect.TryGetValue(out var observed) || observed == Effect.Unknown)
        {
            return (Set(ReadyState.Awaiting, "effect_unknown"), true);
        }
        if (!migrated)
        {
            return (Set(ReadyState.Awaiting, "eligibility_unknown"), true);
        }
        if (st.Work == WorkType.None)
        {
            return (Set(ReadyState.OpenEffect, "settings_write"), true);
        }
        if (st.Staged)
        {
            var inputs = p.Inputs.TryGetValue(a.Id, out var f) ? f : Unknown<bool>();
            if (!inputs.TryGetValue(out var ready))
            {
                return (Set(ReadyState.Awaiting, "stage_inputs_unknown"), true);
            }
            if (!ready)
            {
                return (Set(ReadyState.OpenEffect, "stage_inputs_missing"), true);
            }
            return (Runnable("stage_inputs_ready"), true);
        }
        return (Runnable("designated"), true);
    }

    private static ReadyWork ReadyProposalWork(ReadyProposal p)
    {
        var c = new ReadyWork
        {
            Concerns = EquatableArray.From([p.Concern]),
            Method = p.Method,
            Stage = p.Stage,
            Claims = EquatableArray.From(p.Claims),
            Alternative = p.Alternative,
            Reason = p.Reason,
        };
        if (p.Work != WorkType.None)
        {
            c = c with { Work = EquatableArray.From([p.Work]) };
        }
        c = c with { Requires = EquatableArray.From(p.Requires.OrderBy(x => x, StringComparer.Ordinal)) };
        var known = p.Eligible.TryGetValue(out var eligible);
        if (c.Requires.Count > 0)
        {
            return c with { State = ReadyState.Blocked, Reason = Wire.Name(HeldReason.DependencyBlocked) };
        }
        if (!known)
        {
            return c with { State = ReadyState.Awaiting, Reason = "eligibility_unknown" };
        }
        if (!eligible)
        {
            c = c with { State = ReadyState.Blocked };
            if (c.Reason.Length == 0)
            {
                c = c with { Reason = Wire.Name(HeldReason.NativeIneligible) };
            }
            return c;
        }
        c = c with { State = ReadyState.Runnable };
        if (c.Work.Count > 0)
        {
            c = c with { Parallelism = Math.Max(p.Parallelism, 1) };
        }
        return c;
    }

    private static List<ConcernId> SortedConcerns(IEnumerable<ConcernId> goals)
    {
        var seen = new HashSet<ConcernId>();
        var output = new List<ConcernId>();
        foreach (var g in goals)
        {
            if (seen.Add(g))
            {
                output.Add(g);
            }
        }
        output.Sort();
        return output;
    }

    private static List<ReadyClaim> SortedClaims(IEnumerable<ReadyClaim> claims) =>
        claims.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();

    /// <summary>Go's fmt.Sprint of a claim slice, the proposal sort's last key.</summary>
    private static string ClaimsText(IEnumerable<ReadyClaim> claims) =>
        "[" + string.Join(" ", claims.Select(c => "{" + c.Kind + " " + c.Key + "}")) + "]";

    private static ReadyWorkId ReadyId(GenerationSnapshot s, ReadyWork c)
    {
        var text = new StringBuilder();
        var work = "[" + string.Join(" ", c.Work.Select(w => Wire.Name(w))) + "]";
        text.Append(s.Colony.Value).Append('|').Append(s.Map.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(s.Load.Value).Append('|').Append(c.Stage).Append('|').Append(work).Append('|')
            .Append(c.State == ReadyState.NoMethod ? "true" : "false").Append('|');
        if (c.Claims.Count == 0)
        {
            // Without claims the work is only identified by its origin.
            text.Append(c.Concerns[0].Value).Append('|').Append(c.Method.Value).Append('|').Append(c.Plan.Value).Append('|').Append(c.Action.Value).Append('|');
        }
        foreach (var k in c.Claims)
        {
            text.Append(k.Kind).Append('=').Append(k.Key).Append(';');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return new ReadyWorkId(Convert.ToHexStringLower(hash)[..16]);
    }
}
