using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

// Go excavation_site_read.go, clearance.go and shrines.go: the excavation site, clearance census and ancient shrine reads.
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go's <c>ReadExcavationSite</c>: reads the excavation site for the ordered cell set plus its access cell. Reply
    /// rows must echo the request order exactly so callers can index stages positionally.
    /// </summary>
    internal async Task<(ExcavationSite Site, BridgeResult Receipt)> ReadExcavationSiteAsync(PbCommon.Identity identity, IReadOnlyList<Cell> cells, Cell access, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        if (cells.Count == 0 || !BridgeFunctions.ExcavationCellValid(access))
        {
            throw BridgeFunctions.Contract("invalid excavation site request");
        }
        var requested = new List<PbCommon.Cell>(cells.Count);
        var seen = new HashSet<Cell>();
        foreach (var cell in cells)
        {
            if (!BridgeFunctions.ExcavationCellValid(cell) || !seen.Add(cell))
            {
                throw BridgeFunctions.Contract("invalid excavation site request");
            }
            requested.Add(new PbCommon.Cell { X = cell.X, Z = cell.Z });
        }
        var request = new PbObservations.ExcavationSiteRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            AccessCell = new PbCommon.Cell { X = access.X, Z = access.Z },
            Cells = [.. requested],
        };
        var reply = await Facade.ReadExcavationSiteAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Observed is not { } snapshot)
        {
            throw BridgeFunctions.Contract("missing excavation site outcome");
        }
        if (!PlacementReads.Passes(() => BridgeFunctions.ValidateContext(snapshot.Context)) || !BridgeFunctions.SameIdentity(snapshot.Context!.Identity, identity))
        {
            throw BridgeFunctions.Contract("invalid excavation site context");
        }
        if (snapshot.Cells.Count != cells.Count)
        {
            throw BridgeFunctions.Contract("incomplete excavation site");
        }
        var items = new List<ExcavationSiteCell>(cells.Count);
        for (var i = 0; i < snapshot.Cells.Count; i++)
        {
            var row = snapshot.Cells[i];
            var position = row?.Cell;
            if (row is null
                || position is null
                || position.X is null
                || position.Z is null
                || position.X.GetValueOrDefault() != cells[i].X
                || position.Z.GetValueOrDefault() != cells[i].Z
                || row.Fogged is null
                || row.Eligible is null
                || row.MineDesignated is null)
            {
                throw BridgeFunctions.Contract("excavation site row mismatch");
            }
            var item = new ExcavationSiteCell
            {
                Cell = cells[i],
                Fogged = row.Fogged.GetValueOrDefault(),
                Definition = row.MineableDefName ?? "",
                Walkable = row.Walkable.GetValueOrDefault(),
                MineDesignated = row.MineDesignated.GetValueOrDefault(),
                Eligible = row.Eligible.GetValueOrDefault(),
                Blocker = row.Blocker ?? "",
            };
            if (item.Definition.Length != 0 && !BridgeFunctions.IsValidId(item.Definition))
            {
                throw BridgeFunctions.Contract("invalid excavation cell definition");
            }
            if (item.Eligible && (item.Definition.Length == 0 || item.Blocker.Length != 0))
            {
                throw BridgeFunctions.Contract("inconsistent excavation eligibility");
            }
            items.Add(item);
        }
        var support = snapshot.SupportAfterRemoval switch
        {
            PbObservations.ExcavationSupport.Supported => ExcavationSupport.Supported,
            PbObservations.ExcavationSupport.Unknown => ExcavationSupport.Unknown,
            PbObservations.ExcavationSupport.Unsupported => ExcavationSupport.Unsupported,
            _ => throw BridgeFunctions.Contract("excavation support unspecified"),
        };
        var collapsePending = snapshot.CollapsePending.GetValueOrDefault();
        if (collapsePending && support != ExcavationSupport.Unsupported)
        {
            throw BridgeFunctions.Contract("pending collapse reported supported");
        }
        if (snapshot.WorkerAvailable is null || snapshot.AccessReachable is null)
        {
            throw BridgeFunctions.Contract("excavation worker facts missing");
        }
        var workerAvailable = snapshot.WorkerAvailable.GetValueOrDefault();
        var accessReachable = snapshot.AccessReachable.GetValueOrDefault();
        var workers = new List<string>(snapshot.WorkerIds.Count);
        foreach (var id in snapshot.WorkerIds)
        {
            if (!BridgeFunctions.IsValidId(id))
            {
                throw BridgeFunctions.Contract("invalid excavation worker");
            }
            workers.Add(id);
        }
        if (workerAvailable != (workers.Count > 0) || (workerAvailable && !accessReachable))
        {
            throw BridgeFunctions.Contract("inconsistent excavation workers");
        }
        return (
            new ExcavationSite
            {
                Context = snapshot.Context,
                Cells = items,
                Support = support,
                RoofCellsChecked = snapshot.RoofCellsChecked.GetValueOrDefault(),
                SupportBlocker = snapshot.SupportBlocker ?? "",
                CollapsePending = collapsePending,
                WorkerAvailable = workerAvailable,
                Workers = workers,
                AccessReachable = accessReachable,
            },
            NoReceipt);
    }

    /// <summary>Go's <c>ReadClearanceTargets</c>: read-only and requires no authority. An unsupported native stub is unavailable, never a successful empty census.</summary>
    internal Task<(PbObservations.ClearanceTargetsReply Reply, BridgeResult Receipt)> ReadClearanceTargetsAsync(PbCommon.Identity identity, bool includeSalvage, CancellationToken cancellationToken) =>
        ReadClearanceTargetsOnGroundAsync(identity, includeSalvage, [], cancellationToken);

    /// <summary>
    /// Go's <c>ReadClearanceTargetsOnGround</c>: widens the census to planned ground: on those rectangles native also
    /// reports the player's own buildings (with encloses_room) and constructed floors, one row per cell.
    /// </summary>
    internal async Task<(PbObservations.ClearanceTargetsReply Reply, BridgeResult Receipt)> ReadClearanceTargetsOnGroundAsync(PbCommon.Identity identity, bool includeSalvage, IReadOnlyList<PbObservations.Rectangle> planned, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        foreach (var rect in planned)
        {
            if (!PlacementReads.RectangleValid(rect, 4096))
            {
                throw BridgeFunctions.Contract("invalid planned ground rectangle");
            }
        }
        var request = new PbObservations.ClearanceTargetsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            IncludeSalvage = includeSalvage,
            SalvageMaxAgeTicks = PlacementReads.SalvageMaxAgeTicks,
            SalvageFrameBudgetMs = PlacementReads.SalvageFrameBudgetMs,
            SalvageInlineBudgetMs = PlacementReads.SalvageInlineBudgetMs,
            SalvageCensusBudgetMs = PlacementReads.SalvageCensusBudgetMs,
            PlannedGround = [.. planned],
        };
        var reply = await Facade.ReadClearanceTargetsAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        if (reply.Outcome?.Observed is { } observed)
        {
            PlacementReads.ValidateClearanceTargets(observed, identity);
            PlacementReads.ValidatePlannedFloors(observed, planned);
        }
        else if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        else if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        else
        {
            throw BridgeFunctions.Contract("clearance outcome missing");
        }
        return (reply, NoReceipt);
    }

    /// <summary>Go's <c>ReadAncientShrines</c>: read-only and requires no authority. An unsupported native stub is unavailable, never a successful empty census.</summary>
    internal async Task<(PbObservations.AncientShrinesReply Reply, BridgeResult Receipt)> ReadAncientShrinesAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        var request = new PbObservations.AncientShrinesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ReadAncientShrinesAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        if (reply.Outcome?.Observed is { } observed)
        {
            PlacementReads.ValidateAncientShrines(observed, identity);
        }
        else if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        else if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        else
        {
            throw BridgeFunctions.Contract("shrine outcome missing");
        }
        return (reply, NoReceipt);
    }

    internal static partial class PlacementReads
    {
        // Salvage evidence tunables sent on every clearance read (Go SalvageMaxAgeTicks and friends). Native computes
        // about 1 ms of colonist pathing per ruin, so these are main-thread guards: a cached row older than
        // SalvageMaxAgeTicks (four game hours) is recomputed, the frame refresher spends SalvageFrameBudgetMs per
        // update, a read computes rows with no cache entry up to SalvageInlineBudgetMs, and the first read (empty
        // cache) up to SalvageCensusBudgetMs. Rows past the budget carry salvage_skipped.
        internal const int SalvageMaxAgeTicks = 4 * 2500;
        internal const double SalvageFrameBudgetMs = 1.5;
        internal const double SalvageInlineBudgetMs = 5;
        internal const double SalvageCensusBudgetMs = 250;

        private static bool SalvageFinite(double n) => double.IsFinite(n) && n >= 0 && n <= 1e12;

        /// <summary>
        /// Go's <c>ValidateClearanceTargets</c>: rejects partial censuses and omitted safety facts. A null faction and a
        /// null roof blocker have defined meanings only in a fully validated row; false and absent booleans are never
        /// interchangeable.
        /// </summary>
        internal static void ValidateClearanceTargets(PbObservations.ClearanceTargetsSnapshot? value, PbCommon.Identity? identity)
        {
            if (value is null || !Passes(() => BridgeFunctions.ValidateContext(value.Context)) || !BridgeFunctions.SameIdentity(value.Context!.Identity, identity))
            {
                throw BridgeFunctions.Contract("invalid clearance context");
            }
            var chunks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in value.Chunks)
            {
                if (row is null
                    || !BridgeFunctions.IsValidId(row.EntityId ?? "")
                    || !BridgeFunctions.IsValidId(row.DefName ?? "")
                    || chunks.Contains(row.EntityId ?? "")
                    || row.Cell is null
                    || row.Cell.X is null
                    || row.Cell.Z is null
                    || row.Cell.X.GetValueOrDefault() < 0
                    || row.Cell.Z.GetValueOrDefault() < 0
                    || row.Forbidden is null
                    || row.Stored is null
                    || row.Destination is null
                    || (row.Stored.GetValueOrDefault() && row.Destination.GetValueOrDefault()))
                {
                    throw BridgeFunctions.Contract("invalid clearance chunk");
                }
                chunks.Add(row.EntityId ?? "");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in value.Targets)
            {
                if (row is null
                    || !BridgeFunctions.IsValidId(row.EntityId ?? "")
                    || !BridgeFunctions.IsValidId(row.DefName ?? "")
                    || seen.Contains(row.EntityId ?? "")
                    || row.Deconstructible is null
                    || !row.Deconstructible.GetValueOrDefault()
                    || row.InHome is null
                    || row.AncientDanger is null
                    || row.Designated is null)
                {
                    throw BridgeFunctions.Contract("invalid clearance target");
                }
                seen.Add(row.EntityId ?? "");
                if (row.Salvage is { } s)
                {
                    if (!SalvageFinite(s.PathLength) || !SalvageFinite(s.Labor) || s.AgeTicks < 0)
                    {
                        throw BridgeFunctions.Contract("invalid salvage costs");
                    }
                    var yields = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var y in s.Yields)
                    {
                        if (y is null
                            || !BridgeFunctions.IsValidId(y.DefName)
                            || yields.Contains(y.DefName)
                            || y.Count <= 0
                            || y.Count > 1e9
                            || y.StorageHeadroom < 0
                            || y.StorageHeadroom > 1e9
                            || !SalvageFinite(y.UnitValue))
                        {
                            throw BridgeFunctions.Contract("invalid salvage yield");
                        }
                        yields.Add(y.DefName);
                    }
                }
                if (row.Salvage is not null && row.SalvageSkipped.GetValueOrDefault())
                {
                    throw BridgeFunctions.Contract("salvage both computed and skipped");
                }
                if (row.RoofBlocker is not null && row.RoofBlocker.Length == 0)
                {
                    throw BridgeFunctions.Contract("empty clearance roof blocker");
                }
                if (row.Class < PbObservations.ClearanceClass.AncientWallDoor || row.Class > PbObservations.ClearanceClass.Other)
                {
                    throw BridgeFunctions.Contract("unknown clearance class");
                }
                if (!RectangleValid(row.Occupied, 4096))
                {
                    throw BridgeFunctions.Contract("invalid clearance occupied rectangle");
                }
            }
        }

        /// <summary>
        /// Go's <c>validatePlannedFloors</c>: requires every floor row to be a distinct complete cell inside the
        /// requested planned ground; with none requested, no floors.
        /// </summary>
        internal static void ValidatePlannedFloors(PbObservations.ClearanceTargetsSnapshot value, IReadOnlyList<PbObservations.Rectangle> planned)
        {
            bool Inside(int x, int z)
            {
                foreach (var r in planned)
                {
                    if (x >= (r.Minimum?.X ?? 0) && x <= (r.Maximum?.X ?? 0) && z >= (r.Minimum?.Z ?? 0) && z <= (r.Maximum?.Z ?? 0))
                    {
                        return true;
                    }
                }
                return false;
            }
            var seen = new HashSet<(int, int)>();
            foreach (var row in value.Floors)
            {
                if (row is null || row.Cell is null || row.Cell.X is null || row.Cell.Z is null || !BridgeFunctions.IsValidId(row.DefName ?? "") || row.Designated is null)
                {
                    throw BridgeFunctions.Contract("invalid clearance floor");
                }
                var key = (row.Cell.X.GetValueOrDefault(), row.Cell.Z.GetValueOrDefault());
                if (seen.Contains(key) || !Inside(key.Item1, key.Item2))
                {
                    throw BridgeFunctions.Contract("clearance floor outside planned ground");
                }
                seen.Add(key);
            }
        }

        /// <summary>
        /// Go's <c>ValidateAncientShrines</c>: rejects partial censuses and omitted safety facts. Guards may be empty on a
        /// sealed shrine only because guards_known is false; an opened shrine with no guards is a complete, empty list.
        /// </summary>
        internal static void ValidateAncientShrines(PbObservations.AncientShrinesSnapshot? value, PbCommon.Identity? identity)
        {
            if (value is null || !Passes(() => BridgeFunctions.ValidateContext(value.Context)) || !BridgeFunctions.SameIdentity(value.Context!.Identity, identity))
            {
                throw BridgeFunctions.Contract("invalid shrine context");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in value.Shrines)
            {
                if (row is null
                    || !BridgeFunctions.IsValidId(row.ShrineId ?? "")
                    || seen.Contains(row.ShrineId ?? "")
                    || row.Sealed is null
                    || row.InHome is null
                    || row.GuardsKnown is null
                    || (row.Sealed.GetValueOrDefault() && row.GuardsKnown.GetValueOrDefault()))
                {
                    throw BridgeFunctions.Contract("invalid shrine");
                }
                seen.Add(row.ShrineId ?? "");
                if (!RectangleValid(row.Room, 4096))
                {
                    throw BridgeFunctions.Contract("invalid shrine room");
                }
                if (!row.GuardsKnown.GetValueOrDefault() && row.Guards.Count != 0)
                {
                    throw BridgeFunctions.Contract("guards reported while unknown");
                }
                var entities = new HashSet<string>(StringComparer.Ordinal);
                foreach (var casket in row.Caskets)
                {
                    if (casket is null
                        || !BridgeFunctions.IsValidId(casket.EntityId ?? "")
                        || entities.Contains(casket.EntityId ?? "")
                        || !Passes(() => ValidCell(casket.Cell))
                        || !Passes(() => ValidCell(casket.InteractionCell))
                        || casket.HitPoints is null
                        || casket.HasContents is null
                        || casket.PlayerClaimed is null)
                    {
                        throw BridgeFunctions.Contract("invalid shrine casket");
                    }
                    entities.Add(casket.EntityId ?? "");
                }
                foreach (var guard in row.Guards)
                {
                    if (guard is null
                        || !BridgeFunctions.IsValidId(guard.EntityId ?? "")
                        || entities.Contains(guard.EntityId ?? "")
                        || guard.Downed is null
                        || guard.Dead is null
                        || guard.Kind < PbObservations.ShrineGuardKind.Mechanoid
                        || guard.Kind > PbObservations.ShrineGuardKind.Other)
                    {
                        throw BridgeFunctions.Contract("invalid shrine guard");
                    }
                    entities.Add(guard.EntityId ?? "");
                }
                foreach (var wall in row.BreachWalls)
                {
                    if (wall is null
                        || !BridgeFunctions.IsValidId(wall.EntityId ?? "")
                        || entities.Contains(wall.EntityId ?? "")
                        || !Passes(() => ValidCell(wall.Cell))
                        || !Passes(() => ValidCell(wall.Outside))
                        || !AdjacentCells(wall.Cell, wall.Outside))
                    {
                        throw BridgeFunctions.Contract("invalid shrine breach wall");
                    }
                    entities.Add(wall.EntityId ?? "");
                }

                // A hostile occupant is also listed as a guard, so occupants keep their own identity set.
                var occupants = new HashSet<string>(StringComparer.Ordinal);
                foreach (var occupant in row.Occupants)
                {
                    if (occupant is null
                        || !BridgeFunctions.IsValidId(occupant.EntityId ?? "")
                        || occupants.Contains(occupant.EntityId ?? "")
                        || occupant.Hostile is null
                        || occupant.Downed is null
                        || occupant.Dead is null
                        || occupant.Prisoner is null
                        || occupant.Faction is null)
                    {
                        throw BridgeFunctions.Contract("invalid shrine occupant");
                    }
                    occupants.Add(occupant.EntityId ?? "");
                }
                if (!row.GuardsKnown.GetValueOrDefault() && row.Occupants.Count != 0)
                {
                    throw BridgeFunctions.Contract("occupants reported while unknown");
                }
            }
        }
    }
}
