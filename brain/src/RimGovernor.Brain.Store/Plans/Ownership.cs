using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>One building method for admission.</summary>
public sealed record BuildingMethodRequest
{
    /// <summary>
    /// The Need the method binds to, as loaded; it commits through claim admission (#3279).
    /// </summary>
    public required NeedOwner Owner { get; init; }

    public MethodId Method { get; init; }

    public required PlanSpec Plan { get; init; }

    public GenerationSnapshot Current { get; init; }

    public Tick Tick { get; init; }

    public required Bounds Bounds { get; init; }

    public StockObservation Stock { get; init; } = new();

    public IReadOnlyList<Preview> Previews { get; init; } = [];

    public Purpose Purpose { get; init; }

    /// <summary>The planner's short why, stored with the method.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Reports an admission; <see cref="Refused"/> lists a refusal's reasons. A Need-owned method that was not
/// admitted because its MethodId is open work already is <see cref="Continued"/>, and one whose claims overlap an
/// open Method's names the holder in <see cref="Conflict"/>; nothing was written for either.
/// </summary>
public sealed record BuildingMethodDecision(bool Admitted, NeedOwner Owner, IReadOnlyList<Refusal> Refused)
{
    public bool Continued { get; init; }

    public ClaimConflict? Conflict { get; init; }

    /// <summary>The claimed commit's admission refusal (<see cref="ClaimOutcome.Refused"/>): nothing was written.</summary>
    public AdmissionRefusal? Refusal { get; init; }
}

/// <summary>
/// One zone this colony created, by native zone identity: the completed zone_create's kind, crop, cells and
/// stockpile role key. Role is empty for a legacy role-less claim. <see cref="Owner"/> is the owner's filing key
/// (decision 20): the owning Need's NeedId.FilingKey.
/// </summary>
public sealed record OwnedZone(string Id, ZoneKind Kind, string Crop, EquatableArray<Cell> Cells, string Role, ConcernId Owner, StockpileFilter? Filter, StockpilePriority? Priority);

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
                var owner = LoadNeedIn(tx, r.Owner.Need, r.Owner.Snapshot);
                var old = owner.Snapshot;
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
                            // Guarded demolition, staged excavation, a shell ring's ruin clearance and claims, and packed
                            // furniture carry no cost/footprint preview; they are admitted as part
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
                    var input = AdmissionRules.NewInput(
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
                    decision = AdmissionRules.Admit(input);
                    if (decision.Refused.Count != 0 || decision.Admitted.Count != candidates.Count)
                    {
                        return new BuildingMethodDecision(false, (NeedOwner)owner, decision.Refused);
                    }
                }
                var need = (NeedOwner)owner;
                var claimed = CommitClaimedNeedMethodIn(tx, need.Need, need.Snapshot, r.Method, r.Reason, r.Plan);
                if (claimed.Outcome != ClaimOutcome.Admitted)
                {
                    return new BuildingMethodDecision(false, claimed.Owner, []) { Continued = claimed.Outcome == ClaimOutcome.Continued, Conflict = claimed.Conflict, Refusal = claimed.Refusal };
                }
                NeedOwner committed = claimed.Owner;
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

    /// <summary>The Need-owned plans of the current world holding an <paramref name="actionKind"/> action past <paramref name="transition"/>, with their NeedId.</summary>
    private static string NeedPlanLinks(string actionKind, string transition, string tail) =>
        $"""
            SELECT m.plan_id,m.need_id FROM need_methods m
             WHERE m.colony=$colony AND m.load_token=$load AND m.map_id=$map
             AND EXISTS(SELECT 1 FROM actions a JOIN transitions t ON t.action_id=a.id WHERE a.plan_id=m.plan_id AND a.kind='{actionKind}' AND {transition})
             ORDER BY m.plan_id {tail}
            """;

