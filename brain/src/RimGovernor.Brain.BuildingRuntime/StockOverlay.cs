namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// Pushes the stockpile shortfall tint after a review, gated by the layout overlay flag like the other
    /// layers: every stockpile the zone census lists, tinted by the review's resource targets and the food
    /// runway, sent when it changes or an hour passed, and cleared once with the flag off or no stockpile.
    /// Output only: a failure is dropped and the next review draws again.
    /// </summary>
    internal void DrawStockOverlay(CancellationToken ct, GenerationSnapshot snapshot, Observation.ColonyProjection projection, Sto.RoundsResult result)
    {
        if (Native is not ILayoutOverlayNative native)
        {
            return;
        }
        var layer = new Pol.LayoutOverlay();
        if (LayoutOverlay && projection.Zones.Complete)
        {
            IReadOnlyList<Pol.StockZone> zones;
            try
            {
                zones = StockZones(ct, snapshot, projection);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return;
            }
            var f = projection.Facts;
            var levels = new Pol.StockLevels
            {
                Targets = result.Needs.ResourceDemand.Needs,
                Stock = new Dictionary<Pol.Resource, long>(),
                FoodDays = f.FoodDays,
                FoodTargetDays = Seasonal(f).FoodTargetDays,
            };
            var census = new Pol.StockReader { Resources = f.Resources, Wood = f.Wood }.Census(levels.Targets);
            if (census.TryGetValue(out var rows))
            {
                foreach (var row in rows)
                {
                    levels.Stock[row.Resource] = levels.Stock.GetValueOrDefault(row.Resource) + row.Count;
                }
            }
            layer = Pol.LayoutOverlay.StockOverlay(zones, levels, projection.Bounds);
        }
        var on = layer.Layers.Count > 0 || layer.Labels.Count > 0;
        var tick = projection.Identity.Tick;
        if (!on)
        {
            if (!Stock.Cleared)
            {
                try
                {
                    native.DrawOverlay(ct, ControlIdentity(snapshot), Pol.LayoutOverlay.StockLayer, new Pol.LayoutOverlay(), false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return;
                }
                Stock = new OverlayState { Cleared = true };
            }
            return;
        }
        if (OverlayCurrent(Stock, layer, tick))
        {
            return;
        }
        try
        {
            native.DrawOverlay(ct, ControlIdentity(snapshot), Pol.LayoutOverlay.StockLayer, layer, true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }
        Stock = new OverlayState { Layer = layer, Drawn = tick };
    }

    /// <summary>
    /// Every stockpile the zone census lists, its cells from the projection's planning cells (else its census
    /// bounds), with the colony's claim role and settings, the latest patch superseding them, when the colony
    /// created it.
    /// </summary>
    internal IReadOnlyList<Pol.StockZone> StockZones(CancellationToken ct, GenerationSnapshot snapshot, Observation.ColonyProjection projection)
    {
        var tick = projection.Identity.Tick;
        var claims = Player.Journal.ZoneClaims(ct, snapshot, tick);
        var patches = Player.Journal.StockpilePatches(ct, snapshot, tick);
        var owned = new Dictionary<string, Sto.OwnedZone>(StringComparer.Ordinal);
        if (claims.TryGetValue(out var claimRows))
        {
            foreach (var z in claimRows)
            {
                owned[z.Id] = z;
            }
        }
        var cells = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        foreach (var cell in projection.Cells)
        {
            if (cell.ZoneId.TryGetValue(out var id) && id.Length > 0)
            {
                if (!cells.TryGetValue(id, out var list))
                {
                    cells[id] = list = [];
                }
                list.Add(cell.Cell);
            }
        }
        var output = new List<Pol.StockZone>();
        foreach (var row in projection.Zones.Value.Rows)
        {
            if (row.Type != "stockpile")
            {
                continue;
            }
            var z = new Pol.StockZone
            {
                Id = row.Id,
                Label = row.Label,
                FoodStorage = row.FoodStorage,
                Cells = cells.TryGetValue(row.Id, out var own) ? [.. own] : [],
            };
            if (owned.TryGetValue(z.Id, out var claim) && claim.Kind == ZoneKind.Stockpile)
            {
                z = z with { Role = claim.Role, Filter = Fact.Known(claim.Filter) };
                if (patches.TryGetValue(z.Id, out var patch) && patch.Kind == StorageTargetKind.Zone)
                {
                    z = z with { Filter = Fact.Known(patch.Filter) };
                    if (patch.Role.Length > 0)
                    {
                        z = z with { Role = patch.Role };
                    }
                }
            }
            if (row.Bounds is { } b && z.Cells.Count == 0)
            {
                var all = new List<Cell>();
                for (var x = b.Minimum.X; x <= b.Maximum.X; x++)
                {
                    for (var zz = b.Minimum.Z; zz <= b.Maximum.Z; zz++)
                    {
                        all.Add(new Cell(x, zz));
                    }
                }
                z = z with { Cells = [.. all] };
            }
            output.Add(z);
        }
        return output;
    }
}
