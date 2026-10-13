using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>
/// Binds a Response the review assessed to its open occurrence: the incident a planner commits methods to, and
/// the need the review measured for it. A recovered occurrence stays bound, and open, while its dispatched work
/// (a fight's drafts) settles (Go <c>RoundsIncident</c>).
/// </summary>
public sealed record RoundsIncident
{
    public ConcernId Kind { get; init; }

    /// <summary>The pawn of a per-subject occurrence (EnsureMood: one per pawn); empty for a colony-wide one.</summary>
    public PawnId Subject { get; init; }

    public IncidentId Incident { get; init; }

    public Situation Situation { get; init; }
}

public sealed partial class Store
{
    private const int MaxOpenIncidentIds = 512;

    /// <summary>The id of the open occurrence under the key (the world, <paramref name="kind"/>, <paramref name="subject"/>), or null.</summary>
    internal static IncidentId? OpenIncidentId(StoreTransaction tx, GenerationSnapshot world, ConcernId kind, PawnId subject) =>
        tx.ScalarText(
            "SELECT id FROM incidents WHERE colony=$colony AND load_token=$load AND map_id=$map AND kind=$kind AND subject=$subject AND ended_tick IS NULL",
            ("$colony", world.Colony),
            ("$load", world.Load),
            ("$map", world.Map),
            ("$kind", kind),
            ("$subject", subject)
        ) is { } id
            ? new IncidentId(id)
            : null;

    /// <summary>
    /// Opens, refreshes and closes the incident kinds' occurrences from an enabled review's assessments: a
    /// deficit opens (or re-triggers) its occurrence; a recovered one closes once no work is open, as a goal
    /// satisfies; an unknown one keeps an open occurrence and opens none. An open occurrence the review no longer
    /// binds closes. Returns the bindings and the occurrences they name (Go <c>reviewIncidents</c>).
    /// </summary>
    internal static (EquatableArray<RoundsIncident> Bindings, List<IncidentState> States) ReviewIncidents(
        StoreTransaction tx,
        IReadOnlyList<RoundsAssessment> assessments,
        RoundsRequest request,
        Rounds previous
    )
    {
        var current = request.Current;
        var tick = request.Tick;
        var bindings = new List<RoundsIncident>();
        var states = new List<IncidentState>();
        foreach (var n in assessments)
        {
            var subject = (PawnId)n.Subject;
            var open = OpenIncidentId(tx, current, n.Id, subject);
            if (n.Finding != Finding.Unmet && open is null)
            {
                continue;
            }
            if (request.Immediate && open is { } recoveringId && n.Id == PolicyFunctions.RecoverDisasterServices && n.Finding == Finding.Unclear)
            {
                var recovering = LoadIncident(tx, recoveringId);
                bindings.Add(previous.IncidentBinding(recoveringId) ?? throw new DomainException("recovery lost review binding"));
                states.Add(recovering);
                continue;
            }
            if (request.Immediate && open is { } huntingId && n.Id == PolicyFunctions.ActiveCombat)
            {
                var foodKnown = request.Facts.FoodPlan.IsKnown;
                var hostileKnown = request.Facts.Hostiles.TryGetValue(out var hostiles);
                if (!foodKnown && (!hostileKnown || hostiles == 0))
                {
                    var hunting = LoadIncident(tx, huntingId);
                    if (hunting.Incident.HuntPrey.Count > 0)
                    {
                        bindings.Add(previous.IncidentBinding(huntingId) ?? throw new DomainException("active hunt lost review binding"));
                        states.Add(hunting);
                        continue;
                    }
                }
            }
            if (n.Finding == Finding.Met && open is { } metId)
            {
                var met = LoadIncident(tx, metId);
                CancelUndispatchedMethods(tx, met);
                if (!StandardOpenWork(tx, met))
                {
                    _ = CloseIncidentIn(tx, metId, tick);
                    continue;
                }
            }
            var trigger = n.Id.Value + " deficit";
            var huntPrey = default(EquatableArray<PawnId>);
            if (n.Hunt.Count > 0 && n.Finding == Finding.Unmet)
            {
                // The food plan raised this occurrence: its hunt origin.
                huntPrey = EquatableArray.From(n.Hunt.Select(prey => (PawnId)prey));
                trigger = n.Id.Value + " hunt";
            }
            var opened = OpenIncidentIn(tx, new IncidentAssessment(n.Id, subject, trigger, n.Priority, current, tick, huntPrey));
            bindings.Add(new RoundsIncident { Kind = n.Id, Subject = subject, Incident = opened.Incident.Id, Situation = n.Finding.Situation() });
            states.Add(opened);
        }
        foreach (var id in OpenIncidentIds(tx, new World(current.Colony, current.Load, current.Map)))
        {
            if (bindings.Any(b => b.Incident == id))
            {
                continue;
            }
            if (request.Immediate && !PolicyFunctions.ImmediateConcern(LoadIncident(tx, id).Incident.Kind))
            {
                continue;
            }
            AbandonIncident(tx, id, tick);
        }
        return (EquatableArray.From(bindings), states);
    }

