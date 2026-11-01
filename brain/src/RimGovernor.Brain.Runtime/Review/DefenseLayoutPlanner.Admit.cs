using System.Globalization;

namespace RimGovernor.Brain.Runtime;

public sealed partial class DefenseLayoutPlanner
{
    internal static ControlException DefenseControlErr(int line) =>
        new(string.Create(CultureInfo.InvariantCulture, $"control error (defense-layout:{line})"));

    internal static void SameTick(RimGovernor.Brain.GameFacts.Common.ObservationContext? observed, ControlState state, Tick tick)
    {
        try
        {
            var current = Boundary.Context(observed, state.Snapshot);
            if (current.Native != state.Snapshot.Native || new Tick(observed?.Tick ?? 0) < tick)
            {
                throw DefenseControlErr(388);
            }
        }
        catch (ControlException)
        {
            throw DefenseControlErr(388);
        }
    }

    internal async Task<(BridgeBuildingPreview Preview, bool Ok)> PreviewAsync(DomainAction action, GenerationSnapshot snapshot, Bounds size, CancellationToken ct)
    {
        var previews = await _native.PreviewBuildingsAsync([action], snapshot, ct).ConfigureAwait(false);
        return ClassifyDefensePreview(previews[0], action.Building!, size);
    }

    /// <summary>The North size of <paramref name="definition"/> as read; an unread size is its anchor cell alone, so a larger native footprint refuses.</summary>
    internal static Bounds DefenseSize(ColonyReading projection, string definition) =>
        projection.Facts.Definitions.FirstOrDefault(d => d.Name == definition) is { } d && d.Size.Width > 0 && d.Size.Height > 0 ? d.Size : new Bounds(1, 1);

    /// <summary>
    /// The preview places <paramref name="b"/>: native can and may place it, its claimed cells (occupied and interaction)
    /// covering the whole footprint its definition's <paramref name="size"/> occupies at its cell and rotation
    /// (GenAdj.OccupiedRect), so a multi-cell piece is told from a displaced or truncated one.
    /// </summary>
    internal static (BridgeBuildingPreview Preview, bool Ok) ClassifyDefensePreview(BridgeBuildingPreview preview, Building b, Bounds size)
    {
        var v = preview.Preview;
        var rect = InteriorRules.OccupiedRect(b.Cell, new Cell(size.Width, size.Height), b.Rotation);
        var want = new HashSet<Cell>();
        for (var x = rect.X; x < rect.X + rect.Width; x++)
        {
            for (var z = rect.Z; z < rect.Z + rect.Height; z++)
            {
                want.Add(new Cell(x, z));
            }
        }
        if (want.Count == 0 || !want.IsSubsetOf(v.Footprint) || !v.CanPlace || !v.SafeToPlace)
        {
            return (preview, false);
        }
        return (preview, true);
    }

    internal static string DefensePerimeterBridge(ColonyReading projection)
    {
        if (projection.Facts.Research.Finished.Contains(new ResearchProjectId(LayoutDefenseRules.PerimeterHeavyResearch)))
        {
            return LayoutDefenseRules.PerimeterHeavyBridge;
        }
        return LayoutDefenseRules.PerimeterBridge;
    }

    internal static bool DefenseTerrain(string definition) =>
        definition == DefenseDefinitionNames.Floor || definition == LayoutDefenseRules.PerimeterBridge || definition == LayoutDefenseRules.PerimeterHeavyBridge;

    internal static (DefenseKillbox Killbox, BridgeCellRect Region, Cell Home, bool Ok) DefenseKillbox(ColonyReading projection) =>
        projection.Facts.LayoutPlan is { } plan ? DefenseKillboxOf(plan, projection.Facts.MapBounds) : (new DefenseKillbox(), new BridgeCellRect(), default, false);

    /// <summary>The killbox opening of plan (the stored plan, or a traced candidate's), its census region and the colony cell it leads to.</summary>
    internal static (DefenseKillbox Killbox, BridgeCellRect Region, Cell Home, bool Ok) DefenseKillboxOf(LayoutPlan plan, Bounds bounds)
    {
        var (k, region, home, ok) = LayoutPlan.LayoutKillbox(plan, bounds);
        if (!ok)
        {
            return (new DefenseKillbox(), new BridgeCellRect(), default, false);
        }
        return (k, new BridgeCellRect { Min = new Cell(region.X, region.Z), Max = new Cell(region.X + region.Width - 1, region.Z + region.Height - 1) }, home, true);
    }

    /// <summary>The bounding box of a record's cells: the whole record when the section is empty, the perimeter tiers excluded unless named.</summary>
    internal static BridgeCellRect DefenseRecordRegion(DefenseLayoutRecord record, Bounds bounds, BridgeCellRect baseRegion, string section)
    {
        var min = baseRegion.Min;
        var max = baseRegion.Max;
        foreach (var tier in record.Tiers)
        {
            if (section != "" && tier.Name.Value == section && tier.Buildings.Count > 0)
            {
                min = tier.Buildings[0].Cell;
                max = tier.Buildings[0].Cell;
            }
        }
        foreach (var tier in record.Tiers)
        {
            if (tier.Name.Value != section && (section != "" || LayoutDefenseRules.IsPerimeterTier(tier.Name)))
            {
                continue;
            }
            foreach (var b in tier.Buildings)
            {
                min = new Cell(Math.Min(min.X, b.Cell.X), Math.Min(min.Z, b.Cell.Z));
                max = new Cell(Math.Max(max.X, b.Cell.X), Math.Max(max.Z, b.Cell.Z));
            }
        }
        static int Clamp(int v, int hi) => Math.Max(0, Math.Min(v, hi));
        return new BridgeCellRect
        {
            Min = new Cell(Clamp(min.X - 1, bounds.Width - 1), Clamp(min.Z - 1, bounds.Height - 1)),
            Max = new Cell(Clamp(max.X + 1, bounds.Width - 1), Clamp(max.Z + 1, bounds.Height - 1)),
        };
    }

