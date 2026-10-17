using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;

namespace RimGovernor.Brain.Host;

// Go combat_geometry.go (CombatGeometry and its request validator) and pawn_cargo.go (ReadPawnCargo).
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>CombatGeometry</c>: DecideCombat's one geometry read per stop. For candidate cells and hostiles: cover,
    /// line of fire and colonist-in-path by the game's own rules, and the pawn's path ticks to each cell when a pawn
    /// is named. The reply's cells and lines are in request order; with a propose block its proposed cells follow,
    /// ranked, scored alike.
    /// </summary>
    internal async Task<PbMirror.CombatGeometry> CombatGeometryAsync(PbMirror.CombatGeometryRequest request, CancellationToken cancellationToken)
    {
        CombatGeometryChecks.ValidateCombatGeometryRequest(request);
        var reply = await Facade.ReadCombatGeometryAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        return reply.Outcome.Observed!;
    }

    /// <summary>The request checks of the combat geometry read.</summary>
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
                if (!seen.Add((cell!.X, cell.Z)))
                {
                    throw Contract("combat geometry cell repeated");
                }
            }
        }

        private static bool GeometryCell(PbCommon.Cell? cell) =>
            cell is not null && cell.X >= 0 && cell.Z >= 0;

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
    }

    /// <summary>
    /// Go <c>ReadPawnCargo</c>: reuses the complete held-stock census. Only the named pawns' inventory and carry slots
    /// count; loose home stock cannot prove delivery.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, long>> ReadPawnCargoAsync(PbCommon.Identity identity, IReadOnlyList<string> pawns, CancellationToken cancellationToken)
    {
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
        var crew = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pawns)
        {
            if (!IsValidId(id) || !crew.Add(id))
            {
                throw Contract("invalid cargo crew");
            }
        }
        var reply = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        var snapshot = reply.Outcome.Observed!;
        var cargo = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var stock in snapshot.Stocks)
        {
            var definition = stock.Definition.DefName ?? "";
            foreach (var held in stock.Holders)
            {
                if (crew.Contains(held.Holder.Id ?? "") && held.HolderKind is PbObservations.HolderKind.PawnInventory or PbObservations.HolderKind.Carried)
                {
                    cargo[definition] = cargo.GetValueOrDefault(definition) + held.Units;
                }
            }
        }
        return cargo;
    }
}
