using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using static RimGovernor.Brain.Store.StorePackage;

namespace RimGovernor.Brain.Store;

/// <summary>
/// One Response occurrence and the methods bound to it (Go <c>IncidentState</c>). Its methods live beside the
/// standards' ones, with an incident id instead of a standard id, and go through the same admission.
/// </summary>
/// <param name="Methods">Every method ever committed, retired plans included.</param>
public sealed record IncidentState(Incident Incident, EquatableArray<IncidentMethod> Methods) : IMethodOwner
{
    GenerationSnapshot IMethodOwner.OwnerSnapshot => Incident.Snapshot;

    int IMethodOwner.OwnerPriority => Incident.Priority;

    IReadOnlyList<PlanId> IMethodOwner.OwnerPlans => [.. Methods.Select(m => m.Plan)];

    string IMethodOwner.OwnerLabel => "incident " + Incident.Id.Value;

    bool IMethodOwner.OwnerNeed(Rounds rounds, out ConcernId need)
    {
        need = Incident.Kind;
        return true;
    }

    PlanId? IMethodOwner.BoundPlan(StoreTransaction tx, MethodId method) =>
        tx.ScalarText("SELECT plan_id FROM incident_methods WHERE incident_id=$id AND method_id=$method", ("$id", Incident.Id), ("$method", method)) is { } plan
            ? new PlanId(plan)
            : null;

    void IMethodOwner.BindMethod(StoreTransaction tx, MethodId method, PlanId plan, string reason)
    {
        Store.InsertPlanOwner(tx, plan, "incident");
        _ = tx.Execute(
            "INSERT INTO incident_methods(incident_id,method_id,plan_id,priority,reason) VALUES($id,$method,$plan,$priority,$reason)",
            ("$id", Incident.Id),
            ("$method", method),
            ("$plan", plan),
            ("$priority", Incident.Priority),
            ("$reason", reason.Length == 0 ? null : reason)
        );
    }

    long IMethodOwner.ServedCount(StoreTransaction tx) => tx.Scalar("SELECT count(*) FROM incident_methods WHERE incident_id=$id", ("$id", Incident.Id));
}

/// <summary>A method bound to an incident and the plan that executes it.</summary>
public sealed record IncidentMethod(MethodId Method, PlanId Plan);

/// <summary>
/// What a review asserts about one occurrence: its key (the snapshot's world, <see cref="Kind"/>, <see cref="Subject"/>),
/// the trigger it opens with and the priority the assessment ranks it at.
/// </summary>
/// <param name="HuntPrey">The squad prey of the assessment; empty unless the food plan raises it with no hostile standing.</param>
public sealed record IncidentAssessment(
    ConcernId Kind,
    PawnId Subject,
    string Trigger,
    int Priority,
    GenerationSnapshot Snapshot,
    Tick Tick,
    EquatableArray<PawnId> HuntPrey
);

public sealed partial class Store
{
    private const int MaxOpenIncidents = 256;

    /// <summary>
    /// Opens the occurrence the assessment asserts, or, when one is already open under its key, refreshes that
    /// row's priority, snapshot and hunt prey from the assessment and keeps its id, start and trigger: a
    /// re-trigger inside an occurrence is idempotent.
    /// </summary>
    public IncidentState OpenIncident(IncidentAssessment assessment, CancellationToken cancellationToken) => Run(tx => OpenIncidentIn(tx, assessment), cancellationToken);

    public Task<IncidentState> OpenIncidentAsync(IncidentAssessment assessment, CancellationToken cancellationToken) => RunAsync(tx => OpenIncidentIn(tx, assessment), cancellationToken);

