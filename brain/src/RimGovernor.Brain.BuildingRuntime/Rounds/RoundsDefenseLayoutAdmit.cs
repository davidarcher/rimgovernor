using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const long DefenseWallStone = 5;
    internal const long DefenseDoorStone = 25;

    internal RoundsDefenseLayoutResult Admit(CancellationToken call, CancellationToken epoch, StandardState goal, ControlState state, RoundsReading read, DefenseLayoutRecord record, DefenseTierRecord tier, List<Building> buildings, MethodId key, DefensePipeline pipe)
    {
        var p = _reviewer.Player;
        var projection = read.Projection;
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = 1 };
        var actions = new List<DomainAction>();
        var previews = new List<Preview>();
        var stock = new StockObservation { Snapshot = snapshot, Tick = projection.Identity.Tick };
        var unfloorable = new HashSet<Cell>();
        var placed = 0;
        var candidates = new List<DomainAction>(buildings.Count);
        var promoted = DefensePromoted(read.Projection);
        var roamer = DefenseRoamerOwned(read.Projection);
        for (var i = 0; i < buildings.Count; i++)
        {
            candidates.Add(DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), buildings[i], PolicyApi.DefenseBuildTier(promoted, PolicyApi.IsCorePerimeterTier(tier.Name), roamer)));
        }
        IReadOnlyList<BridgeBuildingPreview> evaluated = [];
        if (candidates.Count > 0)
        {
            (evaluated, _) = _native.PreviewBuildings(candidates, snapshot, call);
        }
        for (var i = 0; i < buildings.Count; i++)
        {
            var building = buildings[i];
            var action = candidates[i];
            var (preview, ok) = ClassifyDefensePreview(evaluated[i], building.Cell);
            if (!ok && preview.NativeWorkPending)
            {
                if (PolicyApi.IsPerimeterTier(tier.Name))
                {
                    placed++;
                    continue;
                }
                return new RoundsDefenseLayoutResult { Verdict = BuildingReasonExistingWork, Tier = tier.Name, NativeWorkTicks = DefenseNativeWorkTicks };
            }
            if (!ok && ((tier.Name == PolicyApi.TierFiringLine && building.Definition == DefenseDefinitionNames.Floor) || PolicyApi.IsPerimeterTier(tier.Name) || tier.Name == PolicyApi.TierIeds))
            {
                unfloorable.Add(building.Cell);
                continue;
            }
            if (!ok)
            {
                var cell = building.Cell;
                DefenseAction(call, "defense-layout", LogLevel.Warn, "refused", "placement_preview_refused", tier.Name, new Dictionary<string, object?> { ["definition"] = building.Definition, ["x"] = cell.X, ["z"] = cell.Z, ["native_reason"] = evaluated[i].Reason });
                return new RoundsDefenseLayoutResult { Verdict = FieldUnavailable("placement_preview"), Tier = tier.Name };
            }
            actions.Add(action);
            previews.Add(preview.Preview);
            MergeRoundsStock(stock, preview.Stock, actions.Count == 1);
        }
        if (actions.Count == 0 && placed > 0 && unfloorable.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonExistingWork, Tier = tier.Name, NativeWorkTicks = DefenseNativeWorkTicks };
        }
        if (unfloorable.Count > 0)
        {
            tier.Buildings = DefenseWithoutFloors(tier.Buildings, unfloorable, PolicyApi.IsPerimeterTier(tier.Name) || tier.Name == PolicyApi.TierIeds);
            record.SetTier(tier);
            DefenseAction(call, "defense-layout", LogLevel.Info, "refused", "placement_refused_natively", tier.Name, new Dictionary<string, object?> { ["cells"] = unfloorable.Count });
            p.Journal.SaveDefenseLayout(record, call);
            if (actions.Count == 0)
            {
                return new RoundsDefenseLayoutResult { Verdict = NoSpace("defense_tier"), Tier = tier.Name };
            }
        }
        var blocked = new HashSet<Cell>();
        var blockedCells = new List<Cell>();
        foreach (var t in record.Tiers)
        {
            foreach (var b in t.Buildings)
            {
                if (!DefenseAuditBlocks(b.Definition))
                {
                    continue;
                }
                if (blocked.Add(b.Cell))
                {
                    blockedCells.Add(b.Cell);
                }
            }
        }
        if (!PolicyApi.IsPerimeterTier(tier.Name))
        {
            var (killbox, _, _, kok) = DefenseKillbox(projection);
            if (!kok)
            {
                return new RoundsDefenseLayoutResult { Verdict = AwaitingPlan("layout_plan", "killbox"), Tier = tier.Name };
            }
            var targets = new List<Cell> { killbox.Entry };
            targets.AddRange(record.Entrances);
            BridgeSpatialAccess access;
            try
            {
                (access, _) = _native.ReadSpatialAccess(BoundaryApi.Identity(state.Snapshot), blockedCells, targets, null, call);
            }
            catch (NativeUnavailableException)
            {
                return new RoundsDefenseLayoutResult { Verdict = FieldUnavailable("spatial_access"), Tier = tier.Name };
            }
            SameTick(access.Context, state, projection.Identity.Tick);
            var (skippedId, skipped) = access.Skipped();
            if (skipped)
            {
                DefenseAction(call, "defense-layout", LogLevel.Info, "unavailable", "access_audit", tier.Name, new Dictionary<string, object?> { ["blocked_cells"] = blockedCells.Count, ["skipped_pawn"] = skippedId });
                return new RoundsDefenseLayoutResult { Verdict = FieldUnavailable("spatial_access_skipped"), Tier = tier.Name };
            }
            var refusal = access.Refusal();
            if (refusal != "")
            {
                DefenseAction(call, "defense-layout", LogLevel.Info, "refused", "access_audit", tier.Name, new Dictionary<string, object?> { ["blocked_cells"] = blockedCells.Count, ["detail"] = refusal });
                return new RoundsDefenseLayoutResult { Verdict = NoSpace("walkable_layout"), Tier = tier.Name };
            }
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw DefenseControlErr(358);
        }
        if (!pipe.Scoped)
        {
            try
            {
                pipe.Scope = StepScope(call, _reviewer.Native);
            }
            catch (ControlException)
            {
                throw DefenseControlErr(366);
            }
            pipe.Scoped = true;
        }
        var actual = pipe.Scope;
        if (!RoundsBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick))
        {
            throw DefenseControlErr(366);
        }
        var now = _reviewer.Clock.Now();
        if (now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new StaleObservationException();
        }
        var decision = AdmitMethod(call, p.Journal, new BuildingMethodRequest
        {
            Owner = goal,
            Method = key,
            Plan = plan,
            Current = snapshot,
            Tick = projection.Identity.Tick,
            Bounds = Known(projection.Bounds),
            Stock = stock,
            Previews = previews,
            Purpose = PolicyApi.Defense,
        });
        var reason = AdmissionRefused(decision);
        if (decision.Admitted)
        {
            reason = BuildingReasonAdmitted;
            tier.Attempts++;
            record.SetTier(tier);
            pipe.Admitted++;
            foreach (var action in actions)
            {
                if (action.Building(out var b))
                {
                    pipe.Cells.Add(b.Cell);
                }
            }
            p.Journal.SaveDefenseLayout(record, call);
        }
        return new RoundsDefenseLayoutResult { Verdict = reason, Plan = id, Tier = tier.Name };
    }

    internal static List<DefenseBuilding> DefenseWithoutFloors(IReadOnlyList<DefenseBuilding> buildings, ISet<Cell> cells, bool perimeter)
    {
        var kept = new List<DefenseBuilding>(buildings.Count);
        foreach (var b in buildings)
        {
            if ((b.Definition != DefenseDefinitionNames.Floor && !perimeter) || !cells.Contains(b.Cell))
            {
                kept.Add(b);
            }
        }
        return kept;
    }

    internal static ControlException DefenseControlErr(int line) =>
        new(string.Create(CultureInfo.InvariantCulture, $"control error (defense-layout:{line})"));

    internal void SameTick(C.ObservationContext observed, ControlState state, Tick tick)
    {
        try
        {
            var current = BoundaryApi.Context(observed, state.Snapshot);
            if (current.Native != state.Snapshot.Native || new Tick(observed.Tick) < tick)
            {
                throw DefenseControlErr(388);
            }
        }
        catch (ControlException)
        {
            throw DefenseControlErr(388);
        }
    }

    internal (BridgeBuildingPreview Preview, bool Ok) Preview(CancellationToken ct, DomainAction action, GenerationSnapshot snapshot, Tick _, Cell cell)
    {
        var (previews, _) = _native.PreviewBuildings([action], snapshot, ct);
        return ClassifyDefensePreview(previews[0], cell);
    }

    internal static (BridgeBuildingPreview Preview, bool Ok) ClassifyDefensePreview(BridgeBuildingPreview preview, Cell cell)
    {
        var v = preview.Preview;
        var fk = v.Footprint.TryGetValue(out var footprint);
        var lk = v.CanPlace.TryGetValue(out var legal);
        var sk = v.SafeToPlace.TryGetValue(out var safe);
        var mk = v.MadeFromStuff.IsKnown;
        if (!fk || !mk || !lk || !sk)
        {
            return (new BridgeBuildingPreview(), false);
        }
        if (footprint!.Count != 1 || footprint[0] != cell || !legal || !safe)
        {
            return (preview, false);
        }
        return (preview, true);
    }

    internal static string DefensePerimeterBridge(ColonyProjection projection)
    {
        if (projection.Facts.Research.TryGetValue(out var research) && research.Finished.Contains(new ResearchProjectId(PolicyApi.PerimeterHeavyResearch)))
        {
            return PolicyApi.PerimeterHeavyBridge;
        }
        return PolicyApi.PerimeterBridge;
    }

    internal static bool DefenseStoneBuilding(Building b) =>
        b.Stuff == "" && (b.Definition == DefenseDefinitionNames.Wall || b.Definition == DefenseDefinitionNames.Door);

    internal static bool DefenseNeedsStone(IReadOnlyList<Building> buildings) => buildings.Any(DefenseStoneBuilding);

    internal static bool DefenseAuditBlocks(string definition)
    {
        if (definition == DefenseDefinitionNames.Trap || definition == DefenseDefinitionNames.Fence || definition == DefenseDefinitionNames.Sandbag
            || definition == DefenseDefinitionNames.Door || definition == DefenseDefinitionNames.Bait || definition == DefenseConduitDefinition)
        {
            return false;
        }
        return !DefenseTerrain(definition);
    }

    internal static bool DefenseTerrain(string definition) =>
        definition == DefenseDefinitionNames.Floor || definition == PolicyApi.PerimeterBridge || definition == PolicyApi.PerimeterHeavyBridge;

    /// <summary>The stone block resource with the most stock, when it covers the stone the buildings need.</summary>
    internal static (string Stuff, bool Ok) DefensePerimeterStone(ColonyProjection projection, IReadOnlyList<Building> buildings)
    {
        if (!projection.Resources.TryGetValue(out var stock))
        {
            return ("", false);
        }
        long need = 0;
        foreach (var b in buildings)
        {
            if (!DefenseStoneBuilding(b))
            {
                continue;
            }
            need += b.Definition == DefenseDefinitionNames.Door ? DefenseDoorStone : DefenseWallStone;
        }
        var best = "";
        long most = 0;
        foreach (var (resource, count) in stock)
        {
            var name = resource.Value;
            if (projection.Facts.Items.IsStoneBlocks(resource) && (count > most || (count == most && string.CompareOrdinal(name, best) < 0)))
            {
                best = name;
                most = count;
            }
        }
        return (best, best != "" && most >= need);
    }

    internal static (DefenseKillbox Killbox, BridgeCellRect Region, Cell Home, bool Ok) DefenseKillbox(ColonyProjection projection)
    {
        if (!projection.LayoutPlan.TryGetValue(out var plan))
        {
            return (new DefenseKillbox(), new BridgeCellRect(), default, false);
        }
        var (k, region, home, ok) = PolicyApi.LayoutKillbox(plan, projection.Bounds);
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
            if (section != "" && tier.Name == section && tier.Buildings.Count > 0)
            {
                min = tier.Buildings[0].Cell;
                max = tier.Buildings[0].Cell;
            }
        }
        foreach (var tier in record.Tiers)
        {
            if (tier.Name != section && (section != "" || PolicyApi.IsPerimeterTier(tier.Name)))
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
            if (!PolicyApi.IsPerimeterTier(tier.Name) || tier.Buildings.Count == 0)
            {
                continue;
            }
            var r = DefenseRecordRegion(record, bounds, baseRegion, tier.Name);
            output = new BridgeCellRect
            {
                Min = new Cell(Math.Min(output.Min.X, r.Min.X), Math.Min(output.Min.Z, r.Min.Z)),
                Max = new Cell(Math.Max(output.Max.X, r.Max.X), Math.Max(output.Max.Z, r.Max.Z)),
            };
        }
        return output;
    }

    internal static DefenseCell DefenseCellFacts(BridgeDefenseCell cell)
    {
        var output = new DefenseCell { Cell = cell.Cell };
        if (cell.Fogged)
        {
            return output;
        }
        output.Walkable = Known(cell.Walkable);
        output.Passable = Known(cell.Passable);
        output.PlayerOwned = Known(cell.PlayerOwned);
        output.NaturalRock = Known(cell.NaturalRock);
        output.EdgeReachable = Known(cell.EdgeReachable);
        output.Door = Known(cell.Door);
        output.CoverFill = Known(cell.CoverFill);
        output.Edifice = cell.EdificeDefName;
        output.Roofed = Known(cell.Roofed);
        output.Cover = DefenseCover(cell);
        return output;
    }

    /// <summary>How many defenders hold a ranged weapon (capped at eight) and the shortest reach among them.</summary>
    internal static (int Count, Fact<double> Shortest) DefenderRangeOf(IReadOnlyList<O.PawnState> rows, Armament arms)
    {
        var count = 0;
        var shortest = double.PositiveInfinity;
        foreach (var row in rows)
        {
            var reach = arms.PrimaryRange(row.Equipment);
            if (reach <= 0)
            {
                continue;
            }
            count++;
            shortest = Math.Min(shortest, reach);
        }
        if (count == 0)
        {
            return (0, Unknown<double>());
        }
        if (count > 8)
        {
            count = 8;
        }
        return (count, Known(shortest));
    }

    internal static void DefenseIedRequest(RoundsReading read, DefenseRequest request)
    {
        var projection = read.Projection;
        request.IedMax = PolicyApi.IedBudget(projection.Facts.RaidPoints);
        request.IedStock = projection.Resources;
        foreach (var d in projection.Definitions)
        {
            if (d.Name != DefenseIedHighExplosive && d.Name != DefenseIedIncendiary)
            {
                continue;
            }
            if (!d.ExplosiveRadius.TryGetValue(out var radius) || !DefenseDefinitionAvailable(read, d.Name))
            {
                continue;
            }
            request.Ieds.Add(new DefenseIed { Definition = d.Name, Radius = radius, Incendiary = d.Name == DefenseIedIncendiary });
            var (price, ok) = DefenseUnitPrice(projection, d);
            if (ok)
            {
                request.UnitCosts[d.Name] = price.Costs;
            }
        }
        request.Ieds.Sort((a, b) => string.CompareOrdinal(a.Definition, b.Definition));
        if (!projection.Zones.Complete)
        {
            return;
        }
        var stockpiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in projection.Zones.Value.Rows)
        {
            if (row.Type == "stockpile")
            {
                stockpiles.Add(row.Id);
            }
        }
        var storage = new List<Cell>();
        foreach (var cell in projection.Cells)
        {
            if (cell.ZoneId.TryGetValue(out var id) && stockpiles.Contains(id))
            {
                storage.Add(cell.Cell);
            }
        }
        request.FlammableStorage = Known<IReadOnlyList<Cell>>(storage);
    }

    internal void LogUnbuiltTiers(CancellationToken ct, DefenseLayoutRecord record, DefenseCensus census)
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
            if (Unbuilt.TryGetValue(tier.Name, out var was) && was == summary)
            {
                continue;
            }
            Unbuilt[tier.Name] = summary;
            DefenseAction(ct, "defense-layout", LogLevel.Info, "waiting", "tier_unbuilt", tier.Name, new Dictionary<string, object?> { ["buildings"] = tier.Buildings.Count, ["census"] = summary });
        }
    }

    internal BridgeDefenseSite DefenseSite(CancellationToken call, C.Identity identity, ColonyProjection projection, BridgeCellRect region)
    {
        var catalog = PawnCatalog(call, _native, identity);
        return BridgeApi.DefenseSiteFromCells(projection.Cells, projection.Bounds, region, catalog);
    }
}