    /// <summary>
    /// Closes an occurrence whose world was replaced, rewound or stopped assessing it, cancelling all its work as
    /// an invalidated goal's is. A closed occurrence is left alone (Go <c>abandonIncident</c>).
    /// </summary>
    internal static void AbandonIncident(StoreTransaction tx, IncidentId id, Tick tick)
    {
        var state = LoadIncident(tx, id);
        if (state.Incident.Closed)
        {
            return;
        }
        CancelMethods(tx, state);
        _ = CloseIncidentIn(tx, id, tick > state.Incident.Started ? tick : state.Incident.Started);
    }

    /// <summary>The world's open occurrences, oldest first; more than 512 is <see cref="CapacityException"/> (Go <c>openIncidentIDs</c>).</summary>
    internal static List<IncidentId> OpenIncidentIds(StoreTransaction tx, World world)
    {
        var ids = tx.Query(
            "SELECT id FROM incidents WHERE colony=$colony AND load_token=$load AND map_id=$map AND ended_tick IS NULL ORDER BY started_tick,id LIMIT 513",
            [("$colony", world.Colony), ("$load", world.Load), ("$map", world.Map)],
            r => new IncidentId(r.GetString(0))
        );
        return ids.Count > MaxOpenIncidentIds ? throw new CapacityException() : ids;
    }

    /// <summary><paramref name="kind"/>'s newest occurrence in <paramref name="world"/>, open or closed; null when it has none.</summary>
    public IncidentState? LatestIncident(World world, ConcernId kind, CancellationToken cancellationToken) =>
        Run<IncidentState?>(
            tx =>
            {
                var id = tx.ScalarText(
                    "SELECT id FROM incidents WHERE colony=$colony AND load_token=$load AND map_id=$map AND kind=$kind ORDER BY started_tick DESC, rowid DESC LIMIT 1",
                    ("$colony", world.Colony),
                    ("$load", world.Load),
                    ("$map", world.Map),
                    ("$kind", kind)
                );
                return id is null ? null : LoadIncident(tx, new IncidentId(id));
            },
            cancellationToken
        );

    public Task<IncidentState?> LatestIncidentAsync(World world, ConcernId kind, CancellationToken cancellationToken) =>
        Task.Run(() => LatestIncident(world, kind, cancellationToken), cancellationToken);

    /// <summary>Every occurrence of <paramref name="kind"/> in <paramref name="world"/>, open or closed, oldest first.</summary>
    public IReadOnlyList<IncidentState> IncidentHistory(World world, ConcernId kind, CancellationToken cancellationToken) =>
        Run<IReadOnlyList<IncidentState>>(
            tx =>
            {
                var ids = tx.Query(
                    "SELECT id FROM incidents WHERE colony=$colony AND load_token=$load AND map_id=$map AND kind=$kind ORDER BY started_tick, rowid",
                    [("$colony", world.Colony), ("$load", world.Load), ("$map", world.Map), ("$kind", kind)],
                    r => new IncidentId(r.GetString(0))
                );
                return [.. ids.Select(id => LoadIncident(tx, id))];
            },
            cancellationToken
        );

    public Task<IReadOnlyList<IncidentState>> IncidentHistoryAsync(World world, ConcernId kind, CancellationToken cancellationToken) =>
        Task.Run(() => IncidentHistory(world, kind, cancellationToken), cancellationToken);

    /// <summary>
    /// Refuses a review whose incident bindings are too many, duplicated, not incident kinds, carry an unknown
    /// situation or name an occurrence that is closed or belongs to another key or world (Go
    /// <c>validateRoundsIncidents</c>).
    /// </summary>
    internal static void ValidateRoundsIncidents(StoreTransaction tx, Rounds r)
    {
        DomainException.Require(r.Incidents.Count <= MaxOpenIncidentIds, "invalid routine incident bindings");
        var seen = new HashSet<(ConcernId Kind, PawnId Subject)>();
        foreach (var b in r.Incidents)
        {
            DomainException.Require(PolicyFunctions.IsIncidentKind(b.Kind) && seen.Add((b.Kind, b.Subject)), "invalid routine incident binding");
            DomainException.Require(b.Situation is Situation.Active or Situation.Clear or Situation.Unclear, "invalid routine incident need");
            var state = LoadIncident(tx, b.Incident);
            DomainException.Require(
                !state.Incident.Closed && state.Incident.Kind == b.Kind && state.Incident.Subject == b.Subject && state.Incident.Snapshot.SameWorld(r.Snapshot),
                "routine incident binding mismatch"
            );
        }
    }
}
