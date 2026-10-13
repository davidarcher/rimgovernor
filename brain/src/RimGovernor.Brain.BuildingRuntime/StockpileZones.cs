using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

// The stockpile helper: the mechanics every stockpile-owning concern shares. A concern reads its owned zones
// (OwnedStockpileZones), decides its edits, and applies them through StockpileZones: Create admits previewed
// zones, EditTargetPresent and StockpileEditAction retarget or delete standing ones, and OpenEditPlan gates a
// concern on its own plan still in flight.

/// <summary>Refreshes one zone's presence and CAS token for each edit.</summary>
public interface IRoundsStockpileSource
{
    Task<(ZoneDeleteTarget Target, BridgeResult Result)> ReadZoneDeleteTargetAsync(C.Identity identity, string zone, CancellationToken ct);
}

/// <summary>Reads a shelf's storage settings CAS token.</summary>
public interface IShelfTargetSource
{
    Task<(StorageBuildingTarget Target, BridgeResult Result)> ReadStorageBuildingTargetAsync(C.Identity identity, string building, CancellationToken ct);
}

/// <summary>Previews a zone creation natively.</summary>
public interface IZonePreviewSource
{
    Task<(OperationsPb.ZonePreviewReply Reply, BridgeResult Result)> PreviewZoneAsync(C.Identity identity, ZoneCreate zone, CancellationToken ct);
}

/// <summary>Applies zone edits for any owning concern.</summary>
internal sealed class StockpileZones(Rounder reviewer, IRoundsStockpileSource native)
{
    /// <summary>
    /// Whether the edit's target (the stockpile zone, or the shelf for a shelf patch) is still there; false when
    /// the source cannot read it.
    /// </summary>
    public async Task<bool> EditTargetPresentAsync(C.Identity identity, Pol.StockpileEdit e, CancellationToken ct)
    {
        if (e.Kind == Pol.StockpileEditKind.ShelfPatch)
        {
            if (native is not IShelfTargetSource source)
            {
                return false;
            }
            var (shelf, _) = await source.ReadStorageBuildingTargetAsync(identity, e.Zone, ct).ConfigureAwait(false);
            return shelf.Present;
        }
        var (target, _) = await native.ReadZoneDeleteTargetAsync(identity, e.Zone, ct).ConfigureAwait(false);
        return target.Present && target.Type == "stockpile";
    }

