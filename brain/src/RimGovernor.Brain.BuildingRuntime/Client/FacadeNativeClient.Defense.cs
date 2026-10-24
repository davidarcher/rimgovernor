using RimGovernor.Brain.Bridge.Contracts;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using EquatableArray = RimGovernor.Brain.Domain.EquatableArray;

namespace RimGovernor.Brain.BuildingRuntime;

// The typed core methods of the defense reads; the facade returns their snapshots whole and throws on a refusal.
public sealed partial class FacadeNativeClient
{
    /// <summary>The main-thread milliseconds a spatial access audit may spend.</summary>
    private const uint DefenseSpatialAccessBudgetMs = 2500;

    /// <summary>Bounds a defense-layout cell coordinate.</summary>
    private const int DefenseMaxExtent = 4096;

    /// <summary>Reads one inclusive rectangle; the read never samples or truncates.</summary>
    internal Task<PbObservations.DefenseSiteSnapshot> ReadDefenseSiteAsync(PbCommon.Identity identity, CellRect region, CancellationToken cancellationToken)
    {
        if (!BridgeCellRect.IsValidDefenseRegion(region))
        {
            throw BridgeContractException.Contract("invalid defense site region");
        }
        var request = new PbObservations.DefenseSiteRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Region = new PbObservations.Rectangle { Minimum = DefenseWireCell(region.Min), Maximum = DefenseWireCell(region.Max) },
        };
        return Facade.ReadDefenseSiteAsync(request, cancellationToken);
    }

    /// <summary>
    /// Audits the map's colonist access (paused or running) with blocked cells
    /// impassable and targets checked; empty <paramref name="pawnIds"/> audits every mobile free colonist.
    /// </summary>
    internal Task<PbObservations.SpatialAccessSnapshot> ReadSpatialAccessAsync(
        PbCommon.Identity identity,
        IReadOnlyList<DomainCell> blocked,
        IReadOnlyList<DomainCell> targets,
        IReadOnlyList<string> pawnIds,
        CancellationToken cancellationToken
    )
    {
        foreach (var cells in new[] { blocked, targets })
        {
            var seenCell = new HashSet<DomainCell>();
            foreach (var cell in cells)
            {
                if (cell.X < 0 || cell.Z < 0 || cell.X >= DefenseMaxExtent || cell.Z >= DefenseMaxExtent || !seenCell.Add(cell))
                {
                    throw BridgeContractException.Contract("invalid spatial access cell");
                }
            }
        }
        var seenId = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pawnIds)
        {
            if (!seenId.Add(id))
            {
                throw BridgeContractException.Contract("duplicate spatial access pawn id");
            }
        }
        var request = new PbObservations.SpatialAccessRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            PawnIds = EquatableArray.From(pawnIds),
            BudgetMs = DefenseSpatialAccessBudgetMs,
            BlockedCells = EquatableArray.From(blocked.Select(DefenseWireCell)),
            TargetCells = EquatableArray.From(targets.Select(DefenseWireCell)),
        };
        return Facade.ReadSpatialAccessAsync(request, cancellationToken);
    }

    private static PbCommon.Cell DefenseWireCell(DomainCell cell) => new() { X = cell.X, Z = cell.Z };
}