    internal static BridgeCellRect DefenseCensusRegion(DefenseLayoutRecord record, Bounds bounds, BridgeCellRect baseRegion)
    {
        var output = DefenseRecordRegion(record, bounds, baseRegion, "");
        foreach (var tier in record.Tiers)
        {
            if (!LayoutDefenseRules.IsPerimeterTier(tier.Name) || tier.Buildings.Count == 0)
            {
                continue;
            }
            var r = DefenseRecordRegion(record, bounds, baseRegion, tier.Name.Value);
            output = new BridgeCellRect
            {
                Min = new Cell(Math.Min(output.Min.X, r.Min.X), Math.Min(output.Min.Z, r.Min.Z)),
                Max = new Cell(Math.Max(output.Max.X, r.Max.X), Math.Max(output.Max.Z, r.Max.Z)),
            };
        }
        return output;
    }

    /// <summary>A seen census row as the policy cell, with the footing the catalog gives its terrain.</summary>
    internal static DefenseCell DefenseCellFacts(RimGovernor.Brain.GameFacts.Observations.DefenseCell.Seen seen, bool wallSupport, bool trapSupport)
    {
        var edifice = seen.Edifice;
        return new DefenseCell
        {
            Cell = seen.Cell,
            Walkable = seen.Walkable,
            Passable = seen.Passable,
            PlayerOwned = edifice is { PlayerOwned: true },
            NaturalRock = edifice is { NaturalRock: true },
            EdgeReachable = seen.EdgeReachable,
            Door = edifice is { Door: true },
            CoverFill = seen.CoverFill,
            Edifice = edifice?.DefName ?? "",
            Roofed = seen.Roofed,
            WallSupport = wallSupport,
            TrapSupport = trapSupport,
        };
    }

    internal static DefenseRequest DefenseIedRequest(ReviewReading read, DefenseRequest request)
    {
        var projection = read.Reading;
        request = request with { IEDMax = DefenseIEDs.IEDBudget(RaidDefense.Of(projection.Facts), projection.Facts.EssentialInputs.Power.Buildings.Where(b => b.Turret is not null).ToArray(), projection.Facts.GoalBuild.Turrets), IEDStock = ResourceStockRules.Count(projection.Snapshot.Resources) };
        var (ieds, unitCosts) = DefenseIEDs.Options(projection.Facts, ResourceStockRules.Count(projection.Snapshot.Resources), request.UnitCosts);
        request = request with { UnitCosts = unitCosts };
        // An IED is sited clear of flammable storage, so a round whose zones are not read whole places none.
        if (projection.Zones is not { Complete: true } zones)
        {
            return request;
        }
        var stockpiles = zones.Value.Zones.OfType<RimGovernor.Brain.GameFacts.Observations.ZoneState.Stockpile>().Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var storage = new List<Cell>();
        foreach (var cell in projection.PlanningWindow.Cells)
        {
            if (cell.ZoneId is { } id && stockpiles.Contains(id))
            {
                storage.Add(cell.Cell);
            }
        }
        return request with { IEDs = [.. ieds], FlammableStorage = [.. storage] };
    }

    internal void LogUnbuiltTiers(DefenseLayoutRecord record, DefenseCensus census, CancellationToken ct)
    {
        foreach (var tier in record.Tiers)
        {
            if (tier.Built || tier.Remove || tier.Buildings.Count == 0)
            {
                continue;
            }
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
            foreach (var b in tier.Buildings)
            {
                if (census.Standing(b.Definition, b.Cell))
                {
                    Count("standing");
                }
                else if (census.Fogged.GetValueOrDefault(b.Cell))
                {
                    Count("fogged");
                }
                else
                {
                    Count("found:" + census.Edifice.GetValueOrDefault(b.Cell, ""));
                }
            }
            var summary = "map[" + string.Join(' ', counts.Select(kv => $"{kv.Key}:{kv.Value}")) + "]";
            if (Unbuilt.TryGetValue(tier.Name.Value, out var was) && was == summary)
            {
                continue;
            }
            Unbuilt[tier.Name.Value] = summary;
            DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "waiting", "tier_unbuilt", tier.Name.Value, new Dictionary<string, object?> { ["buildings"] = tier.Buildings.Count, ["census"] = summary }, ct);
        }
    }

    internal async Task<IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.DefenseCell>> DefenseSiteAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, ColonyReading projection, BridgeCellRect region, CancellationToken call)
    {
        var catalog = await EquipPawn.PawnCatalogAsync(_native, identity, call).ConfigureAwait(false) ?? throw new ControlException("defense site: no definition catalog");
        return catalog.DefenseSiteFromCells(projection.PlanningWindow.Cells, projection.Facts.MapBounds, region);
    }
}
