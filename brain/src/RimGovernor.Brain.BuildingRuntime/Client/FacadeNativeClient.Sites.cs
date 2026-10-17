using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

// Go excavation_site_read.go, clearance.go and shrines.go: the excavation site, clearance census and ancient shrine reads.
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go's <c>ReadExcavationSite</c>: reads the excavation site for the ordered cell set plus its access cell. Reply
    /// rows echo the request order so callers can index stages positionally.
    /// </summary>
    internal async Task<ExcavationSite> ReadExcavationSiteAsync(PbCommon.Identity identity, IReadOnlyList<Cell> cells, Cell access, CancellationToken cancellationToken)
    {
        if (cells.Count == 0 || !ExcavationSiteRules.ExcavationCellValid(access))
        {
            throw BridgeContractException.Contract("invalid excavation site request");
        }
        var requested = new List<PbCommon.Cell>(cells.Count);
        var seen = new HashSet<Cell>();
        foreach (var cell in cells)
        {
            if (!ExcavationSiteRules.ExcavationCellValid(cell) || !seen.Add(cell))
            {
                throw BridgeContractException.Contract("invalid excavation site request");
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
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        var snapshot = reply.Outcome.Observed!;
        var items = new List<ExcavationSiteCell>(cells.Count);
        for (var i = 0; i < snapshot.Cells.Count; i++)
        {
            var row = snapshot.Cells[i];
            items.Add(
                new ExcavationSiteCell
                {
                    Cell = cells[i],
                    Fogged = row.Fogged.GetValueOrDefault(),
                    Definition = row.MineableDefName ?? "",
                    Walkable = row.Walkable.GetValueOrDefault(),
                    MineDesignated = row.MineDesignated.GetValueOrDefault(),
                    Eligible = row.Eligible.GetValueOrDefault(),
                    Blocker = row.Blocker ?? "",
                }
            );
        }
        var support = snapshot.SupportAfterRemoval switch
        {
            PbObservations.ExcavationSupport.Supported => ExcavationSupport.Supported,
            PbObservations.ExcavationSupport.Unsupported => ExcavationSupport.Unsupported,
            _ => ExcavationSupport.Unknown,
        };
        return new ExcavationSite
        {
            Context = snapshot.Context,
            Cells = items,
            Support = support,
            RoofCellsChecked = snapshot.RoofCellsChecked.GetValueOrDefault(),
            SupportBlocker = snapshot.SupportBlocker ?? "",
            CollapsePending = snapshot.CollapsePending.GetValueOrDefault(),
            WorkerAvailable = snapshot.WorkerAvailable.GetValueOrDefault(),
            Workers = [.. snapshot.WorkerIds],
            AccessReachable = snapshot.AccessReachable.GetValueOrDefault(),
        };
    }

    /// <summary>Go's <c>ReadClearanceTargets</c>: read-only and requires no authority. An unsupported native stub is unavailable, never a successful empty census.</summary>
    internal Task<PbObservations.ClearanceTargetsReply> ReadClearanceTargetsAsync(PbCommon.Identity identity, bool includeSalvage, CancellationToken cancellationToken) =>
        ReadClearanceTargetsOnGroundAsync(identity, includeSalvage, [], cancellationToken);

    /// <summary>
    /// Go's <c>ReadClearanceTargetsOnGround</c>: widens the census to planned ground: on those rectangles native also
    /// reports the player's own buildings (with encloses_room) and constructed floors, one row per cell.
    /// </summary>
    internal async Task<PbObservations.ClearanceTargetsReply> ReadClearanceTargetsOnGroundAsync(PbCommon.Identity identity, bool includeSalvage, IReadOnlyList<PbObservations.Rectangle> planned, CancellationToken cancellationToken)
    {
        foreach (var rect in planned)
        {
            if (!PlacementReads.RectangleValid(rect, 4096))
            {
                throw BridgeContractException.Contract("invalid planned ground rectangle");
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
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        return reply;
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
    }
}
