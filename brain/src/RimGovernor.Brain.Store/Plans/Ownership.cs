using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Store;

/// <summary>One building method for admission (Go <c>BuildingMethodRequest</c>).</summary>
public sealed record BuildingMethodRequest
{
    /// <summary>The standard the method binds to, as loaded: its revision scopes the commit.</summary>
    public required StandardState Owner { get; init; }

    public MethodId Method { get; init; }

    public required PlanSpec Plan { get; init; }

    public GenerationSnapshot Current { get; init; }

    public Tick Tick { get; init; }

    public Fact<Bounds> Bounds { get; init; }

    public StockObservation Stock { get; init; } = new();

    public IReadOnlyList<Preview> Previews { get; init; } = [];

    public Purpose Purpose { get; init; }

    /// <summary>The planner's short why, stored with the method.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>Reports an admission; <see cref="Refused"/> lists a refusal's reasons.</summary>
public sealed record BuildingMethodDecision(bool Admitted, StandardState Standard, IReadOnlyList<Refusal> Refused);

/// <summary>
/// One zone this colony created, by native zone identity: the completed zone_create's kind, crop, cells and
/// stockpile role key. Role is empty for a legacy role-less claim.
/// </summary>
public sealed record OwnedZone(string Id, ZoneKind Kind, string Crop, EquatableArray<Cell> Cells, string Role, StandardId Owner, StockpileFilter? Filter, StockpilePriority? Priority);

/// <summary>The last completed autopilot stockpile_patch on one storage target, as of <see cref="Tick"/>.</summary>
public sealed record AppliedStockpile(string Target, StorageTargetKind Kind, StockpileFilter Filter, StockpilePriority Priority, string Role, Tick Tick);

public sealed partial class Store
{
    private const int MaxConstructionClaims = 256;
    private const int MaxStockpilePatchPlans = 1024;

    /// <summary>
    /// Applies the same pure resource/geometry policy as Hands inside the transaction that stores every method
    /// reservation and its plan. Candidates can depend on future work in this method: admission reserves the whole
    /// project, while the persisted dependency graph gates preparation and dispatch.
    /// </summary>
    public BuildingMethodDecision AdmitBuildingMethod(BuildingMethodRequest r, CancellationToken cancellationToken)
    {
        r.Plan.Validate();
        DomainException.Require(
            r.Current.IsValid && r.Current.Plan == r.Plan.Id && r.Current.Revision == r.Plan.Revision && r.Tick.Value >= 0,
            "invalid method admission scope"
        );
        var actions = r.Plan.Actions;
        DomainException.Require(actions.Count != 0, "method actions required");
        // A building intent is validated natively when it is applied: admission neither prices nor sites it, so a
        // planner's siting preview of one is not admission evidence. Zones keep their footprint check.
        var previews = new Dictionary<ActionId, Preview>();
        foreach (var p in r.Previews)
        {
            if (p.Action.Kind == ActionKind.Building)
            {
                continue;
            }
            DomainException.Require(previews.TryAdd(p.Action.Id, p), "duplicate method preview");
        }
        return Run(
            tx =>
            {
                Floors.Guard(r.Current, r.Tick);
                var owner = LoadStandard(tx, r.Owner.Standard.Id);
                if (owner.Revision != r.Owner.Revision)
                {
                    throw new StaleOwnerException($"{((IMethodOwner)owner).OwnerLabel} revision {owner.Revision}, method scoped to {r.Owner.Revision}");
                }
                var old = owner.Standard.Snapshot;
                DomainException.Require(old.Colony == r.Current.Colony && old.Map == r.Current.Map, "method observation differs from reviewed standard");
                var candidates = new List<Candidate>();
                foreach (var a in actions)
                {
                    switch (a.Kind)
                    {
                        case ActionKind.Building:
                            break;
                        case ActionKind.ZoneCreate:
                            DomainException.Require(previews.TryGetValue(a.Id, out var preview) && preview.Action == a, "method preview does not match action");
                            candidates.Add(
                                new Candidate
                                {
                                    Action = a,
                                    Progress = new Progress(r.Plan, a.Id),
                                    Priority = 4 - owner.Standard.Priority,
                                    Purpose = r.Purpose,
                                    Preview = preview,
                                }
                            );
                            break;
                        case ActionKind.WallRemoval
                        or ActionKind.Excavation
                        or ActionKind.Deconstruction
                        or ActionKind.UninstallBuilding
                        or ActionKind.ClaimBuilding
                        or ActionKind.RemoveRoof
                        or ActionKind.BuildRoof:
                            // Guarded demolition, staged excavation and a shell ring's ruin clearance and claims, and the
                            // shelter bunks' packed bedrolls, carry no cost/footprint preview; they are admitted as part
                            // of the bundle's dependency graph, excluded from the admission candidates, and re-checked at
                            // dispatch, not here.
                            DomainException.Require(!previews.ContainsKey(a.Id), "guarded action must not carry a preview");
                            break;
                        default:
                            throw new DomainException("unsupported method action family");
                    }
                }
                DomainException.Require(candidates.Count == previews.Count, "complete bounded method previews required");
                var decision = new Decision();
                if (candidates.Count != 0)
                {
                    var input = NewInput(
                        new Request
                        {
                            Current = r.Current,
                            CurrentTick = r.Tick,
                            Bounds = r.Bounds,
                            Candidates = EquatableArray.From(candidates),
                            Stock = r.Stock,
                            Held = BuildingMethodHolds(tx, r.Current),
                        }
                    );
                    decision = Admit(input);
                    if (decision.Refused.Count != 0 || decision.Admitted.Count != candidates.Count)
                    {
                        return new BuildingMethodDecision(false, owner, decision.Refused);
                    }
                }
                var committed = CommitMethodIn(tx, owner.Standard.Id, owner.Revision, r.Method, r.Reason, r.Plan);
                foreach (var h in decision.Admitted)
                {
                    var admission = new Admission(h.Snapshot, r.Tick, h.Footprint);
                    ValidateAdmission(h.Action, h.Progress, admission);
                    _ = tx.Execute("INSERT INTO admissions(action_id,payload) VALUES($id,$payload)", ("$id", h.Action.Id), ("$payload", StoreJson.Serialize(admission)));
                }
                return new BuildingMethodDecision(true, committed, decision.Refused);
            },
            cancellationToken
        );
    }