    internal static IncidentState OpenIncidentIn(StoreTransaction tx, IncidentAssessment a)
    {
        var next = new Incident
        {
            Kind = a.Kind,
            Subject = a.Subject,
            Trigger = a.Trigger,
            Priority = a.Priority,
            Snapshot = a.Snapshot,
            Started = a.Tick,
            HuntPrey = a.HuntPrey,
        };
        var existing = tx.ScalarText(
            "SELECT id FROM incidents WHERE colony=$colony AND load_token=$load AND map_id=$map AND kind=$kind AND subject=$subject AND ended_tick IS NULL",
            ("$colony", a.Snapshot.Colony),
            ("$load", a.Snapshot.Load),
            ("$map", a.Snapshot.Map),
            ("$kind", a.Kind),
            ("$subject", a.Subject)
        );
        if (existing is null)
        {
            next = next with { Id = IncidentIds.MintIncidentId() };
            tx.ExecuteOrConflict(
                "INSERT INTO incidents(id,colony,load_token,map_id,kind,subject,started_tick,payload) VALUES($id,$colony,$load,$map,$kind,$subject,$started,$payload)",
                ("$id", next.Id),
                ("$colony", a.Snapshot.Colony),
                ("$load", a.Snapshot.Load),
                ("$map", a.Snapshot.Map),
                ("$kind", a.Kind),
                ("$subject", a.Subject),
                ("$started", a.Tick),
                ("$payload", CanonicalIncident(next))
            );
        }
        else
        {
            var id = new IncidentId(existing);
            var state = LoadIncident(tx, id);
            if (a.Tick < state.Incident.Started)
            {
                throw new ConflictException($"incident {id} assessed before it started");
            }
            var refreshed = state.Incident with { Priority = a.Priority, Snapshot = a.Snapshot, HuntPrey = a.HuntPrey };
            _ = tx.Execute("UPDATE incidents SET payload=$payload WHERE id=$id", ("$payload", CanonicalIncident(refreshed)), ("$id", id));
            next = next with { Id = id };
        }
        return LoadIncident(tx, next.Id);
    }

    /// <summary>
    /// Ends the occurrence at <paramref name="tick"/>; the next assessment under its key opens a new row. Methods
    /// no step dispatched are cancelled, as a recovered standard's are; dispatched work settles through its journal.
    /// </summary>
    public IncidentState CloseIncident(IncidentId id, Tick tick, CancellationToken cancellationToken) => Run(tx => CloseIncidentIn(tx, id, tick), cancellationToken);

    public Task<IncidentState> CloseIncidentAsync(IncidentId id, Tick tick, CancellationToken cancellationToken) => RunAsync(tx => CloseIncidentIn(tx, id, tick), cancellationToken);

    internal static IncidentState CloseIncidentIn(StoreTransaction tx, IncidentId id, Tick tick)
    {
        var state = LoadIncident(tx, id);
        if (state.Incident.Closed)
        {
            throw new ConflictException($"incident {id} already closed");
        }
        var closed = state.Incident with { Closed = true, Ended = tick };
        var data = CanonicalIncident(closed);
        CancelUndispatchedMethods(tx, state);
        _ = tx.Execute("UPDATE incidents SET ended_tick=$ended,payload=$payload WHERE id=$id", ("$ended", tick), ("$payload", data), ("$id", id));
        return LoadIncident(tx, id);
    }

    public IncidentState LoadIncident(IncidentId id, CancellationToken cancellationToken) => Run(tx => LoadIncident(tx, id), cancellationToken);

    public Task<IncidentState> LoadIncidentAsync(IncidentId id, CancellationToken cancellationToken) => RunAsync(tx => LoadIncident(tx, id), cancellationToken);

