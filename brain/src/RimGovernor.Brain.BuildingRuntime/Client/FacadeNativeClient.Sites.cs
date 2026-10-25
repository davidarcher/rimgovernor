using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

// The excavation site, clearance census and ancient shrine reads.
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Reads the excavation site for the ordered cell set plus its access cell. Reply
    /// rows echo the request order. Throws when the native refuses the request (no cell on the map) or cannot read it.
    /// </summary>
    internal async Task<PbObservations.ExcavationSiteSnapshot> ReadExcavationSiteAsync(PbCommon.Identity identity, IReadOnlyList<Cell> cells, Cell access, CancellationToken cancellationToken)
    {
        static bool OnGrid(Cell cell) => cell.X >= 0 && cell.Z >= 0;
        if (cells.Count == 0 || !OnGrid(access))
        {
            throw BridgeContractException.Contract("invalid excavation site request");
        }
        var requested = new List<RimGovernor.Brain.Domain.Cell>(cells.Count);
        var seen = new HashSet<Cell>();
        foreach (var cell in cells)
        {
            if (!OnGrid(cell) || !seen.Add(cell))
            {
                throw BridgeContractException.Contract("invalid excavation site request");
            }
            requested.Add(new RimGovernor.Brain.Domain.Cell { X = cell.X, Z = cell.Z });
        }
        var request = new PbObservations.ExcavationSiteRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            AccessCell = new RimGovernor.Brain.Domain.Cell { X = access.X, Z = access.Z },
            Cells = [.. requested],
        };
        var site = await Facade.ReadExcavationSiteAsync(request, cancellationToken).ConfigureAwait(false);
        if (site.Cells.Count != cells.Count)
        {
            throw BridgeContractException.Contract("excavation site rows do not echo the request");
        }
        for (var i = 0; i < cells.Count; i++)
        {
            if (site.Cells[i].Cell != cells[i])
            {
                throw BridgeContractException.Contract("excavation site rows do not echo the request");
            }
        }
        return site;
    }

    /// <summary>
    /// The clearance census, read-only and requiring no authority. Non-empty <paramref name="ground"/> widens it: on
    /// those rectangles native also reports the player's own buildings (Player, EnclosesRoom) and constructed
    /// floors, one row per cell. Throws when the native cannot read it.
    /// </summary>
    internal async Task<PbObservations.ClearanceTargetsSnapshot> ReadClearanceAsync(Identity expected, bool includeSalvage, IReadOnlyList<Rectangle> ground, CancellationToken cancellationToken)
    {
        expected.Validate();
        if (!ColonyContexts.SameColonyContext(expected, expected))
        {
            throw new ObservationContractException("expected identity lacks a native generation");
        }
        var planned = new List<PbObservations.Rectangle>(ground.Count);
        foreach (var g in ground)
        {
            var rect = new PbObservations.Rectangle
            {
                Minimum = new RimGovernor.Brain.Domain.Cell { X = g.X, Z = g.Z },
                Maximum = new RimGovernor.Brain.Domain.Cell { X = g.X + g.Width - 1, Z = g.Z + g.Height - 1 },
            };
            if (!PlacementChecks.RectangleValid(rect, 4096))
            {
                throw BridgeContractException.Contract("invalid planned ground rectangle");
            }
            planned.Add(rect);
        }
        var request = new PbObservations.ClearanceTargetsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = new PbCommon.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value } },
            IncludeSalvage = includeSalvage,
            SalvageMaxAgeTicks = SiteBudgets.SalvageMaxAgeTicks,
            SalvageFrameBudgetMs = SiteBudgets.SalvageFrameBudgetMs,
            SalvageInlineBudgetMs = SiteBudgets.SalvageInlineBudgetMs,
            SalvageCensusBudgetMs = SiteBudgets.SalvageCensusBudgetMs,
            PlannedGround = [.. planned],
        };
        var census = await Facade.ReadClearanceTargetsAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ColonyContexts.SameColonyContext(new ObservationIdentity { Context = census.Context }, expected))
        {
            throw new ObservationChangedException();
        }
        return census;
    }

    internal static class SiteBudgets
    {
        // Salvage evidence tunables sent on every clearance read. Native computes
        // about 1 ms of colonist pathing per ruin, so these are main-thread guards: a cached row older than
        // SalvageMaxAgeTicks (four game hours) is recomputed, the frame refresher spends SalvageFrameBudgetMs per
        // update, a read computes rows with no cache entry up to SalvageInlineBudgetMs, and the first read (empty
        // cache) up to SalvageCensusBudgetMs. Rows past the budget carry the skipped salvage case.
        internal const int SalvageMaxAgeTicks = 4 * 2500;
        internal const double SalvageFrameBudgetMs = 1.5;
        internal const double SalvageInlineBudgetMs = 5;
        internal const double SalvageCensusBudgetMs = 250;
    }
}
