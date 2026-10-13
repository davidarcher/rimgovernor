using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;

namespace RimGovernor.Brain.Host;

// Go combat_geometry.go (CombatGeometry and its request and reply validators) and pawn_cargo.go (ReadPawnCargo).
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>CombatGeometry</c>: DecideCombat's one geometry read per stop. For candidate cells and hostiles: cover,
    /// line of fire and colonist-in-path by the game's own rules, and the pawn's path ticks to each cell when a pawn
    /// is named. The reply's cells and lines are in request order; with a propose block its proposed cells follow,
    /// ranked, scored alike.
    /// </summary>
    internal async Task<(PbMirror.CombatGeometry Value, BridgeResult Receipt)> CombatGeometryAsync(PbMirror.CombatGeometryRequest request, CancellationToken cancellationToken)
    {
        CombatGeometryChecks.ValidateCombatGeometryRequest(request);
        var reply = await Facade.ReadCombatGeometryAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Observed is { } observed)
        {
            CombatGeometryChecks.ValidateCombatGeometry(observed, request);
            return (observed, NoReceipt);
        }
        throw Contract("combat geometry outcome missing");
    }

    /// <summary>The request and reply checks of the combat geometry read.</summary>
    internal static class CombatGeometryChecks
    {
        /// <summary>
        /// Go <c>ValidateCombatGeometryRequest</c>: checks the caps and shape. An identity, 1..max distinct cells (0..max-1
        /// with propose), 1..max distinct hostile ids (0 for named cells with no propose and no pawn), a valid pawn id
        /// when one is named, and a propose block's role and anchor.
        /// </summary>
        internal static void ValidateCombatGeometryRequest(PbMirror.CombatGeometryRequest? request)
        {
            if (request is null)
            {
                throw Contract("combat geometry request required");
            }
            ValidateIdentity(request.Identity);
            var min = 1;
            if (request.Propose is not null)
            {
                min = 0;
                if (request.Cells.Count >= BridgeConstants.CombatGeometryMaxCells)
                {
                    throw Contract($"combat geometry propose leaves no room under {BridgeConstants.CombatGeometryMaxCells} cells");
                }
            }
            GeometryCells(request.Cells, min);

            // Named cells alone need no hostile: their standability.
            var hostiles = 1;
            if (request.Propose is null && request.PawnId is null)
            {
                hostiles = 0;
            }
            var n = request.HostileIds.Count;
            if (n < hostiles || n > BridgeConstants.CombatGeometryMaxHostiles)
            {
                throw Contract($"combat geometry hostiles outside {hostiles}..{BridgeConstants.CombatGeometryMaxHostiles}");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in request.HostileIds)
            {
                if (!IsValidId(id) || !seen.Add(id))
                {
                    throw Contract("invalid duplicate combat detail ID");
                }
            }
            if (request.PawnId is not null && !IsValidId(request.PawnId))
            {
                throw Contract("combat geometry pawn id invalid");
            }
            if (request.Propose?.Role?.RescuePath is not null && request.PawnId is null)
            {
                throw Contract("combat geometry rescue_path needs a pawn");
            }
            if (request.Propose is not null)
            {
                ValidatePropose(request.Propose);
            }
        }

        /// <summary>Go <c>geometryCells</c>: min..cap distinct, valid cells.</summary>
        private static void GeometryCells(EquatableArray<PbCommon.Cell> cells, int min)
        {
            var n = cells.Count;
            if (n < min || n > BridgeConstants.CombatGeometryMaxCells)
            {
                throw Contract($"combat geometry cells outside {min}..{BridgeConstants.CombatGeometryMaxCells}");
            }
            var seen = new HashSet<(int, int)>();
            foreach (var cell in cells)
            {
                if (!GeometryCell(cell))
                {
                    throw Contract("combat geometry cell invalid");
                }
                if (!seen.Add((cell!.X.GetValueOrDefault(), cell.Z.GetValueOrDefault())))
                {
                    throw Contract("combat geometry cell repeated");
                }
            }
        }

        private static bool GeometryCell(PbCommon.Cell? cell) =>
            cell is not null && cell.X is not null && cell.Z is not null && cell.X.GetValueOrDefault() >= 0 && cell.Z.GetValueOrDefault() >= 0;

        /// <summary>Go <c>validatePropose</c>: one role and its anchor.</summary>
        private static void ValidatePropose(PbMirror.CombatGeometryPropose p)
        {
            switch (p.Role)
            {
                case PbMirror.CombatGeometryProposeRole.CoverBehindLineCase role:
                    GeometryCells(role.Value?.Line ?? [], 1);
                    return;
                case PbMirror.CombatGeometryProposeRole.AdjacentToChokeCase role:
                    var a = role.Value;
                    if (!GeometryCell(a?.Choke) || !GeometryCell(a?.OurSide) || Equals(a!.Choke, a.OurSide))
                    {
                        throw Contract("combat geometry adjacent_to_choke needs a choke and a distinct our_side cell");
                    }
                    return;
                case PbMirror.CombatGeometryProposeRole.RescuePathCase role:
                    if (!GeometryCell(role.Value?.To))
                    {
                        throw Contract("combat geometry rescue_path needs a to cell");
                    }
                    return;
                case PbMirror.CombatGeometryProposeRole.FiringCellsCase role:
                    var f = role.Value;
                    if (!GeometryCell(f?.From))
                    {
                        throw Contract("combat geometry firing_cells needs a from cell");
                    }
                    if (f!.Radius is not { } radius || radius < 1 || radius > BridgeConstants.CombatGeometryMaxRadius)
                    {
                        throw Contract($"combat geometry firing_cells radius outside 1..{BridgeConstants.CombatGeometryMaxRadius}");
                    }
                    if (f.Targets.Count > BridgeConstants.CombatGeometryMaxHostiles)
                    {
                        throw Contract($"combat geometry firing_cells targets over {BridgeConstants.CombatGeometryMaxHostiles}");
                    }
                    GeometryCells(f.Targets, 1);
                    return;
            }
            throw Contract("combat geometry propose without a role");
        }

        /// <summary>
        /// Go <c>ValidateCombatGeometry</c>: checks a reply against its request: the request's world, every cell in order,
        /// every hostile's line in order, cover in 0..1, and proposals only for a propose block: standable, distinct from
        /// each other and the named cells, within the cells cap.
        /// </summary>
        internal static void ValidateCombatGeometry(PbMirror.CombatGeometry? g, PbMirror.CombatGeometryRequest request)
        {
            if (g is null)
            {
                throw Contract("combat geometry missing");
            }
            ValidateContext(g.Context);
            if (!SameIdentity(g.Context?.Identity, request.Identity))
            {
                throw Contract("combat geometry world mismatch");
            }
            if (g.Cells.Count != request.Cells.Count)
            {
                throw Contract("combat geometry cell count");
            }
            var seen = new HashSet<(int, int)>();
            for (var i = 0; i < g.Cells.Count; i++)
            {
                var row = g.Cells[i];
                if (!Equals(row?.Cell, request.Cells[i]))
                {
                    throw Contract($"combat geometry cell {i} off its request");
                }
                seen.Add((row!.Cell?.X ?? 0, row.Cell?.Z ?? 0));
                GeometryLines(row, request, i);
            }
            if (request.Propose is null)
            {
                if (g.Proposed.Count != 0)
                {
                    throw Contract("combat geometry proposals without propose");
                }
                return;
            }
            if (g.Proposed.Count > BridgeConstants.CombatGeometryMaxCells - request.Cells.Count)
            {
                throw Contract("combat geometry proposals over the cells cap");
            }
            var route = request.Propose.Role?.RescuePath is not null;
            for (var i = 0; i < g.Proposed.Count; i++)
            {
                var row = g.Proposed[i];
                var cell = row?.Cell;
                var key = (cell?.X ?? 0, cell?.Z ?? 0);
                if (!GeometryCell(cell) || (!route && (!(row!.Standable ?? false) || seen.Contains(key))))
                {
                    throw Contract($"combat geometry proposal {i} invalid or repeated");
                }
                if (!route)
                {
                    seen.Add(key);
                }
                GeometryLines(row!, request, request.Cells.Count + i);
            }
        }

        /// <summary>Go <c>geometryLines</c>: checks one scored row: a line per request hostile in order, cover in 0..1, path ticks only for a pawn.</summary>
        private static void GeometryLines(PbMirror.CombatGeometryCell row, PbMirror.CombatGeometryRequest request, int i)
        {
            if (row.Lines.Count != request.HostileIds.Count)
            {
                throw Contract($"combat geometry cell {i} lines off its request");
            }
            if (row.PathTicks is not null && (request.PawnId is null || row.PathTicks.GetValueOrDefault() < 0))
            {
                throw Contract("combat geometry path ticks without a pawn");
            }
            for (var j = 0; j < row.Lines.Count; j++)
            {
                var line = row.Lines[j];
                var cover = line?.Cover.GetValueOrDefault() ?? 0;
                if ((line?.HostileId ?? "") != request.HostileIds[j] || line!.Cover is null || double.IsNaN(cover) || cover < 0 || cover > 1)
                {
                    throw Contract($"combat geometry line {i}.{j} invalid");
                }
            }
        }
    }

    /// <summary>
    /// Go <c>ReadPawnCargo</c>: reuses the complete held-stock census. Only the named pawns' inventory and carry slots
    /// count; loose home stock cannot prove delivery.
    /// </summary>
    internal async Task<(IReadOnlyDictionary<string, long> Value, BridgeResult Receipt)> ReadPawnCargoAsync(PbCommon.Identity identity, IReadOnlyList<string> pawns, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var request = new PbObservations.ListSuppliesRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.StockFilter
            {
                Category = PbObservations.StockCategory.Haulable,
                Ownership = PbObservations.StockOwnership.Ours,
                IncludeHeld = true,
            },
        };
        var reply = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        var snapshot = reply.Outcome?.Observed;
        if (snapshot is null)
        {
            throw Contract("unknown pawn cargo census");
        }
        try
        {
            ValidateContext(snapshot.Context);
        }
        catch (BridgeContractException)
        {
            throw Contract("unknown pawn cargo census");
        }
        if (!SameIdentity(snapshot.Context!.Identity, identity) || snapshot.Completeness is null || snapshot.Completeness.Filtered is null)
        {
            throw Contract("unknown pawn cargo census");
        }
        var crew = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pawns)
        {
            if (!IsValidId(id) || !crew.Add(id))
            {
                throw Contract("invalid cargo crew");
            }
        }
        var cargo = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var stock in snapshot.Stocks)
        {
            if (stock is null || stock.Definition is null || !IsValidId(stock.Definition.DefName ?? ""))
            {
                throw Contract("invalid cargo definition");
            }
            var definition = stock.Definition.DefName ?? "";
            foreach (var held in stock.Holders)
            {
                if (held is null || held.Holder is null || held.Units is null || held.Units.GetValueOrDefault() < 0 || held.HolderKind is null)
                {
                    throw Contract("unknown cargo holder");
                }
                if (crew.Contains(held.Holder.Id ?? "") && held.HolderKind.GetValueOrDefault() is PbObservations.HolderKind.PawnInventory or PbObservations.HolderKind.Carried)
                {
                    cargo.TryGetValue(definition, out var count);
                    if (count > long.MaxValue - held.Units.GetValueOrDefault())
                    {
                        throw Contract("cargo count overflow");
                    }
                    cargo[definition] = count + held.Units.GetValueOrDefault();
                }
            }
        }
        return (cargo, NoReceipt);
    }
}