    private static List<(PlanId Plan, string Owner)> OwnedPlanLinks(StoreTransaction tx, string sql, GenerationSnapshot current) =>
        tx.Query(sql, [("$colony", current.Colony), ("$load", current.Load), ("$map", current.Map)], r => (Plan: new PlanId(r.GetString(0)), Owner: r.GetString(1)));

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
        // A Need-owned plan (#3263) claims what it builds, filed under the Need's key.
        var needs = tx.Query(
            $"""
                SELECT m.plan_id,m.need_id FROM need_methods m
                 WHERE m.colony=$colony AND m.load_token=$load AND m.map_id=$map
                 AND EXISTS(SELECT 1 FROM actions a JOIN transitions t ON t.action_id=a.id WHERE a.plan_id=m.plan_id AND a.kind='building' AND COALESCE(json_extract(a.body,'$.Payload.ConstructionTarget'),'')='' AND {ReceiptAccepted})
                 ORDER BY m.plan_id LIMIT 257
                """,
            [("$colony", current.Colony), ("$load", current.Load), ("$map", current.Map)],
            r => (Plan: new PlanId(r.GetString(0)), Need: new NeedId(r.GetString(1))));
        if (needs.Count > MaxConstructionClaims)
        {
            return default;
        }
        var result = new List<ConstructionClaim>();
        foreach (var link in needs)
        {
            foreach (var progress in LoadIn(tx, link.Plan).Progress)
            {
                if (progress.Action.Building is not { } building || progress.Action.ConstructionTarget.Length != 0 || !CompletedInScope(progress.View, current, tick))
                {
                    continue;
                }
                result.Add(new ConstructionClaim { Plan = link.Plan, Action = progress.View.Action, Concern = link.Need.FilingKey, Building = building });
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
            throw new ConflictException($"construction claims read needs a valid world and tick, got {current} at {tick.Value}");
        }
        return Run(tx => ConstructionClaimsIn(tx, current, tick), cancellationToken);
    }

    public Task<Fact<EquatableArray<ConstructionClaim>>> ConstructionClaimsAsync(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken) =>
        Task.Run(() => ConstructionClaims(current, tick, cancellationToken), cancellationToken);

    /// <summary>
    /// Every completed autopilot zone_create of the current world scope by the native zone identity its receipt
    /// returned; the layout tidy reads a zone's kind and crop from these when it has one. A Need-owned plan's zones
    /// are filed under the Need's key (decision 20).
    /// </summary>
    internal static Fact<EquatableArray<OwnedZone>> ZoneClaimsIn(StoreTransaction tx, GenerationSnapshot current, Tick tick)
    {
        var result = new List<OwnedZone>();
        void Add(PlanId plan, ConcernId owner)
        {
            foreach (var progress in LoadIn(tx, plan).Progress)
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
        foreach (var link in OwnedPlanLinks(tx, NeedPlanLinks("zone_create", ObservedOrReceipted, ""), current))
        {
            Add(link.Plan, new NeedId(link.Owner).FilingKey);
        }
        return Fact.Known(EquatableArray.From(result));
    }

    /// <summary>Every zone this colony created in the current world scope.</summary>
    public Fact<EquatableArray<OwnedZone>> ZoneClaims(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken)
    {
        if (!current.IsValid || tick.Value < 0)
        {
            throw new ConflictException($"zone claims read needs a valid world and tick, got {current} at {tick.Value}");
        }
        return Run(tx => ZoneClaimsIn(tx, current, tick), cancellationToken);
    }

    public Task<Fact<EquatableArray<OwnedZone>>> ZoneClaimsAsync(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken) =>
        Task.Run(() => ZoneClaims(current, tick, cancellationToken), cancellationToken);

    /// <summary>
    /// The latest completed autopilot stockpile_patch of the current world scope at or before <paramref name="tick"/>,
    /// per target id, zones and storage buildings alike, of Need-owned plans.
    /// </summary>
    internal static Dictionary<string, AppliedStockpile> StockpilePatchesIn(StoreTransaction tx, GenerationSnapshot current, Tick tick)
    {
        var result = new Dictionary<string, AppliedStockpile>(StringComparer.Ordinal);
        var limit = "LIMIT " + (MaxStockpilePatchPlans + 1);
        var links = OwnedPlanLinks(tx, NeedPlanLinks("stockpile_patch", ObservedOrReceipted, limit), current);
        foreach (var (plan, _) in links)
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
            throw new ConflictException($"stockpile patches read needs a valid world and tick, got {current} at {tick.Value}");
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
