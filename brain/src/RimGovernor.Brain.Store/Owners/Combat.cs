using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>One order of a stop and its native outcome.</summary>
public sealed record CombatOrderRecord(CombatOrder Order, bool Applied, bool Uncertain = false, string Refusal = "");

/// <summary>One stop's changed orders.</summary>
public sealed record CombatStopRecord(PlanId Batch, Tick Tick, StopEvent Stop, EquatableArray<CombatOrderRecord> Orders);

/// <summary>
/// A fight's state: open while it runs, the DecideCombat memory after its latest stop, and the roster of pawns it
/// drafted in <see cref="World"/>.
/// </summary>
public sealed record CombatFight(bool Open, CombatMemory Memory, World World, IReadOnlySet<PawnId> Roster);

/// <summary>The stored shape of a fight's roster.</summary>
internal sealed record CombatRoster(World World, EquatableArray<PawnId> Pawns);

/// <summary>Save-owned intent to undo temporary fight settings: original settings only, never attacks, attempts or receipts.</summary>
public sealed record CombatRestoration(World World, PlanId Owner, EquatableArray<CombatDoorRestoration> Doors, EquatableArray<CombatAnimalRestoration> Animals)
{
    internal void Validate()
    {
        var total = Doors.Count + Animals.Count;
        if (!World.IsValid || !Identity.IsValidText(Owner.Value) || total == 0 || total > 256)
        {
            throw new CapacityException();
        }
        var doors = new HashSet<Cell>();
        foreach (var d in Doors)
        {
            if (d.Cell.X < 0 || d.Cell.Z < 0 || !doors.Add(d.Cell))
            {
                throw new ConflictException();
            }
        }
        var animals = new HashSet<PawnId>();
        foreach (var a in Animals)
        {
            if (!Identity.IsValidText(a.Pawn.Value) || (a.Area.Length != 0 && !Identity.IsValidText(a.Area)) || !animals.Add(a.Pawn))
            {
                throw new ConflictException();
            }
        }
    }
}

public readonly record struct CombatDoorRestoration(Cell Cell, bool HoldOpen, bool Forbidden);

public sealed record CombatAnimalRestoration(PawnId Pawn, string Area);

public sealed partial class Store
{
    /// <summary>Bounds retained fight evidence to the newest stops; OTLP recordings retain the full history.</summary>
    public const int CombatEvidenceStops = 32;

    public const string GovernorCombatRestorationKey = "family/combat_restoration";

    private const int MaxCombatRestorationBytes = 128 * 1024;