    /// <summary>The world's open occurrences, oldest first.</summary>
    public IReadOnlyList<IncidentState> OpenIncidents(World world, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var ids = tx.Query(
                    "SELECT id FROM incidents WHERE colony=$colony AND load_token=$load AND map_id=$map AND ended_tick IS NULL ORDER BY started_tick,id LIMIT 257",
                    [("$colony", world.Colony), ("$load", world.Load), ("$map", world.Map)],
                    r => new IncidentId(r.GetString(0))
                );
                return ids.Count > MaxOpenIncidents ? throw new CapacityException() : (IReadOnlyList<IncidentState>)[.. ids.Select(id => LoadIncident(tx, id))];
            },
            cancellationToken
        );

    public Task<IReadOnlyList<IncidentState>> OpenIncidentsAsync(World world, CancellationToken cancellationToken) =>
        Task.Run(() => OpenIncidents(world, cancellationToken), cancellationToken);

    private static string CanonicalIncident(Incident i)
    {
        i.Validate();
        return StoreJson.Serialize(i);
    }

    internal static IncidentState LoadIncident(StoreTransaction tx, IncidentId id)
    {
        var payload = tx.ScalarText("SELECT payload FROM incidents WHERE id=$id", ("$id", id)) ?? throw new NotFoundException();
        var incident = StoreJson.Deserialize<Incident>(payload);
        try
        {
            incident.Validate();
        }
        catch (DomainException e)
        {
            throw new StoreCorruptException("incident: " + e.Message);
        }
        if (incident.Id != id)
        {
            throw new StoreCorruptException("incident identity mismatch");
        }
        var methods = tx.Query(
            "SELECT m.method_id,m.plan_id FROM incident_methods m JOIN plans p ON p.id=m.plan_id WHERE m.incident_id=$id AND (p.retired=0 OR (m.method_id NOT LIKE 'batch-%' AND m.method_id NOT LIKE 'restore-%')) ORDER BY m.method_id LIMIT 257",
            [("$id", id)],
            r => new IncidentMethod(new MethodId(r.GetString(0)), new PlanId(r.GetString(1)))
        );
        return new IncidentState(incident, EquatableArray.From(methods));
    }

    /// <summary>
    /// Stores a method for an open incident and its shared plan atomically, through the Safeguards and per-family
    /// admission a standard's method takes. Like <see cref="CommitMethod"/> it grants no authority to dispatch.
    /// </summary>
    public IncidentState CommitIncidentMethod(IncidentId id, MethodId method, string reason, PlanSpec plan, CancellationToken cancellationToken)
    {
        plan.Validate();
        return plan.Actions.Count == 0
            ? throw new DomainException("empty incident method")
            : Run(tx => CommitIncidentMethodIn(tx, id, method, reason, plan, fightWork: false), cancellationToken);
    }

    public Task<IncidentState> CommitIncidentMethodAsync(IncidentId id, MethodId method, string reason, PlanSpec plan, CancellationToken cancellationToken) =>
        Task.Run(() => CommitIncidentMethod(id, method, reason, plan, cancellationToken), cancellationToken);

    /// <summary>
    /// <paramref name="fightWork"/> admits the method beside the incident's open fight: only a plan still open
    /// refuses it.
    /// </summary>
    internal static IncidentState CommitIncidentMethodIn(StoreTransaction tx, IncidentId id, MethodId method, string reason, PlanSpec plan, bool fightWork)
    {
        var state = LoadIncident(tx, id);
        if (state.Incident.Closed)
        {
            throw new ConflictException($"incident {id} is closed");
        }
        AdmitRoundsSafeguards(tx, state);
        var open = fightWork ? PlanOpenWork(tx, state) : StandardOpenWork(tx, state);
        if (open && state.Incident.Kind == PolicyFunctions.RecoverDisasterServices && RecoveryAreaOnly(plan))
        {
            open = RecoveryAreaConflictingWork(tx, state);
        }
        if (open)
        {
            throw new OpenMethodException();
        }
        if (string.IsNullOrWhiteSpace(method.Value))
        {
            throw new DomainException("incident method needs an id");
        }
        BindOwnerMethod(tx, state, method, reason, plan);
        return LoadIncident(tx, id);
    }

    /// <summary>The guard for an incident's plan: the occurrence is open in the current world and no Safeguard vetoes it.</summary>
    internal static void GuardIncidentWork(StoreTransaction tx, IncidentId id, GenerationSnapshot current, Tick tick)
    {
        var state = LoadIncident(tx, id);
        var i = state.Incident;
        if (i.Closed || !i.Snapshot.SameWorld(current) || tick < i.Started)
        {
            throw new DomainException("incident does not admit current work");
        }
        AdmitRoundsSafeguards(tx, state);
    }

    /// <summary>
    /// A protective area setting may coexist with ordinary service jobs on the recovery incident. All other open
    /// methods retain exclusive admission.
    /// </summary>
    private static bool RecoveryAreaOnly(PlanSpec plan) =>
        plan.Actions.Count > 0
        && plan.Actions.All(action =>
            (action.WorkAssignment is { } work && work.HasArea && work.Settings.Count == 0 && !work.HasSchedule)
            || (action.Husbandry is { } animal && animal.Method == HusbandryMethod.AllowedArea)
        );

    private static bool RecoveryAreaConflictingWork(StoreTransaction tx, IncidentState state)
    {
        foreach (var id in ((IMethodOwner)state).OwnerPlans)
        {
            var plan = LoadIn(tx, id);
            if (CombatFightHolds(tx, id))
            {
                return true;
            }
            if (!PlanOpen(plan))
            {
                continue;
            }
            if (plan.Progress.Any(progress => Standard.WorkOpen([progress]) && progress.Action.Kind != ActionKind.RecoveryService))
            {
                return true;
            }
        }
        return false;
    }
}
