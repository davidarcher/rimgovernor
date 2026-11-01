namespace RimGovernor.Brain.Runtime;

public sealed partial class DefenseLayoutPlanner
{
    /// <summary>The policy request of the site census: the turret and mortar budgets, the defenders, the cover choice and the IEDs.</summary>
    internal async Task<(DefenseRequest Request, Verdict Held, bool Ok)> DefenseCensusRequestAsync(ControlState state, ReviewReading read, DefenseKillbox killbox, BridgeCellRect region, Cell home, CancellationToken call)
    {
        var projection = read.Reading;
        var identity = Boundary.Identity(state.Snapshot);
        var site = await DefenseSiteAsync(identity, projection, region, call).ConfigureAwait(false);
        var request = DefenseTurretRequest(read);
        var mortar = DefenseMortarRequest(read);
        var unitCosts = new Dictionary<string, EquatableArray<Amount>>(request.UnitCosts)
        {
            [DefenseMortarDefinition] = mortar.UnitCosts.GetValueOrDefault(DefenseMortarDefinition),
        };
        var entrances = new List<Cell>(request.Entrances);
        foreach (var cell in site)
        {
            if (cell is RimGovernor.Brain.GameFacts.Observations.DefenseCell.Seen { Edifice: { Door: true, PlayerOwned: true } })
            {
                entrances.Add(cell.Cell);
            }
        }
        request = request with
        {
            Mortar = mortar.Mortar,
            UnitCosts = unitCosts,
            Bounds = projection.Facts.MapBounds,
            Home = home,
            Killbox = killbox,
            Arrivals = EquatableArray.From(DefenseArrivals(projection.Snapshot.Threat.Observed.Raids, region)),
            Tick = projection.Identity.Tick,
            Region = new Rectangle(region.Min.X, region.Min.Z, region.Max.X - region.Min.X + 1, region.Max.Z - region.Min.Z + 1),
            Entrances = [.. entrances],
        };
        var (defenders, minRange, ok) = await DefenderRangeAsync(state, read, call).ConfigureAwait(false);
        if (!ok)
        {
            return (new DefenseRequest(), NoWorker("ranged_defender"), false);
        }
        request = request with
        {
            Defenders = defenders,
            MinRange = minRange,
            Definitions = DefenseCoverStock.DefenseCoverChoice(projection.Facts.Items, request.Definitions, ResourceStockRules.Count(projection.Snapshot.Resources),
                DefenseDefinitionAvailable(read, DefenseCoverStock.DefenseSandbags), DefenseDefinitionAvailable(read, DefenseCoverStock.DefenseEmbrasure), defenders),
        };
        request = DefenseIedRequest(read, request);
        var (cells, fogged) = await DefenseSiteCellsAsync(identity, request.Definitions, site, call).ConfigureAwait(false);
        request = request with { Cells = [.. cells], Fogged = [.. fogged] };
        return (request, default, true);
    }

}
