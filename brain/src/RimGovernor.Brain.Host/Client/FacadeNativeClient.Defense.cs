using RimGovernor.Brain.Bridge.Contracts;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using EquatableArray = RimGovernor.Brain.Domain.EquatableArray;

namespace RimGovernor.Brain.Host;

// Go internal/bridge defense_site.go (ReadDefenseSite, ReadLinesOfFire) and spatial_access.go (ReadSpatialAccess):
// the typed core methods and the reply validators that decode them into the Bridge.Contracts read models.
internal sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>LinesOfFireBudgetMS</c> (frames.go): the main-thread milliseconds a lines-of-fire read may spend.</summary>
    private const uint DefenseLinesOfFireBudgetMs = 15;

    /// <summary>Go <c>SpatialAccessBudgetMS</c> (frames.go): the main-thread milliseconds a spatial access audit may spend.</summary>
    private const uint DefenseSpatialAccessBudgetMs = 2500;

    /// <summary>Go <c>maxDefenseSiteExtent</c>: bounds a defense-layout cell coordinate.</summary>
    private const int DefenseMaxExtent = 4096;

    /// <summary>Go <c>Client.ReadDefenseSite</c>: reads one inclusive rectangle; the read never samples or truncates.</summary>
    internal async Task<(DefenseSite Value, BridgeResult Receipt)> ReadDefenseSiteAsync(PbCommon.Identity identity, CellRect region, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        if (!BridgeFunctions.IsValidDefenseRegion(region))
        {
            throw BridgeFunctions.Contract("invalid defense site region");
        }
        var request = new PbObservations.DefenseSiteRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Region = new PbObservations.Rectangle { Minimum = DefenseWireCell(region.Min), Maximum = DefenseWireCell(region.Max) },
        };
        var reply = await Facade.ReadDefenseSiteAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        switch (reply.Outcome)
        {
            case PbObservations.DefenseSiteReplyOutcome.FailureCase failure:
                throw Failure(failure.Value);
            case PbObservations.DefenseSiteReplyOutcome.UnavailableCase unavailable:
                throw Unavailable(unavailable.Value);
            case PbObservations.DefenseSiteReplyOutcome.ObservedCase observed:
                return (DefenseValidateSite(observed.Value, identity, region), NoReceipt);
            default:
                throw BridgeFunctions.Contract("defense site outcome missing");
        }
    }

    /// <summary>
    /// Go <c>Client.ReadLinesOfFire</c>: reads every (firing, approach) pair of unique cells, firing-major, under the
    /// lines-of-fire budget; <see cref="LinesOfFire.Skipped"/> reports a read cut short.
    /// </summary>
    internal async Task<(LinesOfFire Value, BridgeResult Receipt)> ReadLinesOfFireAsync(PbCommon.Identity identity, IReadOnlyList<DomainCell> firing, IReadOnlyList<DomainCell> approach, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
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
        BridgeFunctions.BuildingUnknown(reply);
        switch (reply.Outcome)
        {
            case PbObservations.LinesOfFireReplyOutcome.FailureCase failure:
                throw Failure(failure.Value);
            case PbObservations.LinesOfFireReplyOutcome.UnavailableCase unavailable:
                throw Unavailable(unavailable.Value);
            case PbObservations.LinesOfFireReplyOutcome.ObservedCase observed:
                var lines = DefenseValidateLinesOfFire(observed.Value, identity, firing, approach);
                return (new LinesOfFire(observed.Value.Context, lines, observed.Value.Skipped.GetValueOrDefault()), NoReceipt);
            default:
                throw BridgeFunctions.Contract("lines of fire outcome missing");
        }
    }

    /// <summary>
    /// Go <c>Client.ReadSpatialAccess</c>: audits the map's colonist access (paused or running) with blocked cells
    /// impassable and targets checked; empty <paramref name="pawnIds"/> audits every mobile free colonist. Unknown never
    /// becomes access: an incomplete or partial reply is a contract error.
    /// </summary>
    internal async Task<(SpatialAccess Value, BridgeResult Receipt)> ReadSpatialAccessAsync(
        PbCommon.Identity identity,
        IReadOnlyList<DomainCell> blocked,
        IReadOnlyList<DomainCell> targets,
        IReadOnlyList<string> pawnIds,
        CancellationToken cancellationToken
    )
    {
        BridgeFunctions.ValidateIdentity(identity);
        foreach (var cells in new[] { blocked, targets })
        {
            var seenCell = new HashSet<DomainCell>();
            foreach (var cell in cells)
            {
                if (cell.X < 0 || cell.Z < 0 || cell.X >= DefenseMaxExtent || cell.Z >= DefenseMaxExtent || !seenCell.Add(cell))
                {
                    throw BridgeFunctions.Contract("invalid spatial access cell");
                }
            }
        }
        var seenId = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pawnIds)
        {
            if (!BridgeFunctions.IsValidId(id) || !seenId.Add(id))
            {
                throw BridgeFunctions.Contract("invalid spatial access pawn id");
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
        BridgeFunctions.BuildingUnknown(reply);
        switch (reply.Outcome)
        {
            case PbObservations.SpatialAccessReplyOutcome.FailureCase failure:
                throw Failure(failure.Value);
            case PbObservations.SpatialAccessReplyOutcome.UnavailableCase unavailable:
                throw Unavailable(unavailable.Value);
            case PbObservations.SpatialAccessReplyOutcome.ObservedCase observed:
                return (DefenseValidateSpatialAccess(observed.Value, identity, blocked, targets, pawnIds), NoReceipt);
            default:
                throw BridgeFunctions.Contract("spatial access outcome missing");
        }
    }

    private static PbCommon.Cell DefenseWireCell(DomainCell cell) => new() { X = cell.X, Z = cell.Z };

    /// <summary>Go <c>ValidateContext(ctx) == nil &amp;&amp; sameIdentity(ctx.Identity, identity)</c>.</summary>
    private static bool DefenseContextMatches(PbCommon.ObservationContext? context, PbCommon.Identity identity)
    {
        try
        {
            BridgeFunctions.ValidateContext(context);
        }
        catch (BridgeContractException)
        {
            return false;
        }
        return BridgeFunctions.SameIdentity(context!.Identity, identity);
    }

    /// <summary>Go <c>validateLineCells</c>.</summary>
    private static void DefenseValidateLineCells(IReadOnlyList<DomainCell> cells)
    {
        if (cells.Count < 1)
        {
            throw BridgeFunctions.Contract("lines of fire needs at least one cell on each side");
        }
        var seen = new HashSet<DomainCell>();
        foreach (var cell in cells)
        {
            if (cell.X < 0 || cell.Z < 0 || !seen.Add(cell))
            {
                throw BridgeFunctions.Contract("invalid or duplicate line of fire cell");
            }
        }
    }

    /// <summary>Go <c>validateDefenseSite</c>.</summary>
    private static DefenseSite DefenseValidateSite(PbObservations.DefenseSiteSnapshot? v, PbCommon.Identity identity, CellRect region)
    {
        if (v is null || !DefenseContextMatches(v.Context, identity))
        {
            throw BridgeFunctions.Contract("invalid defense site context");
        }
        if (v.MapSize is null || v.MapSize.Width is null || v.MapSize.Height is null || v.MapSize.Width.GetValueOrDefault() == 0 || v.MapSize.Height.GetValueOrDefault() == 0)
        {
            throw BridgeFunctions.Contract("defense site map size missing");
        }
        var minOk = BridgeFunctions.TryProtoCell(v.Region?.Minimum, out var min);
        var maxOk = BridgeFunctions.TryProtoCell(v.Region?.Maximum, out var max);
        if (!minOk || !maxOk || min != region.Min || max != region.Max)
        {
            throw BridgeFunctions.Contract("defense site region changed");
        }
        var want = (region.Max.X - region.Min.X + 1) * (region.Max.Z - region.Min.Z + 1);
        if (v.Cells.Count != want)
        {
            throw BridgeFunctions.Contract("incomplete defense site census");
        }
        var width = v.MapSize.Width.GetValueOrDefault();
        var height = v.MapSize.Height.GetValueOrDefault();
        var cells = new List<DefenseCell>(want);
        var seen = new HashSet<DomainCell>(want);
        foreach (var row in v.Cells)
        {
            if (row is null)
            {
                throw BridgeFunctions.Contract("missing defense cell");
            }
            if (!BridgeFunctions.TryProtoCell(row.Cell, out var cell)
                || seen.Contains(cell)
                || cell.X < region.Min.X
                || cell.X > region.Max.X
                || cell.Z < region.Min.Z
                || cell.Z > region.Max.Z
                || (long)cell.X >= width
                || (long)cell.Z >= height)
            {
                throw BridgeFunctions.Contract("defense cell outside region or duplicated");
            }
            seen.Add(cell);
            if (row.Fogged is null)
            {
                throw BridgeFunctions.Contract("defense cell visibility missing");
            }
            var fogged = row.Fogged.GetValueOrDefault();
            if (fogged)
            {
                // Unknown geometry carries no facts; a fogged cell claiming traversal or cover would be an invented observation.
                if (row.Walkable is not null || row.Passable is not null || row.CoverFill is not null || row.EdgeReachable is not null || row.EdificeDefName is not null || row.EdificeStuffDefName is not null)
                {
                    throw BridgeFunctions.Contract("fogged defense cell carries facts");
                }
                cells.Add(new DefenseCell { Cell = cell, Fogged = true });
                continue;
            }
            if (row.Terrain is null
                || !BridgeFunctions.IsValidId(row.Terrain)
                || row.Walkable is null
                || row.Passable is null
                || row.EdgeReachable is null
                || row.PlayerOwned is null
                || row.NaturalRock is null
                || row.Door is null
                || !BridgeFunctions.Fraction(row.CoverFill))
            {
                throw BridgeFunctions.Contract("defense cell facts missing or invalid");
            }
            if (row.EdificeDefName is not null && !BridgeFunctions.IsValidId(row.EdificeDefName))
            {
                throw BridgeFunctions.Contract("invalid defense cell edifice");
            }
            if (row.EdificeStuffDefName is not null && (row.EdificeDefName is null || !BridgeFunctions.IsValidId(row.EdificeStuffDefName)))
            {
                throw BridgeFunctions.Contract("invalid defense cell edifice stuff");
            }
            if (row.EdificeDefName is null && (row.PlayerOwned.GetValueOrDefault() || row.NaturalRock.GetValueOrDefault() || row.Door.GetValueOrDefault()))
            {
                throw BridgeFunctions.Contract("edifice facts without an edifice");
            }
            if ((row.Walkable.GetValueOrDefault() && !row.Passable.GetValueOrDefault()) || (row.EdgeReachable.GetValueOrDefault() && !row.Passable.GetValueOrDefault()))
            {
                throw BridgeFunctions.Contract("contradictory defense cell traversal");
            }
            DefenseCover? cover = null;
            if (row.CoverThingId is not null || row.CoverDefName is not null || row.CoverKind is not null || row.CoverDesignated is not null)
            {
                if (!BridgeFunctions.IsValidId(row.CoverThingId ?? "")
                    || !BridgeFunctions.IsValidId(row.CoverDefName ?? "")
                    || row.CoverDesignated is null
                    || row.CoverKind.GetValueOrDefault() == PbObservations.CoverKind.Unspecified
                    || row.CoverFill.GetValueOrDefault() <= 0)
                {
                    throw BridgeFunctions.Contract("defense cell cover identity incomplete");
                }
                cover = new DefenseCover(row.CoverThingId ?? "", row.CoverDefName ?? "", row.CoverKind.GetValueOrDefault(), row.CoverDesignated.GetValueOrDefault());
            }
            cells.Add(
                new DefenseCell
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
        return new DefenseSite(v.Context, width, height, region, cells);
    }

    /// <summary>Go <c>validateLinesOfFire</c>.</summary>
    internal static List<LineOfFire> DefenseValidateLinesOfFire(PbObservations.LinesOfFireSnapshot? v, PbCommon.Identity identity, IReadOnlyList<DomainCell> firing, IReadOnlyList<DomainCell> approach)
    {
        if (v is null || !DefenseContextMatches(v.Context, identity))
        {
            throw BridgeFunctions.Contract("invalid lines of fire context");
        }
        var want = firing.Count * approach.Count;
        var skipped = v.Skipped.GetValueOrDefault();
        // A skipped snapshot holds a prefix of the product; otherwise it is whole.
        if ((skipped && v.Lines.Count > want) || (!skipped && v.Lines.Count != want))
        {
            throw BridgeFunctions.Contract("incomplete lines of fire");
        }
        var wantFrom = new HashSet<DomainCell>(firing);
        var wantTo = new HashSet<DomainCell>(approach);
        var seen = new HashSet<(DomainCell From, DomainCell To)>(want);
        var lines = new List<LineOfFire>(want);
        foreach (var row in v.Lines)
        {
            if (row is null)
            {
                throw BridgeFunctions.Contract("missing line of fire");
            }
            var fromOk = BridgeFunctions.TryProtoCell(row.From, out var from);
            var toOk = BridgeFunctions.TryProtoCell(row.To, out var to);
            if (!fromOk || !toOk || !wantFrom.Contains(from) || !wantTo.Contains(to) || !seen.Add((from, to)))
            {
                throw BridgeFunctions.Contract("line of fire pair unexpected or duplicated");
            }
            if (row.Distance is null || !double.IsFinite(row.Distance.GetValueOrDefault()) || row.Distance.GetValueOrDefault() < 0)
            {
                throw BridgeFunctions.Contract("invalid line of fire distance");
            }
            var known = row.LineOfSight is not null;
            if (known != (row.TargetCover is not null) || known != (row.ShooterCover is not null))
            {
                throw BridgeFunctions.Contract("partial line of fire facts");
            }
            if (known)
            {
                if (!BridgeFunctions.Fraction(row.TargetCover) || !BridgeFunctions.Fraction(row.ShooterCover))
                {
                    throw BridgeFunctions.Contract("invalid line of fire cover");
                }
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
            else if (row.Issues.Count == 0)
            {
                throw BridgeFunctions.Contract("unknown line of fire without issue");
            }
            else
            {
                lines.Add(new LineOfFire { From = from, To = to, Distance = row.Distance.GetValueOrDefault() });
            }
        }
        return lines;
    }

    /// <summary>Go <c>validateSpatialAccess</c>.</summary>
    private static SpatialAccess DefenseValidateSpatialAccess(
        PbObservations.SpatialAccessSnapshot? v,
        PbCommon.Identity identity,
        IReadOnlyList<DomainCell> blocked,
        IReadOnlyList<DomainCell> targets,
        IReadOnlyList<string> pawnIds
    )
    {
        if (v is null || !DefenseContextMatches(v.Context, identity))
        {
            throw BridgeFunctions.Contract("invalid spatial access context");
        }
        if (v.MapCells is null || v.ObservedWalkableCells is null || v.MapCells.GetValueOrDefault() == 0 || v.ObservedWalkableCells.GetValueOrDefault() > v.MapCells.GetValueOrDefault())
        {
            throw BridgeFunctions.Contract("spatial access map census missing");
        }
        if (v.Pawns.Count == 0 || (pawnIds.Count != 0 && v.Pawns.Count != pawnIds.Count))
        {
            throw BridgeFunctions.Contract("incomplete spatial access census");
        }
        var wanted = new HashSet<string>(pawnIds, StringComparer.Ordinal);
        var blockedSet = new HashSet<DomainCell>(blocked);
        var pawns = new List<PawnAccess>(v.Pawns.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Pawns)
        {
            if (row is null || row.Pawn is null || row.Pawn.Id is null || !BridgeFunctions.IsValidId(row.Pawn.Id) || seen.Contains(row.Pawn.Id) || (pawnIds.Count != 0 && !wanted.Contains(row.Pawn.Id)))
            {
                throw BridgeFunctions.Contract("invalid spatial access pawn");
            }
            var pawnId = row.Pawn.Id;
            seen.Add(pawnId);
            var positionOk = BridgeFunctions.TryProtoCell(row.Position, out var position);
            if (row.Skipped.GetValueOrDefault())
            {
                // Native left this colonist unaudited: position only, nothing else.
                if (!positionOk || row.CurrentCells is not null || row.ProjectedCells is not null || row.LosesAccess is not null || row.EgressSteps is not null || row.ProjectedOrigin is not null || row.Targets.Count != 0)
                {
                    throw BridgeFunctions.Contract("skipped spatial access row carries facts");
                }
                pawns.Add(new PawnAccess { Id = pawnId, Position = position, Skipped = true });
                continue;
            }
            if (!positionOk || row.CurrentCells is null || row.ProjectedCells is null || row.LosesAccess is null || row.EgressSteps is null || row.Targets.Count != targets.Count)
            {
                throw BridgeFunctions.Contract("incomplete spatial access row");
            }
            var current = row.CurrentCells.GetValueOrDefault();
            var after = row.ProjectedCells.GetValueOrDefault();
            var egress = row.EgressSteps.GetValueOrDefault();
            if (after > current)
            {
                throw BridgeFunctions.Contract("inconsistent spatial access counts");
            }
            var originKnown = false;
            DomainCell projectedOrigin = default;
            if (row.ProjectedOrigin is not null)
            {
                if (!BridgeFunctions.TryProtoCell(row.ProjectedOrigin, out var origin) || blockedSet.Contains(origin))
                {
                    throw BridgeFunctions.Contract("invalid projected origin");
                }
                originKnown = true;
                projectedOrigin = origin;
                if ((origin == position && egress != 0) || (origin != position && (egress == 0 || !blockedSet.Contains(position))))
                {
                    throw BridgeFunctions.Contract("projected origin disagrees with egress");
                }
            }
            else if (after != 0 || !blockedSet.Contains(position))
            {
                throw BridgeFunctions.Contract("missing projected origin");
            }
            var accessTargets = new List<AccessTarget>(row.Targets.Count);
            for (var i = 0; i < row.Targets.Count; i++)
            {
                var raw = row.Targets[i];
                var cellOk = BridgeFunctions.TryProtoCell(raw?.Cell, out var cell);
                if (raw is null || !cellOk || cell != targets[i] || raw.NativeReachable is null || raw.ProjectedReachable is null)
                {
                    throw BridgeFunctions.Contract("spatial access target mismatch");
                }
                var projectedReachable = raw.ProjectedReachable.GetValueOrDefault();
                if (projectedReachable != (raw.ProjectedSteps is not null) || (projectedReachable && (!originKnown || blockedSet.Contains(cell))))
                {
                    throw BridgeFunctions.Contract("projected steps disagree with reachability");
                }
                accessTargets.Add(
                    new AccessTarget
                    {
                        Cell = cell,
                        NativeReachable = raw.NativeReachable.GetValueOrDefault(),
                        ProjectedReachable = projectedReachable,
                        ProjectedSteps = raw.ProjectedSteps.GetValueOrDefault(),
                    }
                );
            }
            pawns.Add(
                new PawnAccess
                {
                    Id = pawnId,
                    Position = position,
                    Current = current,
                    After = after,
                    LosesAccess = row.LosesAccess.GetValueOrDefault(),
                    EgressSteps = egress,
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