    public Task<BuildingMethodDecision> AdmitBuildingMethodAsync(BuildingMethodRequest r, CancellationToken cancellationToken) => Task.Run(() => AdmitBuildingMethod(r, cancellationToken), cancellationToken);

    /// <summary>The authoritative reservations under the admission transaction. Never accept a caller's potentially stale inventory of competing projects.</summary>
    public IReadOnlyList<Reservation> BuildingReservations(GenerationSnapshot current, CancellationToken cancellationToken)
    {
        current.Validate();
        return Run(tx => (IReadOnlyList<Reservation>)[.. BuildingMethodHolds(tx, current)], cancellationToken);
    }

    public Task<IReadOnlyList<Reservation>> BuildingReservationsAsync(GenerationSnapshot current, CancellationToken cancellationToken) =>
        Task.Run(() => BuildingReservations(current, cancellationToken), cancellationToken);

    /// <summary>The admission transaction repeats this read after spatial proposal generation.</summary>
    private static EquatableArray<Reservation> BuildingMethodHolds(StoreTransaction tx, GenerationSnapshot current)
    {
        var held = new List<Reservation>();
        foreach (var p in LoadPlansIn(tx))
        {
            var records = p.Admissions.ToDictionary(a => a.Action, a => a.Admission);
            foreach (var progress in p.Progress)
            {
                if (progress.Action.Kind != ActionKind.ZoneCreate)
                {
                    continue;
                }
                var v = progress.View;
                var known = v.Effect.TryGetValue(out var effect);
                if (v.Stage == Stage.Cancelled && !v.Unresolved && (v.Attempt.Value == 0 || (known && effect == Effect.Absent)))
                {
                    continue;
                }
                if (!records.TryGetValue(v.Action, out var admission))
                {
                    if (v.Stage == Stage.Pending && v.Attempt.Value == 0 && !v.Unresolved)
                    {
                        continue;
                    }
                    throw new DomainException("existing zone work lacks footprint evidence");
                }
                var scope = admission.Snapshot;
                if (scope.Colony != current.Colony || scope.Map != current.Map)
                {
                    continue;
                }
                held.Add(new Reservation(progress.Action, progress, scope, default, admission.Footprint));
            }
        }
        return EquatableArray.From(held);
    }

    private const string ObservedOrReceipted = "json_extract(t.payload,'$.Kind') IN ('observe','receipt')";

    private const string ReceiptAccepted = "json_extract(t.payload,'$.Kind')='receipt' AND json_extract(t.payload,'$.Receipt')='accepted'";

    private static string StandardPlanLinks(string actionKind, string extra, string transition, string tail) =>
        $"""
            SELECT m.plan_id,m.owner_id FROM plan_methods m JOIN standards g ON g.id=m.owner_id AND m.kind='standard'
             WHERE json_extract(g.payload,'$.Snapshot.Colony')=$colony
             AND json_extract(g.payload,'$.Snapshot.Load')=$load AND json_extract(g.payload,'$.Snapshot.Map')=$map
             AND EXISTS(SELECT 1 FROM actions a JOIN transitions t ON t.action_id=a.id WHERE a.plan_id=m.plan_id AND a.kind='{actionKind}'{extra} AND {transition})
             ORDER BY m.plan_id {tail}
            """;

