using RimGovernor.Brain.Bridge.Contracts;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using EquatableArray = RimGovernor.Brain.Domain.EquatableArray;

namespace RimGovernor.Brain.BuildingRuntime;

// Go internal/bridge defense_site.go (ReadDefenseSite, ReadLinesOfFire) and spatial_access.go (ReadSpatialAccess):
// the typed core methods and the decoders that turn their replies into the Bridge.Contracts read models.
public sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>LinesOfFireBudgetMS</c> (frames.go): the main-thread milliseconds a lines-of-fire read may spend.</summary>
    private const uint DefenseLinesOfFireBudgetMs = 15;

    /// <summary>Go <c>SpatialAccessBudgetMS</c> (frames.go): the main-thread milliseconds a spatial access audit may spend.</summary>
    private const uint DefenseSpatialAccessBudgetMs = 2500;

    /// <summary>Go <c>maxDefenseSiteExtent</c>: bounds a defense-layout cell coordinate.</summary>
    private const int DefenseMaxExtent = 4096;

    /// <summary>Go <c>Client.ReadDefenseSite</c>: reads one inclusive rectangle; the read never samples or truncates.</summary>
    internal async Task<DefenseSite> ReadDefenseSiteAsync(PbCommon.Identity identity, CellRect region, CancellationToken cancellationToken)
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
        var reply = await Facade.ReadDefenseSiteAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        return DefenseDecodeSite(reply.Outcome.Observed!, region);
    }

    /// <summary>
    /// Go <c>Client.ReadLinesOfFire</c>: reads every (firing, approach) pair of unique cells, firing-major, under the
    /// lines-of-fire budget; <see cref="LinesOfFire.Skipped"/> reports a read cut short.
    /// </summary>
    internal async Task<LinesOfFire> ReadLinesOfFireAsync(PbCommon.Identity identity, IReadOnlyList<DomainCell> firing, IReadOnlyList<DomainCell> approach, CancellationToken cancellationToken)
    {
        DefenseValidateLineCells(firing);
        DefenseValidateLineCells(approach);
        var request = new PbObservations.LinesOfFireRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            BudgetMs = DefenseLinesOfFireBudgetMs,
            FiringCells = EquatableArray.From(firing.Select(DefenseWireCell)),
            ApproachCells = EquatableArray.From(approach.Select(DefenseWireCell)),
        };
        var reply = await Facade.ReadLinesOfFireAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        var observed = reply.Outcome.Observed!;
        var lines = DefenseDecodeLinesOfFire(observed);
        return new LinesOfFire(observed.Context, lines, observed.Skipped.GetValueOrDefault());
    }

    /// <summary>
    /// Go <c>Client.ReadSpatialAccess</c>: audits the map's colonist access (paused or running) with blocked cells
    /// impassable and targets checked; empty <paramref name="pawnIds"/> audits every mobile free colonist.
    /// </summary>
    internal async Task<SpatialAccess> ReadSpatialAccessAsync(
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
            if (!WireValidation.IsValidId(id) || !seenId.Add(id))
            {
                throw BridgeContractException.Contract("invalid spatial access pawn id");
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
        var reply = await Facade.ReadSpatialAccessAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        return DefenseDecodeSpatialAccess(reply.Outcome.Observed!);
    }

    private static PbCommon.Cell DefenseWireCell(DomainCell cell) => new() { X = cell.X, Z = cell.Z };

    /// <summary>Go <c>validateLineCells</c>.</summary>
    private static void DefenseValidateLineCells(IReadOnlyList<DomainCell> cells)
    {
        if (cells.Count < 1)
        {
            throw BridgeContractException.Contract("lines of fire needs at least one cell on each side");
        }
        var seen = new HashSet<DomainCell>();
        foreach (var cell in cells)
        {
            if (cell.X < 0 || cell.Z < 0 || !seen.Add(cell))
            {
                throw BridgeContractException.Contract("invalid or duplicate line of fire cell");
            }
        }
    }

    /// <summary>Go <c>validateDefenseSite</c>: decodes the defense site census of <paramref name="region"/>.</summary>
    private static DefenseSite DefenseDecodeSite(PbObservations.DefenseSiteSnapshot v, CellRect region)
    {
        var cells = new List<BridgeDefenseCell>(v.Cells.Count);
        foreach (var row in v.Cells)
        {
            FactHelpers.TryProtoCell(row.Cell, out var cell);
            if (row.Fogged.GetValueOrDefault())
            {
                // Unknown geometry carries no facts.
                cells.Add(new BridgeDefenseCell { Cell = cell, Fogged = true });
                continue;
            }
            BridgeDefenseCover? cover = null;
            if (row.CoverThingId is not null || row.CoverDefName is not null || row.CoverKind is not null || row.CoverDesignated is not null)
            {
                cover = new BridgeDefenseCover(row.CoverThingId ?? "", row.CoverDefName ?? "", row.CoverKind.GetValueOrDefault(), row.CoverDesignated.GetValueOrDefault());
            }
            cells.Add(
                new BridgeDefenseCell
                {
                    Cell = cell,
                    Fogged = false,
                    Terrain = row.Terrain ?? "",
                    EdificeDefName = row.EdificeDefName ?? "",
                    EdificeStuff = row.EdificeStuffDefName ?? "",
                    Walkable = row.Walkable.GetValueOrDefault(),
                    Passable = row.Passable.GetValueOrDefault(),
                    CoverFill = row.CoverFill.GetValueOrDefault(),
                    PlayerOwned = row.PlayerOwned.GetValueOrDefault(),
                    NaturalRock = row.NaturalRock.GetValueOrDefault(),
                    Door = row.Door.GetValueOrDefault(),
                    EdgeReachable = row.EdgeReachable.GetValueOrDefault(),
                    Unbridging = row.FoundationRemovalDesignated.GetValueOrDefault(),
                    Roofed = row.Roofed.GetValueOrDefault(),
                    Cover = cover,
                }
            );
        }
        return new DefenseSite(v.Context, v.MapSize.Width, v.MapSize.Height, region, cells);
    }

    /// <summary>Go <c>validateLinesOfFire</c>: decodes the lines of fire; a line without line-of-sight facts is unknown.</summary>
    internal static List<LineOfFire> DefenseDecodeLinesOfFire(PbObservations.LinesOfFireSnapshot v)
    {
        var lines = new List<LineOfFire>(v.Lines.Count);
        foreach (var row in v.Lines)
        {
            FactHelpers.TryProtoCell(row.From, out var from);
            FactHelpers.TryProtoCell(row.To, out var to);
            if (row.LineOfSight is not null)
            {
                lines.Add(
                    new LineOfFire
                    {
                        From = from,
                        To = to,
                        Distance = row.Distance.GetValueOrDefault(),
                        Known = true,
                        LineOfSight = row.LineOfSight.GetValueOrDefault(),
                        TargetCover = row.TargetCover.GetValueOrDefault(),
                        ShooterCover = row.ShooterCover.GetValueOrDefault(),
                    }
                );
            }
            else
            {
                lines.Add(new LineOfFire { From = from, To = to, Distance = row.Distance.GetValueOrDefault() });
            }
        }
        return lines;
    }

    /// <summary>Go <c>validateSpatialAccess</c>: decodes the spatial access audit; a skipped colonist carries its position only.</summary>
    private static SpatialAccess DefenseDecodeSpatialAccess(PbObservations.SpatialAccessSnapshot v)
    {
        var pawns = new List<PawnAccess>(v.Pawns.Count);
        foreach (var row in v.Pawns)
        {
            var pawnId = row.Pawn?.Id ?? "";
            FactHelpers.TryProtoCell(row.Position, out var position);
            if (row.Skipped.GetValueOrDefault())
            {
                // Native left this colonist unaudited: position only, nothing else.
                pawns.Add(new PawnAccess { Id = pawnId, Position = position, Skipped = true });
                continue;
            }
            var originKnown = FactHelpers.TryProtoCell(row.ProjectedOrigin, out var projectedOrigin);
            var accessTargets = new List<AccessTarget>(row.Targets.Count);
            foreach (var raw in row.Targets)
            {
                FactHelpers.TryProtoCell(raw.Cell, out var cell);
                accessTargets.Add(
                    new AccessTarget
                    {
                        Cell = cell,
                        NativeReachable = raw.NativeReachable.GetValueOrDefault(),
                        ProjectedReachable = raw.ProjectedReachable.GetValueOrDefault(),
                        ProjectedSteps = raw.ProjectedSteps.GetValueOrDefault(),
                    }
                );
            }
            pawns.Add(
                new PawnAccess
                {
                    Id = pawnId,
                    Position = position,
                    Current = row.CurrentCells.GetValueOrDefault(),
                    After = row.ProjectedCells.GetValueOrDefault(),
                    LosesAccess = row.LosesAccess.GetValueOrDefault(),
                    EgressSteps = row.EgressSteps.GetValueOrDefault(),
                    OriginKnown = originKnown,
                    ProjectedOrigin = projectedOrigin,
                    Targets = accessTargets,
                }
            );
        }
        return new SpatialAccess
        {
            Context = v.Context,
            MapCells = v.MapCells.GetValueOrDefault(),
            Walkable = v.ObservedWalkableCells.GetValueOrDefault(),
            Pawns = pawns,
        };
    }
}