    /// <summary>
    /// Admits a fight: the ActiveCombat incident's method on <paramref name="plan"/>, whose actions are its threat
    /// loadout's equip and wear actions or a guarded subdue response, and its open fight row with the roster in one
    /// transaction (the admission batch drafts them; a loadout pawn drafts later, so the roster may be empty then).
    /// </summary>
    public IncidentState CommitCombatFight(
        IncidentId incident,
        MethodId method,
        PlanSpec plan,
        CombatMemory memory,
        World world,
        IReadOnlyList<PawnId> roster,
        CancellationToken cancellationToken
    )
    {
        plan.Validate();
        if (plan.Actions.Any(a => a.Kind is not (ActionKind.Equip or ActionKind.GearReplace or ActionKind.OwnedDraft or ActionKind.Subdue)))
        {
            throw new DomainException("combat fight plan holds only loadout or subdue actions");
        }
        if (roster.Count + plan.Actions.Count == 0 || !world.IsValid)
        {
            throw new DomainException("combat fight needs a roster or a loadout, and a world");
        }
        return Run(
            tx =>
            {
                var state = CommitIncidentMethodIn(tx, incident, method, "", plan, fightWork: false);
                InsertCombatFight(tx, plan.Id, memory, world, roster);
                return state;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Admits a strip of a downed raider beside the incident's open fight: a method whose plan holds only strip
    /// actions, which the fight's own open work does not refuse.
    /// </summary>
    public IncidentState CommitFightStrip(IncidentId incident, MethodId method, PlanSpec plan, CancellationToken cancellationToken)
    {
        plan.Validate();
        if (plan.Actions.Count == 0)
        {
            throw new DomainException("empty fight strip");
        }
        if (plan.Actions.Any(a => a.Kind != ActionKind.Strip))
        {
            throw new DomainException("fight strip plan holds only strip actions");
        }
        return Run(tx => CommitIncidentMethodIn(tx, incident, method, "", plan, fightWork: true), cancellationToken);
    }

    /// <summary>Records an open fight for an existing plan, as <see cref="CommitCombatFight"/> does; a fight already recorded is left as it is.</summary>
    public void OpenCombatFight(PlanId plan, CombatMemory memory, World world, IReadOnlyList<PawnId> roster, CancellationToken cancellationToken) =>
        Run(tx => InsertCombatFight(tx, plan, memory, world, roster), cancellationToken);

    private static string EncodeRoster(World world, IEnumerable<PawnId> roster) => StoreJson.Serialize(new CombatRoster(world, EquatableArray.From(roster.OrderBy(p => p.Value, StringComparer.Ordinal).ToList())));

    private static void InsertCombatFight(StoreTransaction tx, PlanId plan, CombatMemory memory, World world, IEnumerable<PawnId> roster) =>
        tx.Execute(
            "INSERT OR IGNORE INTO combat_fights(plan_id,open,memory,roster) VALUES($plan,1,$memory,$roster)",
            ("$plan", plan),
            ("$memory", StoreJson.Serialize(memory)),
            ("$roster", EncodeRoster(world, roster.Distinct()))
        );

    private static CombatFight DecodeCombatFight(long open, string memory, string roster)
    {
        var held = StoreJson.Deserialize<CombatRoster>(roster);
        return new CombatFight(open == 1, StoreJson.Deserialize<CombatMemory>(memory), held.World, held.Pawns.ToHashSet());
    }

    /// <summary>The plan's fight, if it has one.</summary>
    public CombatFight? LoadCombatFight(PlanId plan, CancellationToken cancellationToken) => Run(tx => LoadCombatFightIn(tx, plan), cancellationToken);

    internal static CombatFight? LoadCombatFightIn(StoreTransaction tx, PlanId plan)
    {
        var (row, found) = tx.QueryFirst("SELECT open,memory,roster FROM combat_fights WHERE plan_id=$plan", [("$plan", plan)], r => (Open: r.GetInt64(0), Memory: r.GetString(1), Roster: r.GetString(2)));
        return found ? DecodeCombatFight(row.Open, row.Memory, row.Roster) : null;
    }

    /// <summary>Whether the plan is a fight still open.</summary>
    internal static bool CombatFightHolds(StoreTransaction tx, PlanId plan) => LoadCombatFightIn(tx, plan) is { Open: true };

    /// <summary>Every open fight.</summary>
    public IReadOnlyDictionary<PlanId, CombatFight> OpenCombatFights(CancellationToken cancellationToken) =>
        Run(
            tx =>
                (IReadOnlyDictionary<PlanId, CombatFight>)
                    tx.Query("SELECT plan_id,open,memory,roster FROM combat_fights WHERE open=1", [], r => (Plan: new PlanId(r.GetString(0)), Fight: DecodeCombatFight(r.GetInt64(1), r.GetString(2), r.GetString(3))))
                        .ToDictionary(f => f.Plan, f => f.Fight),
            cancellationToken
        );

    /// <summary>Adds the pawns a stop drafted and forgets those whose draft order was refused: the fight no longer needs them drafted.</summary>
    public void UpdateCombatRoster(PlanId plan, IReadOnlyList<PawnId> add, IReadOnlyList<PawnId> drop, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var fight = LoadCombatFightIn(tx, plan) ?? throw new NotFoundException();
                var roster = fight.Roster.ToHashSet();
                roster.UnionWith(add);
                roster.ExceptWith(drop);
                _ = tx.Execute("UPDATE combat_fights SET roster=$roster WHERE plan_id=$plan", ("$roster", EncodeRoster(fight.World, roster)), ("$plan", plan));
            },
            cancellationToken
        );

    /// <summary>Ends the plan's fight; the undraft sweep then undrafts its roster.</summary>
    public void CloseCombatFight(PlanId plan, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                if (LoadCombatRestorationIn(tx) is { } kept && kept.Owner == plan)
                {
                    throw new ConflictException();
                }
                _ = tx.Execute("UPDATE combat_fights SET open=0 WHERE plan_id=$plan", ("$plan", plan));
                SupersedeCombatBatches(tx, plan, plan, restored: false);
            },
            cancellationToken
        );

    /// <summary>Replaces an open fight's memory without evidence: a re-formation that ordered nothing.</summary>
    public void SaveCombatMemory(PlanId plan, CombatMemory memory, CancellationToken cancellationToken) =>
        Run(tx => tx.Execute("UPDATE combat_fights SET memory=$memory WHERE plan_id=$plan", ("$memory", StoreJson.Serialize(memory)), ("$plan", plan)), cancellationToken);

    /// <summary>
    /// Appends one stop's orders to the plan's evidence, keeps the newest <see cref="CombatEvidenceStops"/> and saves
    /// the memory after it. A stop with no orders is not recorded.
    /// </summary>
    public void RecordCombatStop(PlanId plan, CombatStopRecord stop, CombatMemory memory, CancellationToken cancellationToken)
    {
        if (stop.Orders.Count == 0 || stop.Tick.Value < 0)
        {
            throw new DomainException("combat stop records no orders");
        }
        Run(
            tx =>
            {
                if (tx.Execute("UPDATE combat_fights SET memory=$memory WHERE plan_id=$plan", ("$memory", StoreJson.Serialize(memory)), ("$plan", plan)) != 1)
                {
                    throw new DomainException("combat fight not recorded");
                }
                _ = tx.Execute(
                    "INSERT INTO combat_evidence(plan_id,sequence,tick,payload) VALUES($plan,(SELECT COALESCE(MAX(sequence),0)+1 FROM combat_evidence WHERE plan_id=$plan),$tick,$payload)",
                    ("$plan", plan),
                    ("$tick", stop.Tick),
                    ("$payload", StoreJson.Serialize(stop))
                );
                _ = tx.Execute(
                    "DELETE FROM combat_evidence WHERE plan_id=$plan AND sequence<=(SELECT MAX(sequence) FROM combat_evidence WHERE plan_id=$plan)-$keep",
                    ("$plan", plan),
                    ("$keep", CombatEvidenceStops)
                );
                if (stop.Batch.Value.Length != 0)
                {
                    SupersedeCombatBatches(tx, plan, stop.Batch, restored: false);
                }
            },
            cancellationToken
        );
    }

    /// <summary>The plan's recorded stops, oldest first.</summary>
    public IReadOnlyList<CombatStopRecord> CombatEvidence(PlanId plan, CancellationToken cancellationToken) =>
        Run(
            tx => (IReadOnlyList<CombatStopRecord>)[.. tx.Query("SELECT payload FROM combat_evidence WHERE plan_id=$plan ORDER BY sequence", [("$plan", plan)], r => StoreJson.Deserialize<CombatStopRecord>(r.GetString(0)))],
            cancellationToken
        );

    // ---- batches -----------------------------------------------------------------------------------------

    /// <summary>
    /// Binds an immutable ordered batch beside its fight. Pending tactical work does not prevent a new protective
    /// batch; the executor still checks the Incident and current authority before dispatch.
    /// </summary>
    public PlanState CommitCombatBatch(PlanSpec plan, CancellationToken cancellationToken)
    {
        if (!IsValidPlan(plan) || plan.Actions.Count != 1 || plan.Actions[0].CombatBatch is not { } batch)
        {
            throw new ConflictException();
        }
        return Run(
            tx =>
            {
                if (tx.Scalar("SELECT count(*) FROM plans WHERE id=$id", ("$id", plan.Id)) != 0)
                {
                    var got = LoadIn(tx, plan.Id);
                    return got.Spec.Actions.Count == 1 && got.Spec.Actions[0] == plan.Actions[0] ? got : throw new ConflictException();
                }
                var incident = tx.ScalarText(
                    "SELECT i.incident_id FROM incident_methods i JOIN combat_fights f ON f.plan_id=i.plan_id WHERE f.plan_id=$fight AND f.open=1",
                    ("$fight", batch.Fight)
                ) ?? throw new NotFoundException();
                var state = LoadIncident(tx, new IncidentId(incident));
                if (state.Incident.Closed)
                {
                    throw new ConflictException();
                }
                AdmitRoundsSafeguards(tx, state);
                BindOwnerMethod(tx, state, new MethodId("batch-" + plan.Id.Value), "", plan);
                return LoadIn(tx, plan.Id);
            },
            cancellationToken
        );
    }

    private static bool IsValidPlan(PlanSpec plan)
    {
        try
        {
            plan.Validate();
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>
    /// Retains exact commands and outcomes for postmortem and duplicate prevention, removing settled execution
    /// from the bounded active set.
    /// </summary>
    public void RetireCombatBatch(PlanId id, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var p = LoadIn(tx, id);
                if (p.Progress.Count != 1 || p.Progress[0].Action.Kind != ActionKind.CombatBatch)
                {
                    throw new ConflictException();
                }
                var v = p.Progress[0].View;
                if (v.Unresolved || v.Stage is not (Stage.Completed or Stage.Unsuccessful))
                {
                    return;
                }
                _ = tx.Execute("UPDATE plans SET retired=1 WHERE id=$id", ("$id", id));
            },
            cancellationToken
        );

    /// <summary>
    /// Transfers execution responsibility to a later recorded decision or completed restoration, while historical
    /// receipts stay untouched.
    /// </summary>
    internal static void SupersedeCombatBatches(StoreTransaction tx, PlanId fight, PlanId next, bool restored)
    {
        if (next != fight)
        {
            var successor = LoadIn(tx, next);
            if (successor.Progress.Count == 0)
            {
                throw new ConflictException();
            }
            if (successor.Progress[0].Action.CombatBatch is not { } b || b.Fight != fight || successor.Progress[0].View.Attempt.Value == 0)
            {
                throw new ConflictException();
            }
        }
        var ids = tx.Query(
            "SELECT p.id FROM plans p JOIN actions a ON a.plan_id=p.id WHERE p.retired=0 AND a.kind='combat_batch' AND a.fight=$fight AND p.id<>$next",
            [("$fight", fight), ("$next", next)],
            r => new PlanId(r.GetString(0))
        );
        var kept = LoadCombatRestorationIn(tx);
        foreach (var id in ids)
        {
            var p = LoadIn(tx, id);
            if (p.Progress.Count != 1)
            {
                continue;
            }
            var progress = p.Progress[0];
            if (progress.View.Attempt.Value == 0)
            {
                _ = AdvanceIn(tx, id, progress.Action.Id, new Transition(TransitionKind.Cancel));
                _ = tx.Execute("UPDATE plans SET retired=1,superseded_by=$next WHERE id=$id", ("$next", next), ("$id", id));
                continue;
            }
            var transferred = true;
            if (!restored)
            {
                foreach (var o in progress.Action.CombatBatch!.Orders)
                {
                    switch (o.Kind)
                    {
                        case "door" when kept is null || kept.Owner != fight || !kept.Doors.Any(d => d.Cell == o.Cell):
                        case "animal_area" when kept is null || kept.Owner != fight || !kept.Animals.Any(a => a.Pawn == o.Pawn):
                            transferred = false;
                            break;
                    }
                }
            }
            if (transferred)
            {
                _ = tx.Execute("UPDATE plans SET retired=1,superseded_by=$next WHERE id=$id", ("$next", next), ("$id", id));
            }
        }
    }

    // ---- restoration -------------------------------------------------------------------------------------

    internal static CombatRestoration? LoadCombatRestorationIn(StoreTransaction tx)
    {
        if (tx.ScalarText("SELECT payload FROM combat_restoration WHERE singleton=1") is not { } raw)
        {
            return null;
        }
        var r = StoreJson.Deserialize<CombatRestoration>(raw);
        r.Validate();
        return r;
    }

    public CombatRestoration? LoadCombatRestoration(CancellationToken cancellationToken) => Run(LoadCombatRestorationIn, cancellationToken);

    public void SaveCombatRestoration(CombatRestoration r, CancellationToken cancellationToken)
    {
        r.Validate();
        Run(
            tx =>
            {
                if (LoadCombatRestorationIn(tx) is { } old)
                {
                    if (old.Owner != r.Owner || old.World != r.World || old.Doors.Any(d => !r.Doors.Contains(d)) || old.Animals.Any(a => !r.Animals.Contains(a)))
                    {
                        throw new ConflictException();
                    }
                    // The superset replaces the kept row.
                    _ = tx.Execute("DELETE FROM combat_restoration");
                }
                PutSingleton(tx, "combat_restoration", r, MaxCombatRestorationBytes);
            },
            cancellationToken
        );
    }

    /// <summary>
    /// The existing combat wire for doors and temporary area deletion, followed by native husbandry to restore each
    /// original area.
    /// </summary>
    public static IReadOnlyList<DomainAction> CombatRestorationActions(CombatRestoration r, PlanId id)
    {
        r.Validate();
        var commands = new List<CombatCommand>();
        foreach (var d in r.Doors)
        {
            commands.Add(new CombatCommand { Kind = "door", Cell = d.Cell, Door = d.HoldOpen ? "hold_open" : "close" });
            commands.Add(new CombatCommand { Kind = "door", Cell = d.Cell, Door = d.Forbidden ? "forbid" : "allow" });
        }
        foreach (var a in r.Animals)
        {
            commands.Add(new CombatCommand { Kind = "animal_clear", Pawn = a.Pawn });
        }
        if (commands.Count == 0)
        {
            throw new ConflictException();
        }
        var actions = new List<DomainAction>
        {
            DomainAction.NewCombatBatchAction(new ActionId(id.Value + "-settings"), new CombatBatch(r.Owner, id.Value, commands)),
        };
        for (var i = 0; i < r.Animals.Count; i++)
        {
            var animal = r.Animals[i];
            actions.Add(DomainAction.NewHusbandryAction(new ActionId($"{id.Value}-animal-{i}"), new Husbandry(animal.Pawn, HusbandryMethod.AllowedArea, animal.Area)));
        }
        return actions;
    }

    internal static bool CombatRestorationPlan(StoreTransaction tx, PlanSpec plan)
    {
        try
        {
            if (LoadCombatRestorationIn(tx) is not { } r)
            {
                return false;
            }
            var expected = CombatRestorationActions(r, plan.Id);
            return expected.Count == plan.Actions.Count && expected.Select((a, i) => plan.Actions[i] == a).All(same => same);
        }
        catch (Exception e) when (e is DomainException or StoreCorruptException)
        {
            return false;
        }
    }

    public void CommitCombatRestoration(IncidentId id, PlanSpec plan, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                if (!IsValidPlan(plan) || !CombatRestorationPlan(tx, plan))
                {
                    throw new ConflictException();
                }
                if (tx.Scalar("SELECT count(*) FROM plans WHERE id=$id", ("$id", plan.Id)) != 0)
                {
                    if (!CombatRestorationPlan(tx, LoadIn(tx, plan.Id).Spec))
                    {
                        throw new ConflictException();
                    }
                    return;
                }
                var state = LoadIncident(tx, id);
                if (state.Incident.Closed)
                {
                    throw new ConflictException();
                }
                // A fresh restoration decision transfers the same saved originals. Retire earlier cleanup
                // attempts without changing their historical uncertainty.
                var priorIds = new List<PlanId>();
                foreach (var method in state.Methods)
                {
                    var prior = LoadIn(tx, method.Plan);
                    if (!CombatRestorationPlan(tx, prior.Spec))
                    {
                        continue;
                    }
                    priorIds.Add(prior.Spec.Id);
                    _ = tx.Execute("UPDATE plans SET retired=1 WHERE id=$id", ("$id", prior.Spec.Id));
                }
                BindOwnerMethod(tx, state, new MethodId("restore-" + plan.Id.Value), "", plan);
                foreach (var prior in priorIds)
                {
                    _ = tx.Execute("UPDATE plans SET superseded_by=$next WHERE id=$id", ("$next", plan.Id), ("$id", prior));
                }
            },
            cancellationToken
        );

    /// <summary>Drops saved intent after settings complete or native cleanup proves the paired animal target impossible. Retirement is atomic.</summary>
    public void FinishCombatRestoration(PlanId owner, PlanId plan, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var p = LoadIn(tx, plan);
                if (!CombatRestorationPlan(tx, p.Spec))
                {
                    throw new ConflictException();
                }
                // Only a completed native clear can discharge an impossible animal's original setting. A
                // missing or uncertain reply never proves cleanup.
                var cleared = new HashSet<PawnId>();
                var batch = p.Spec.Actions[0].CombatBatch!;
                var first = p.Progress[0].View;
                if (first.Stage != Stage.Completed || first.Unresolved || !first.Combat.TryGetValue(out var results))
                {
                    throw new ConflictException();
                }
                for (var i = 0; i < results.Orders.Count; i++)
                {
                    var result = results.Orders[i];
                    var command = batch.Orders[i];
                    if (result.Applied)
                    {
                        continue;
                    }
                    if (command.Kind == "animal_clear" && result.Refusal is "not_found" or "not_ours")
                    {
                        _ = cleared.Add(command.Pawn);
                    }
                    else if (command.Kind != "door" || result.Refusal != "not_a_door")
                    {
                        throw new ConflictException();
                    }
                }
                for (var i = 0; i < p.Progress.Count; i++)
                {
                    if (p.Spec.Actions[i].Husbandry is { } animal && cleared.Contains(animal.Animal))
                    {
                        continue;
                    }
                    var v = p.Progress[i].View;
                    if (v.Stage != Stage.Completed || v.Unresolved)
                    {
                        throw new ConflictException();
                    }
                }
                if (LoadCombatRestorationIn(tx) is not { } kept || kept.Owner != owner)
                {
                    throw new ConflictException();
                }
                SupersedeCombatBatches(tx, owner, plan, restored: true);
                _ = tx.Execute("DELETE FROM combat_restoration");
                _ = tx.Execute("UPDATE plans SET retired=1 WHERE id=$id", ("$id", plan));
            },
            cancellationToken
        );
}

internal static partial class StoreSchema
{
    internal static readonly SchemaPart CombatFightsPart = new(
        """
        CREATE TABLE combat_fights(plan_id TEXT PRIMARY KEY REFERENCES plans(id), open INTEGER NOT NULL CHECK(open IN (0,1)), memory TEXT NOT NULL, roster TEXT NOT NULL) STRICT;
        CREATE TABLE combat_evidence(plan_id TEXT NOT NULL REFERENCES combat_fights(plan_id), sequence INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), payload TEXT NOT NULL, PRIMARY KEY(plan_id,sequence)) STRICT;
        """,
        ["SELECT plan_id,open,memory,roster FROM combat_fights LIMIT 0", "SELECT plan_id,sequence,tick,payload FROM combat_evidence LIMIT 0"]
    );
}
