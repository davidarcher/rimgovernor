using System.Globalization;

namespace RimGovernor.Brain.Runtime;

public sealed partial class DefenseLayoutPlanner
{
    internal const long DefenseWallStone = 5;
    internal const long DefenseDoorStone = 25;

    internal async Task<DefenseLayoutResult> AdmitAsync(NeedOwner goal, ControlState state, ReviewReading read, DefenseLayoutRecord record, DefenseTierRecord tier, List<Building> buildings, MethodId key, DefensePipeline pipe, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var projection = read.Reading;
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
        var actions = new List<DomainAction>();
        var previews = new List<Preview>();
        var stock = new StockObservation { Snapshot = snapshot, Tick = projection.Identity.Tick };
        var unfloorable = new HashSet<Cell>();
        var placed = 0;
        var candidates = new List<DomainAction>(buildings.Count);
        var promoted = DefensePromoted(read.Reading);
        var roamer = DefenseRoamerOwned(read.Reading);
        for (var i = 0; i < buildings.Count; i++)
        {
            candidates.Add(DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), buildings[i], DefenseTier.DefenseBuildTier(promoted, LayoutPerimeterSitesRules.IsCorePerimeterTier(tier.Name), roamer)));
        }
        IReadOnlyList<BridgeBuildingPreview> evaluated = [];
        if (candidates.Count > 0)
        {
            evaluated = await _native.PreviewBuildingsAsync(candidates, snapshot, call).ConfigureAwait(false);
        }
        for (var i = 0; i < buildings.Count; i++)
        {
            var building = buildings[i];
            var action = candidates[i];
            var (preview, ok) = ClassifyDefensePreview(evaluated[i], building.Cell);
            if (!ok && preview.NativeWorkPending)
            {
                if (LayoutDefenseRules.IsPerimeterTier(tier.Name))
                {
                    placed++;
                    continue;
                }
                return new DefenseLayoutResult { Verdict = BuildingReasonExistingWork, Tier = tier.Name.Value };
            }
            if (!ok && ((tier.Name == DefenseLayout.TierFiringLine && building.Definition == DefenseDefinitionNames.Floor) || LayoutDefenseRules.IsPerimeterTier(tier.Name) || tier.Name == DefenseIEDs.TierIEDs))
            {
                unfloorable.Add(building.Cell);
                continue;
            }
            if (!ok)
            {
                var cell = building.Cell;
                DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Warn, "refused", "placement_preview_refused", tier.Name.Value, new Dictionary<string, object?> { ["definition"] = building.Definition, ["x"] = cell.X, ["z"] = cell.Z, ["native_reason"] = evaluated[i].Reason }, call);
                return new DefenseLayoutResult { Verdict = FieldUnavailable("placement_preview"), Tier = tier.Name.Value };
            }
            actions.Add(action);
            previews.Add(preview.Preview);
            BuildingPlanner.MergeReviewStock(stock, preview.Stock);
        }
        if (actions.Count == 0 && placed > 0 && unfloorable.Count == 0)
        {
            return new DefenseLayoutResult { Verdict = BuildingReasonExistingWork, Tier = tier.Name.Value };
        }
        if (unfloorable.Count > 0)
        {
            tier.Buildings = DefenseWithoutFloors(tier.Buildings, unfloorable, LayoutDefenseRules.IsPerimeterTier(tier.Name) || tier.Name == DefenseIEDs.TierIEDs);
            record.SetTier(tier);
            DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "refused", "placement_refused_natively", tier.Name.Value, new Dictionary<string, object?> { ["cells"] = unfloorable.Count }, call);
            p.Journal.SaveDefenseLayout(record, call);
            if (actions.Count == 0)
            {
                return new DefenseLayoutResult { Verdict = NoSpace("defense_tier"), Tier = tier.Name.Value };
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
        if (!LayoutDefenseRules.IsPerimeterTier(tier.Name))
        {
            var (killbox, _, _, kok) = DefenseKillbox(projection);
            if (!kok)
            {
                return new DefenseLayoutResult { Verdict = AwaitingPlan("layout_plan", "killbox"), Tier = tier.Name.Value };
            }
            var targets = new List<Cell> { killbox.Entry };
            targets.AddRange(record.Entrances);
            RimGovernor.Brain.GameFacts.Observations.SpatialAccessSnapshot access;
            try
            {
                access = await _native.ReadSpatialAccessAsync(Boundary.Identity(state.Snapshot), blockedCells, targets, [], call).ConfigureAwait(false);
            }
            catch (NativeUnavailableException)
            {
                return new DefenseLayoutResult { Verdict = FieldUnavailable("spatial_access"), Tier = tier.Name.Value };
            }
            SameTick(access.Context, state, projection.Identity.Tick);
            var (skippedId, skipped) = access.Skipped();
            if (skipped)
            {
                DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "unavailable", "access_audit", tier.Name.Value, new Dictionary<string, object?> { ["blocked_cells"] = blockedCells.Count, ["skipped_pawn"] = skippedId }, call);
                return new DefenseLayoutResult { Verdict = FieldUnavailable("spatial_access_skipped"), Tier = tier.Name.Value };
            }
            var refusal = access.Refusal();
            if (refusal != "")
            {
                DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "refused", "access_audit", tier.Name.Value, new Dictionary<string, object?> { ["blocked_cells"] = blockedCells.Count, ["detail"] = refusal }, call);
                return new DefenseLayoutResult { Verdict = NoSpace("walkable_layout"), Tier = tier.Name.Value };
            }
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw DefenseControlErr(358);
        }
        if (pipe.Scope is null)
        {
            try
            {
                pipe.Scope = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
            }
            catch (ControlException)
            {
                throw DefenseControlErr(366);
            }
        }
        var actual = pipe.Scope ?? throw DefenseControlErr(366);
        if (!BuildingPlanner.BuildingBoundary(actual, state.Snapshot, projection.Identity.Tick))
        {
            throw DefenseControlErr(366);
        }
        var now = _reviewer.Clock.Now();
        if (now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new RimGovernor.Brain.GameAccess.ObservationStaleException();
        }
        var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
        {
            Owner = goal,
            Method = key,
            Plan = plan,
            Current = snapshot,
            Tick = projection.Identity.Tick,
            Bounds = projection.Facts.MapBounds,
            Stock = stock,
            Previews = previews,
            Purpose = Pol.Purpose.Defense,
        }, call).ConfigureAwait(false);
        var reason = AdmissionLog.AdmissionRefused(decision);
        if (decision.Admitted)
        {
            reason = BuildingReasonAdmitted;
            tier.Attempts++;
            record.SetTier(tier);
            pipe.Admitted++;
            foreach (var action in actions)
            {
                if (action.Building is { } b)
                {
                    pipe.Cells.Add(b.Cell);
                }
            }
            p.Journal.SaveDefenseLayout(record, call);
        }
        return new DefenseLayoutResult { Verdict = reason, Plan = id, Tier = tier.Name.Value };
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

    internal async Task<(BridgeBuildingPreview Preview, bool Ok)> PreviewAsync(DomainAction action, GenerationSnapshot snapshot, Tick _, Cell cell, CancellationToken ct)
    {
        var previews = await _native.PreviewBuildingsAsync([action], snapshot, ct).ConfigureAwait(false);
        return ClassifyDefensePreview(previews[0], cell);
    }

    internal static (BridgeBuildingPreview Preview, bool Ok) ClassifyDefensePreview(BridgeBuildingPreview preview, Cell cell)
    {
        var v = preview.Preview;
        if (v.Footprint.Count != 1 || v.Footprint[0] != cell || !v.CanPlace || !v.SafeToPlace)
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
        definition == DefenseDefinitionNames.Floor || definition == LayoutDefenseRules.PerimeterBridge || definition == LayoutDefenseRules.PerimeterHeavyBridge;

    /// <summary>The stone block resource with the most stock, when it covers the stone the buildings need.</summary>
    internal static (string Stuff, bool Ok) DefensePerimeterStone(ColonyReading projection, IReadOnlyList<Building> buildings)
    {
        var stock = ResourceStockRules.Count(projection.Snapshot.Resources);
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

    internal static (DefenseKillbox Killbox, BridgeCellRect Region, Cell Home, bool Ok) DefenseKillbox(ColonyReading projection)
    {
        if (projection.Facts.LayoutPlan is not { } plan)
        {
            return (new DefenseKillbox(), new BridgeCellRect(), default, false);
        }
        var (k, region, home, ok) = LayoutPlan.LayoutKillbox(plan, projection.Facts.MapBounds);
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
            Cover = DefenseCover(seen),
        };
    }

    /// <summary>How many defenders hold a ranged weapon (capped at eight) and the shortest reach among them; zero with none.</summary>
    internal static (int Count, double Shortest) DefenderRangeOf(IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.PawnState> rows, Armament arms)
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
            return (0, 0);
        }
        if (count > 8)
        {
            count = 8;
        }
        return (count, shortest);
    }
    internal static DefenseRequest DefenseIedRequest(ReviewReading read, DefenseRequest request)
    {
        var projection = read.Reading;
        request = request with { IEDMax = DefenseIEDs.IEDBudget(RaidDefense.Of(projection.Facts), projection.Facts.EssentialInputs.Power.Buildings.Where(b => b.Turret is not null).ToArray(), projection.Facts.GoalBuild.Turrets), IEDStock = ResourceStockRules.Count(projection.Snapshot.Resources) };
        var ieds = new List<DefenseIED>();
        var unitCosts = new Dictionary<string, EquatableArray<Amount>>(request.UnitCosts, StringComparer.Ordinal);
        foreach (var d in projection.Facts.Definitions)
        {
            if (d.Name != DefenseIedHighExplosive && d.Name != DefenseIedIncendiary)
            {
                continue;
            }
            if (!DefenseDefinitionAvailable(read, d.Name))
            {
                continue;
            }
            var radius = (d.Explosive ?? throw new RequiredFactException("planning Explosive")).ExplosiveRadius;
            ieds.Add(new DefenseIED(d.Name, radius, d.Name == DefenseIedIncendiary));
            var (price, ok) = DefenseUnitPrice(projection, d);
            if (ok)
            {
                unitCosts[d.Name] = [.. price.Costs];
            }
        }
        ieds.Sort((a, b) => string.CompareOrdinal(a.Definition, b.Definition));
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
