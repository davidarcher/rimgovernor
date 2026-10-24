using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// DecideCombat's one geometry read per stop. For candidate cells and hostiles: cover,
    /// line of fire and colonist-in-path by the game's own rules. The reply's cells and lines are in request order;
    /// with a propose block its proposed cells follow, ranked, scored alike. A refused read throws.
    /// </summary>
    internal Task<PbMirror.CombatGeometry> CombatGeometryAsync(PbMirror.CombatGeometryRequest request, CancellationToken cancellationToken)
    {
        CombatGeometryChecks.ValidateCombatGeometryRequest(request);
        return Facade.ReadCombatGeometryAsync(request, cancellationToken);
    }

    /// <summary>The request checks of the combat geometry read.</summary>
    internal static class CombatGeometryChecks
    {
        /// <summary>
        /// Checks the caps and shape: 1..max distinct cells (0..max-1 with propose), 1..max distinct hostile ids (0 for
        /// named cells with no propose), and a propose block's anchor.
        /// </summary>
        internal static void ValidateCombatGeometryRequest(PbMirror.CombatGeometryRequest request)
        {
            var min = 1;
            if (request.Propose is not null)
            {
                min = 0;
                if (request.Cells.Count >= CombatConstants.CombatGeometryMaxCells)
                {
                    throw BridgeContractException.Contract($"combat geometry propose leaves no room under {CombatConstants.CombatGeometryMaxCells} cells");
                }
            }
            GeometryCells(request.Cells, min);

            // Named cells alone need no hostile: their standability.
            var hostiles = request.Propose is null ? 0 : 1;
            var n = request.HostileIds.Count;
            if (n < hostiles || n > CombatConstants.CombatGeometryMaxHostiles)
            {
                throw BridgeContractException.Contract($"combat geometry hostiles outside {hostiles}..{CombatConstants.CombatGeometryMaxHostiles}");
            }
            if (request.HostileIds.Distinct().Count() != n)
            {
                throw BridgeContractException.Contract("duplicate combat detail ID");
            }
            if (request.Propose is not null)
            {
                ValidatePropose(request.Propose);
            }
        }

        /// <summary>min..cap distinct, valid cells.</summary>
        private static void GeometryCells(EquatableArray<PbCommon.Cell> cells, int min)
        {
            var n = cells.Count;
            if (n < min || n > CombatConstants.CombatGeometryMaxCells)
            {
                throw BridgeContractException.Contract($"combat geometry cells outside {min}..{CombatConstants.CombatGeometryMaxCells}");
            }
            var seen = new HashSet<(int, int)>();
            foreach (var cell in cells)
            {
                if (!GeometryCell(cell))
                {
                    throw BridgeContractException.Contract("combat geometry cell invalid");
                }
                if (!seen.Add((cell.X, cell.Z)))
                {
                    throw BridgeContractException.Contract("combat geometry cell repeated");
                }
            }
        }

        private static bool GeometryCell(PbCommon.Cell cell) => cell.X >= 0 && cell.Z >= 0;

        /// <summary>One role and its anchor.</summary>
        private static void ValidatePropose(PbMirror.CombatGeometryPropose p)
        {
            switch (p)
            {
                case PbMirror.CombatGeometryPropose.CoverBehindLine role:
                    GeometryCells(role.Line, 1);
                    return;
                case PbMirror.CombatGeometryPropose.AdjacentToChoke a:
                    if (!GeometryCell(a.Choke) || !GeometryCell(a.OurSide) || Equals(a.Choke, a.OurSide))
                    {
                        throw BridgeContractException.Contract("combat geometry adjacent_to_choke needs a choke and a distinct our_side cell");
                    }
                    return;
                case PbMirror.CombatGeometryPropose.RescuePath role:
                    if (!GeometryCell(role.To) || role.Pawn.IsEmpty)
                    {
                        throw BridgeContractException.Contract("combat geometry rescue_path needs a pawn and a to cell");
                    }
                    return;
                case PbMirror.CombatGeometryPropose.FiringCells f:
                    if (!GeometryCell(f.From))
                    {
                        throw BridgeContractException.Contract("combat geometry firing_cells needs a from cell");
                    }
                    if (f.Radius < 1 || f.Radius > CombatConstants.CombatGeometryMaxRadius)
                    {
                        throw BridgeContractException.Contract($"combat geometry firing_cells radius outside 1..{CombatConstants.CombatGeometryMaxRadius}");
                    }
                    if (f.Targets.Count > CombatConstants.CombatGeometryMaxHostiles)
                    {
                        throw BridgeContractException.Contract($"combat geometry firing_cells targets over {CombatConstants.CombatGeometryMaxHostiles}");
                    }
                    GeometryCells(f.Targets, 1);
                    return;
            }
            throw new ArgumentOutOfRangeException(nameof(p), p, "combat geometry propose role");
        }
    }

    /// <summary>
    /// Reuses the complete held-stock census. Only the named pawns' inventory and carry slots
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
            if (!crew.Add(id))
            {
                throw BridgeContractException.Contract("duplicate cargo crew");
            }
        }
        var snapshot = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        var cargo = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var stock in snapshot.Stocks)
        {
            var definition = stock.DefName;
            foreach (var held in stock.Holders)
            {
                if (crew.Contains(held.Holder) && held.HolderKind is PbObservations.HolderKind.PawnInventory or PbObservations.HolderKind.Carried)
                {
                    cargo[definition] = cargo.GetValueOrDefault(definition) + held.Units;
                }
            }
        }
        return cargo;
    }
}