    /// <summary>
    /// Admits the missing zones as one method of owner: each zone is previewed natively on the review's zone map
    /// token, a refused one is dropped, and the accepted ones are admitted together with their footprints
    /// reserved, like every routine zone. Zoning is instant and needs no worker, so a fresh colony's zones land
    /// in one step.
    /// </summary>
    public async Task<RoundsStockpileResult> CreateAsync(ControlState state, Sto.StandardState owner, Observation.ColonyProjection projection, DateTimeOffset started, IReadOnlyList<Pol.StockpileEdit> edits, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        if (native is not IZonePreviewSource preview)
        {
            return new RoundsStockpileResult { Verdict = FieldUnavailable("zone_preview") };
        }
        var tick = projection.Identity.Tick;
        var roles = edits.Select(e => e.Role);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{owner.Standard.Id}/{owner.Standard.Episode}/create/{string.Join(",", roles)}/{tick.Value}")));
        var id = PlanIds.MintPlanId();
        var method = new MethodId("stockpile-create-" + Convert.ToHexString(digest, 0, 8).ToLowerInvariant());
        try
        {
            p.Journal.LoadMethod(owner.Standard.Id, owner.Standard.Episode, method, call);
            return new RoundsStockpileResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "stockpile_create") };
        }
        catch (Sto.NotFoundException)
        {
        }
        var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
        var actions = new List<DomainAction>();
        var previews = new List<Pol.Preview>();
        var admitted = new List<Pol.StockpileEdit>();
        foreach (var e in edits)
        {
            var createFilter = e.Filter ?? throw new InvalidOperationException("a stockpile create edit names its filter");
            ZoneCreate value = e.CellList.Count > 0
                ? ZoneCreate.NewFilteredStockpileCellsZone(createFilter, e.Priority, e.CellList)
                : ZoneCreate.NewFilteredStockpileZone(createFilter, e.Priority, new GroundRect(new Cell(e.Rectangle.X, e.Rectangle.Z), e.Rectangle.Width, e.Rectangle.Height));
            value = value.WithRole(e.Role);
            OperationsPb.ZonePreviewReply reply;
            try
            {
                (reply, _) = await preview.PreviewZoneAsync(Boundary.Identity(snapshot), value, call).ConfigureAwait(false);
            }
            catch (NativeFailureException refused)
            {
                Tel.Decide(StockpileEditDecision("refused", refused.Value.Code.GetValueOrDefault().ToString(), e.Role, new Dictionary<string, object?> { ["kind"] = "create", ["detail"] = (refused.Value.Detail ?? "") }));
                continue;
            }
            var v = reply.Outcome?.Evaluated;
            if (v is null || !v.Accepted.GetValueOrDefault())
            {
                Tel.Decide(StockpileEditDecision("refused", "preview_not_accepted", e.Role, new Dictionary<string, object?> { ["kind"] = "create", ["detail"] = v?.Reason ?? "" }));
                continue;
            }
            try
            {
                Boundary.Context(v.Context, snapshot);
            }
            catch (Exception err) when (err is not OperationCanceledException)
            {
                throw new ControlException("create: err != nil || domain.Tick(v.Context.GetTick()) < tick", err);
            }
            if (new Tick((v.Context?.Tick).GetValueOrDefault()) < tick)
            {
                throw new ControlException("create: err != nil || domain.Tick(v.Context.GetTick()) < tick");
            }
            var action = DomainAction.NewZoneCreateAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), value);
            actions.Add(action);
            previews.Add(new Pol.Preview { Action = action, Snapshot = snapshot, Tick = tick, CanPlace = Known(true), SafeToPlace = Known(true), MadeFromStuff = Known(false), WatchCellsAccessible = Known(true), Footprint = Known(value.Cells), Costs = Known<EquatableArray<Pol.Amount>>([]) });
            admitted.Add(e);
        }
        if (actions.Count == 0)
        {
            return new RoundsStockpileResult { Verdict = NoSpace("stockpile_zone") };
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var now = reviewer.Clock.Now();
        if (p.Session.State() != state || now < started || now - started > reviewer.MaxAge)
        {
            throw new ControlException("create: p.session.State() != state || now.Before(started) || now.Sub(started) > z.reviewer.maxAge");
        }
        var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new Sto.BuildingMethodRequest
        {
            Owner = owner,
            Method = method,
            Plan = plan,
            Current = snapshot,
            Tick = tick,
            Bounds = Known(projection.Bounds),
            Stock = new Pol.StockObservation { Snapshot = snapshot, Tick = tick },
            Previews = previews,
            Purpose = Pol.Purpose.Rounds,
        }, call).ConfigureAwait(false);
        if (!decision.Admitted)
        {
            return new RoundsStockpileResult { Verdict = AdmissionLog.AdmissionRefused(decision) };
        }
        foreach (var e in admitted)
        {
            Tel.Decide(StockpileEditDecision("admitted", "", e.Role, new Dictionary<string, object?> { ["kind"] = Wire.Name(e.Kind), ["role"] = e.Role, ["cells"] = e.Cells().Count, ["plan"] = id.ToString(), ["detail"] = e.Explanation }));
        }
        return new RoundsStockpileResult { Verdict = BuildingReasonAdmitted, Plan = id, Edits = actions.Count };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The owned-zone view: each journal zone claim of kind stockpile that the projection's planning cells still
    /// name, with the zone's cells (a cell whose storage-empty flag is false holds things) and its settings
    /// superseded by the latest patch of each.
    /// </summary>
    internal static List<Pol.StockpileZone> OwnedStockpileZones(Observation.ColonyProjection projection, IReadOnlyList<Sto.OwnedZone> owned, IReadOnlyDictionary<string, Sto.AppliedStockpile>? patches)
    {
        var byZone = new Dictionary<string, (List<Cell> All, List<Cell> Stored)>();
        foreach (var cell in projection.Cells)
        {
            if (!cell.ZoneId.TryGetValue(out var id) || id == "")
            {
                continue;
            }
            if (!byZone.TryGetValue(id, out var entry))
            {
                entry = ([], []);
                byZone[id] = entry;
            }
            entry.All.Add(cell.Cell);
            if (cell.StorageEmpty.TryGetValue(out var empty) && !empty)
            {
                entry.Stored.Add(cell.Cell);
            }
        }
        var zones = new List<Pol.StockpileZone>();
        foreach (var z in owned)
        {
            if (z.Kind != Domain.ZoneKind.Stockpile || !byZone.TryGetValue(z.Id, out var entry) || z.Filter is not { } zoneFilter)
            {
                continue;
            }
            var zone = new Pol.StockpileZone { Id = z.Id, Role = z.Role, Cells = entry.All, Stored = entry.Stored, Filter = zoneFilter, Priority = z.Priority ?? default };
            if (patches is not null && patches.TryGetValue(z.Id, out var patch) && patch.Kind == StorageTargetKind.Zone)
            {
                zone = zone with { Filter = patch.Filter };
                zone = zone with { Priority = patch.Priority };
                if (patch.Role != "")
                {
                    zone = zone with { Role = patch.Role };
                }
            }
            zones.Add(zone);
        }
        return zones;
    }

    /// <summary>
    /// Reports a zone-edit plan (a create, patch, cell edit or delete batch), not a room shell: the plan's own
    /// actions decide, so any owner's zone method counts, whatever its method id.
    /// </summary>
    internal static bool IsStockpileEditPlan(PlanSpec plan)
    {
        foreach (var a in plan.Actions)
        {
            switch (a.Kind)
            {
                case ActionKind.ZoneCreate:
                case ActionKind.ZoneDelete:
                case ActionKind.ZoneCellEdit:
                case ActionKind.StockpilePatch:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reports whether one of the owner's methods holds a zone-edit plan still open. A room shell is building
    /// work that takes days; zoning is instant, so only an open zone-edit plan holds the next edits.
    /// </summary>
    internal static bool OpenEditPlan(StoreJournal journal, Sto.StandardState owner, CancellationToken ct)
    {
        foreach (var method in owner.Methods)
        {
            var plan = journal.LoadPlan(method.Plan, ct);
            if (IsStockpileEditPlan(plan.Spec) && StorePackage.PlanOpen(plan))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>One edit's action.</summary>
    internal static DomainAction StockpileEditAction(ActionId id, Pol.StockpileEdit e)
    {
        switch (e.Kind)
        {
            case Pol.StockpileEditKind.Grow:
                return DomainAction.NewZoneCellEditAction(id, new ZoneCellEdit(e.Zone, CellEditMode.Add, e.AddedCells));
            case Pol.StockpileEditKind.Shrink:
                return DomainAction.NewZoneCellEditAction(id, new ZoneCellEdit(e.Zone, CellEditMode.Remove, e.RemovedCells));
            case Pol.StockpileEditKind.Retarget:
                return DomainAction.NewStockpilePatchAction(id, new StockpilePatch(StorageTargetKind.Zone, e.Zone, e.Filter ?? throw new InvalidOperationException("a retarget edit names its filter"), e.Priority, e.Role));
            case Pol.StockpileEditKind.ShelfPatch:
                return DomainAction.NewStockpilePatchAction(id, new StockpilePatch(StorageTargetKind.Building, e.Zone, e.Filter ?? throw new InvalidOperationException("a shelf patch edit names its filter"), e.Priority, e.Role));
            case Pol.StockpileEditKind.Delete:
                return DomainAction.NewZoneDeleteAction(id, new ZoneDelete(e.Zone));
        }
        throw new ArgumentException($"unknown stockpile edit \"{Wire.Name(e.Kind)}\"");
    }
}