    private static List<(PlanId Plan, string Owner)> OwnedPlanLinks(StoreTransaction tx, string sql, GenerationSnapshot current) =>
        tx.Query(sql, [("$colony", current.Colony), ("$load", current.Load), ("$map", current.Map)], r => (Plan: new PlanId(r.GetString(0)), Owner: r.GetString(1)));

    private static bool InScope(OwnerSummary owner, GenerationSnapshot current) => owner.Snapshot.Colony == current.Colony && owner.Snapshot.Load == current.Load && owner.Snapshot.Map == current.Map;

    private static bool CompletedInScope(ProgressView v, GenerationSnapshot current, Tick tick) =>
        v.Effect.TryGetValue(out var effect)
        && effect == Effect.Completed
        && v.Stage == Stage.Completed
        && v.Tick <= tick
        && v.Snapshot.Colony == current.Colony
        && v.Snapshot.Load == current.Load
        && v.Snapshot.Map == current.Map;

    /// <summary>
    /// Every applied building intent of an autopilot goal claims what its key builds, through catalog retirement and
    /// manual play. Explicit goal cancellation revokes upkeep ownership; world changes and future receipts confer none.
    /// Only a plan holding a building action with an accepted receipt can carry an applied intent, so the link query
    /// excludes the rest before the full plan load. Unknown when the claims outgrow their bound.
    /// </summary>
    internal static Fact<EquatableArray<ConstructionClaim>> ConstructionClaimsIn(StoreTransaction tx, GenerationSnapshot current, Tick tick)
    {
        var links = OwnedPlanLinks(
            tx,
            StandardPlanLinks("building", " AND COALESCE(json_extract(a.body,'$.Payload.ConstructionTarget'),'')=''", ReceiptAccepted, "LIMIT 257"),
            current
        );
        if (links.Count > MaxConstructionClaims)
        {
            return default;
        }
        var result = new List<ConstructionClaim>();
        var owners = new Dictionary<string, OwnerSummary>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (!owners.TryGetValue(link.Owner, out var g))
            {
                g = SummarizeOwner(LoadStandard(tx, new StandardId(link.Owner)));
                owners[link.Owner] = g;
            }
            if (!InScope(g, current))
            {
                continue;
            }
            foreach (var progress in LoadIn(tx, link.Plan).Progress)
            {
                if (progress.Action.Building is not { } building || progress.Action.ConstructionTarget.Length != 0 || !CompletedInScope(progress.View, current, tick))
                {
                    continue;
                }
                result.Add(new ConstructionClaim { Plan = link.Plan, Action = progress.View.Action, Concern = g.Kind, Building = building });
                if (result.Count > MaxConstructionClaims)
                {
                    return default;
                }
            }
        }
        return Fact.Known(EquatableArray.From(result));
    }

    public Fact<EquatableArray<ConstructionClaim>> ConstructionClaims(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken)
    {
        if (!current.IsValid || tick.Value < 0)
        {
            throw new ConflictException();
        }
        return Run(tx => ConstructionClaimsIn(tx, current, tick), cancellationToken);
    }

    public Task<Fact<EquatableArray<ConstructionClaim>>> ConstructionClaimsAsync(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken) =>
        Task.Run(() => ConstructionClaims(current, tick, cancellationToken), cancellationToken);

