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
    (Bridge.ZoneDeleteTarget Target, BridgeResult Result) ReadZoneDeleteTarget(CancellationToken ct, CommonPb.Identity identity, string zone);
}

/// <summary>Reads a shelf's storage settings CAS token.</summary>
public interface IShelfTargetSource
{
    (Bridge.StorageBuildingTarget Target, BridgeResult Result) ReadStorageBuildingTarget(CancellationToken ct, CommonPb.Identity identity, string building);
}

/// <summary>Previews a zone creation natively.</summary>
public interface IZonePreviewSource
{
    (OperationsPb.ZonePreviewReply Reply, BridgeResult Result) PreviewZone(CancellationToken ct, CommonPb.Identity identity, ZoneCreate zone);
}

/// <summary>Applies zone edits for any owning concern.</summary>
internal sealed class StockpileZones(Rounder reviewer, IRoundsStockpileSource native)
{
    /// <summary>
    /// Whether the edit's target (the stockpile zone, or the shelf for a shelf patch) is still there; false when
    /// the source cannot read it.
    /// </summary>
    public bool EditTargetPresent(CancellationToken ct, CommonPb.Identity identity, Pol.StockpileEdit e)
    {
        if (e.Kind == Pol.StockpileEditKind.ShelfPatch)
        {
            if (native is not IShelfTargetSource source)
            {
                return false;
            }
            var (shelf, _) = source.ReadStorageBuildingTarget(ct, identity, e.Zone);
            return shelf.Present;
        }
        var (target, _) = native.ReadZoneDeleteTarget(ct, identity, e.Zone);
        return target.Present && target.Type == "stockpile";
    }

    /// <summary>
    /// Admits the missing zones as one method of owner: each zone is previewed natively on the review's zone map
    /// token, a refused one is dropped, and the accepted ones are admitted together with their footprints
    /// reserved, like every routine zone. Zoning is instant and needs no worker, so a fresh colony's zones land
    /// in one step.
    /// </summary>
    public RoundsStockpileResult Create(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState owner, Observation.ColonyProjection projection, DateTimeOffset started, IReadOnlyList<Pol.StockpileEdit> edits)
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
            p.Journal.LoadMethod(call, owner.Standard.Id, owner.Standard.Episode, method);
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
            ZoneCreate value = e.CellList.Count > 0
                ? ZoneCreate.NewFilteredStockpileCellsZone(e.Filter, e.Priority, e.CellList)
                : ZoneCreate.NewFilteredStockpileZone(e.Filter, e.Priority, new GroundRect(new Cell(e.Rectangle.X, e.Rectangle.Z), e.Rectangle.Width, e.Rectangle.Height));
            value = value.WithRole(e.Role);
            OperationsPb.ZonePreviewReply reply;
            try
            {
                (reply, _) = preview.PreviewZone(call, BoundaryApi.Identity(snapshot), value);
            }
            catch (Bridge.NativeFailure refused)
            {
                TelemetryPackage.Decide(call, StockpileEditDecision("refused", refused.Value.Code.ToString(), e.Role, new Dictionary<string, object?> { ["kind"] = "create", ["detail"] = refused.Value.Detail }));
                continue;
            }
            var v = reply.Evaluated;
            if (v is null || !v.Accepted)
            {
                TelemetryPackage.Decide(call, StockpileEditDecision("refused", "preview_not_accepted", e.Role, new Dictionary<string, object?> { ["kind"] = "create", ["detail"] = v?.Reason ?? "" }));
                continue;
            }
            try
            {
                BoundaryApi.Context(v.Context, snapshot);
            }
            catch (Exception err) when (err is not OperationCanceledException)
            {
                throw new ControlException("create: err != nil || domain.Tick(v.Context.GetTick()) < tick", err);
            }
            if (new Tick(v.Context.Tick) < tick)
            {
                throw new ControlException("create: err != nil || domain.Tick(v.Context.GetTick()) < tick");
            }
            var action = DomainAction.NewZoneCreateAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), value);
            actions.Add(action);
            previews.Add(new Pol.Preview { Action = action, Snapshot = snapshot, Tick = tick, CanPlace = Known(true), SafeToPlace = Known(true), MadeFromStuff = Known(false), WatchCellsAccessible = Known(true), Footprint = Known(value.Cells()), Costs = Known<IReadOnlyList<Pol.Amount>>([]) });
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
        var decision = AdmitMethod(call, p.Journal, new Sto.BuildingMethodRequest
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
        });
        if (!decision.Admitted)
        {
            return new RoundsStockpileResult { Verdict = AdmissionRefused(decision) };
        }
        foreach (var e in admitted)
        {
            TelemetryPackage.Decide(call, StockpileEditDecision("admitted", "", e.Role, new Dictionary<string, object?> { ["kind"] = Wire.Name(e.Kind), ["role"] = e.Role, ["cells"] = e.Cells().Count, ["plan"] = id.ToString(), ["detail"] = e.Explanation }));
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
            if (z.Kind != ZoneKind.StockpileZone || !byZone.TryGetValue(z.Id, out var entry))
            {
                continue;
            }
            var zone = new Pol.StockpileZone { Id = z.Id, Role = z.Role, Cells = entry.All, Stored = entry.Stored, Filter = z.Filter, Priority = z.Priority };
            if (patches is not null && patches.TryGetValue(z.Id, out var patch) && patch.Kind == StorageTargetKind.StorageZoneTarget)
            {
                zone.Filter = patch.Filter;
                zone.Priority = patch.Priority;
                if (patch.Role != "")
                {
                    zone.Role = patch.Role;
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
                case ActionKind.ZoneCreateAction:
                case ActionKind.ZoneDeleteAction:
                case ActionKind.ZoneCellEditAction:
                case ActionKind.StockpilePatchAction:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reports whether one of the owner's methods holds a zone-edit plan still open. A room shell is building
    /// work that takes days; zoning is instant, so only an open zone-edit plan holds the next edits.
    /// </summary>
    internal static bool OpenEditPlan(CancellationToken ct, Sto.Store journal, Sto.StandardState owner)
    {
        foreach (var method in owner.Methods)
        {
            var plan = journal.LoadPlan(ct, method.Plan);
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
                return DomainAction.NewZoneCellEditAction(id, new ZoneCellEdit(e.Zone, ZoneCellEditKind.AddZoneCells, e.AddedCells));
            case Pol.StockpileEditKind.Shrink:
                return DomainAction.NewZoneCellEditAction(id, new ZoneCellEdit(e.Zone, ZoneCellEditKind.RemoveZoneCells, e.RemovedCells));
            case Pol.StockpileEditKind.Retarget:
                return DomainAction.NewStockpilePatchAction(id, new StockpilePatch(StorageTargetKind.StorageZoneTarget, e.Zone, e.Filter, e.Priority, e.Role));
            case Pol.StockpileEditKind.ShelfPatch:
                return DomainAction.NewStockpilePatchAction(id, new StockpilePatch(StorageTargetKind.StorageBuildingTarget, e.Zone, e.Filter, e.Priority, e.Role));
            case Pol.StockpileEditKind.Delete:
                return DomainAction.NewZoneDeleteAction(id, new ZoneDelete(e.Zone));
        }
        throw new ArgumentException($"unknown stockpile edit \"{Wire.Name(e.Kind)}\"");
    }
}