    /// <summary>
    /// Every completed autopilot zone_create of the current world scope by the native zone identity its receipt
    /// returned; the layout tidy reads a zone's kind and crop from these when it has one.
    /// </summary>
    internal static Fact<EquatableArray<OwnedZone>> ZoneClaimsIn(StoreTransaction tx, GenerationSnapshot current, Tick tick)
    {
        var links = OwnedPlanLinks(tx, StandardPlanLinks("zone_create", "", ObservedOrReceipted, ""), current);
        var result = new List<OwnedZone>();
        var owners = new Dictionary<string, OwnerSummary>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (!owners.TryGetValue(link.Owner, out var g))
            {
                g = SummarizeOwner(LoadStandard(tx, new StandardId(link.Owner)));
                owners[link.Owner] = g;
            }
            if (!InScope(g, current))
            {
                continue;
            }
            var owner = new StandardId(link.Owner);
            foreach (var progress in LoadIn(tx, link.Plan).Progress)
            {
                var v = progress.View;
                if (progress.Action.ZoneCreate is not { } zone || !CompletedInScope(v, current, tick))
                {
                    continue;
                }
                if (v.Stockpiles.TryGetValue(out var placements))
                {
                    result.AddRange(placements.Rows.Select(created => new OwnedZone(created.Id, zone.Kind, "", created.Cells, zone.Role, owner, zone.Filter, zone.Priority)));
                }
                else if (v.Zone.TryGetValue(out var id))
                {
                    result.Add(new OwnedZone(id, zone.Kind, zone.Crop, zone.Cells, zone.Role, owner, zone.Filter, zone.Priority));
                }
            }
        }
        return Fact.Known(EquatableArray.From(result));
    }

    /// <summary>Every zone this colony created in the current world scope.</summary>
    public Fact<EquatableArray<OwnedZone>> ZoneClaims(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken)
    {
        if (!current.IsValid || tick.Value < 0)
        {
            throw new ConflictException();
        }
        return Run(tx => ZoneClaimsIn(tx, current, tick), cancellationToken);
    }

    public Task<Fact<EquatableArray<OwnedZone>>> ZoneClaimsAsync(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken) =>
        Task.Run(() => ZoneClaims(current, tick, cancellationToken), cancellationToken);

    /// <summary>
    /// The latest completed autopilot stockpile_patch of the current world scope at or before <paramref name="tick"/>,
    /// per target id, zones and storage buildings alike.
    /// </summary>
    internal static Dictionary<string, AppliedStockpile> StockpilePatchesIn(StoreTransaction tx, GenerationSnapshot current, Tick tick)
    {
        var result = new Dictionary<string, AppliedStockpile>(StringComparer.Ordinal);
        foreach (var (plan, _) in OwnedPlanLinks(tx, StandardPlanLinks("stockpile_patch", "", ObservedOrReceipted, "LIMIT " + (MaxStockpilePatchPlans + 1)), current))
        {
            foreach (var progress in LoadIn(tx, plan).Progress)
            {
                var v = progress.View;
                if (progress.Action.StockpilePatch is not { } patch || !CompletedInScope(v, current, tick))
                {
                    continue;
                }
                if (result.TryGetValue(patch.Target, out var prior) && prior.Tick > v.Tick)
                {
                    continue;
                }
                result[patch.Target] = new AppliedStockpile(patch.Target, patch.TargetKind, patch.Filter, patch.Priority, patch.Role, v.Tick);
            }
        }
        return result;
    }

    public IReadOnlyDictionary<string, AppliedStockpile> StockpilePatches(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken)
    {
        if (!current.IsValid || tick.Value < 0)
        {
            throw new ConflictException();
        }
        return Run(tx => (IReadOnlyDictionary<string, AppliedStockpile>)StockpilePatchesIn(tx, current, tick), cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, AppliedStockpile>> StockpilePatchesAsync(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken) =>
        Task.Run(() => StockpilePatches(current, tick, cancellationToken), cancellationToken);

    /// <summary>
    /// The anchor cells of building intents not yet applied in <paramref name="current"/>'s world on plans other than
    /// <paramref name="own"/>: nothing on the map shows them, so siting keeps off them. The store knows no definition
    /// sizes; native validation refuses a placement over the rest of a multi-cell footprint. An applied, refused or
    /// cancelled intent holds nothing (the census shows what it placed).
    /// </summary>
    public IReadOnlyList<Cell> PendingBuildingAnchors(GenerationSnapshot current, IReadOnlyCollection<PlanId> own, CancellationToken cancellationToken)
    {
        current.Validate();
        return Run(
            tx =>
            {
                var plans = LoadPlansIn(tx);
                var methods = tx.Query("SELECT plan_id FROM plan_owner", [], r => new PlanId(r.GetString(0))).ToHashSet();
                var cells = new List<Cell>();
                foreach (var p in plans)
                {
                    if (own.Contains(p.Spec.Id))
                    {
                        continue;
                    }
                    foreach (var progress in p.Progress)
                    {
                        var v = progress.View;
                        if (progress.Action.Building is not { } building || (!v.Unresolved && v.Stage is Stage.Completed or Stage.Unsuccessful or Stage.Cancelled))
                        {
                            continue;
                        }
                        var started = v.Stage != Stage.Pending || v.Attempt.Value > 0 || v.Unresolved;
                        // Unstarted work holds cells only once admitted as a goal's method.
                        if (!started && !methods.Contains(p.Spec.Id))
                        {
                            continue;
                        }
                        var w = v.Snapshot;
                        if (started && (w.Colony != current.Colony || w.Load != current.Load || w.Map != current.Map))
                        {
                            continue;
                        }
                        cells.Add(building.Cell);
                    }
                }
                return (IReadOnlyList<Cell>)cells;
            },
            cancellationToken
        );
    }

    public Task<IReadOnlyList<Cell>> PendingBuildingAnchorsAsync(GenerationSnapshot current, IReadOnlyCollection<PlanId> own, CancellationToken cancellationToken) =>
        Task.Run(() => PendingBuildingAnchors(current, own, cancellationToken), cancellationToken);
}
