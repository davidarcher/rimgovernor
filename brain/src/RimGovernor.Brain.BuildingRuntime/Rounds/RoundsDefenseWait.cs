using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseWaitTier = "wait";

    /// <summary>The fight-time work of the layout planner: harden the waiting rooms.</summary>
    internal async Task<RoundsDefenseLayoutResult> FightAsync(NeedOwner goal, ControlState state, CombatMemory m, CancellationToken call, CancellationToken epoch)
    {
        return await HardenAsync(goal, state, m, call, epoch).ConfigureAwait(false);
    }

    internal async Task<(RoundsReading Read, IReadOnlyDictionary<Cell, WaitDoorCell> Census)> FightCensusAsync(ControlState state, BridgeCellRect region, CancellationToken call)
    {
        var p = _reviewer.Player;
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, [], call).ConfigureAwait(false);
        var site = await _native.ReadDefenseSiteAsync(Boundary.Identity(state.Snapshot), region, call).ConfigureAwait(false);
        SameTick(site.Context, state, read.Reading.Identity.Tick);
        return (read, DefenseFightCensus(site));
    }

    internal async Task<RoundsDefenseLayoutResult> HardenAsync(NeedOwner goal, ControlState state, CombatMemory m, CancellationToken call, CancellationToken epoch)
    {
        if (m.WaitRooms.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseWaitTier };
        }
        var (read, census) = await FightCensusAsync(state, DefenseWaitRegion(m), call).ConfigureAwait(false);
        var doorStock = ResourceStockRules.Count(read.Reading.Snapshot.Resources).GetValueOrDefault(new Resource(CombatWaitRules.WaitDoorStuff));
        var buildings = CombatWaitRules.WaitHardening(m, census, doorStock, DefenseDefinitionNames.Door, DefenseDefinitionNames.Wall, DefenseDefinitionNames.WallStuff);
        if (buildings.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseWaitTier };
        }
        return await AdmitFightBuildsAsync(goal, state, read, buildings, DefenseWaitTier, call, epoch).ConfigureAwait(false);
    }

    internal async Task<RoundsDefenseLayoutResult> AdmitFightBuildsAsync(NeedOwner goal, ControlState state, RoundsReading read, IReadOnlyList<Building> buildings, string tier, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var tick = read.Reading.Identity.Tick;
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
        var actions = new List<DomainAction>();
        var previews = new List<Preview>();
        var stockSeen = new StockObservation { Snapshot = snapshot, Tick = tick };
        var promoted = DefensePromoted(read.Reading);
        foreach (var b in buildings)
        {
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), b, DefenseTier.DefenseBuildTier(promoted, false, false));
            var (preview, ok) = await PreviewAsync(action, snapshot, tick, b.Cell, call).ConfigureAwait(false);
            if (!ok)
            {
                continue;
            }
            actions.Add(action);
            previews.Add(preview.Preview);
            RoundsBuildingPlanner.MergeRoundsStock(stockSeen, preview.Stock);
        }
        if (actions.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = SiteBlocked("defense_layout", "preview_refused"), Tier = tier };
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw DefenseControlErr(118);
        }
        var now = _reviewer.Clock.Now();
        if (now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw DefenseControlErr(122);
        }
        var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"defense-{tier}-{tick.Value}"));
        var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
        {
            Owner = goal,
            Method = method,
            Plan = plan,
            Current = snapshot,
            Tick = tick,
            Bounds = read.Reading.Facts.MapBounds,
            Stock = stockSeen,
            Previews = previews,
            Purpose = Purpose.Defense,
        }, call).ConfigureAwait(false);
        if (!decision.Admitted)
        {
            return new RoundsDefenseLayoutResult { Verdict = AdmissionLog.AdmissionRefused(decision), Plan = id, Tier = tier };
        }
        DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "builds", tier, new Dictionary<string, object?> { ["builds"] = actions.Count, ["method"] = method.Value }, call);
        return new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = tier };
    }

    /// <summary>
    /// The site census as policy cells: every seen cell a row with its footing (whether its terrain carries the funnel
    /// wall's and the trap's affordance; a building that needs none stands on any terrain), and the fogged cells apart.
    /// The definition catalog is required, and a seen cell whose terrain the catalog has no row for is a catalog fault.
    /// </summary>
    internal async Task<(List<DefenseCell> Cells, List<Cell> Fogged)> DefenseSiteCellsAsync(C.Identity identity, DefenseDefinitions definitions, IReadOnlyList<O.DefenseCell> site, CancellationToken call)
    {
        var catalog = await RoundsEquipPawn.PawnCatalogAsync(_native, identity, call).ConfigureAwait(false) ?? throw new RequiredFactException("definition_catalog");
        var wallNeed = DefenseWallAffordance(catalog, definitions.Wall, definitions.WallStuff);
        var trapNeed = DefenseWallAffordance(catalog, definitions.Trap, definitions.TrapStuff);
        var cells = new List<DefenseCell>(site.Count);
        var fogged = new List<Cell>();
        foreach (var cell in site)
        {
            if (cell is not O.DefenseCell.Seen seen)
            {
                fogged.Add(cell.Cell);
                continue;
            }
            var terrain = catalog.FindTerrain(seen.Terrain) ?? throw new CatalogException($"catalog has no def row for terrain {seen.Terrain} at {cell.Cell}");
            var wall = wallNeed == "" || terrain.affordances.Any(a => a.defName == wallNeed);
            var trap = trapNeed == "" || terrain.affordances.Any(a => a.defName == trapNeed);
            cells.Add(DefenseCellFacts(seen, wall, trap));
        }
        return (cells, fogged);
    }
}

partial class RoundsDefenseLayoutPlanner
{
    /// <summary>The open combat memory that needs layout work (a waiting room to harden or a burn to fuel) in the world, if any.</summary>
    internal static CombatMemory? DefenseWaitingFight(StoreJournal journal, StoreWorld world, CancellationToken ct)
    {
        foreach (var (_, f) in journal.OpenCombatFights(ct))
        {
            if (f.Open && f.World == world && ((f.Memory.Wait && f.Memory.WaitRooms.Count > 0)))
            {
                return f.Memory;
            }
        }
        return null;
    }

    internal static BridgeCellRect DefenseWaitRegion(CombatMemory m)
    {
        var minX = m.WaitRooms[0].Door.X;
        var minZ = m.WaitRooms[0].Door.Z;
        var maxX = minX;
        var maxZ = minZ;
        foreach (var w in m.WaitRooms)
        {
            foreach (var c in new[] { w.Door, w.Inside })
            {
                minX = Math.Min(minX, c.X);
                minZ = Math.Min(minZ, c.Z);
                maxX = Math.Max(maxX, c.X);
                maxZ = Math.Max(maxZ, c.Z);
            }
        }
        return new BridgeCellRect { Min = new Cell(minX, minZ), Max = new Cell(maxX, maxZ) };
    }

    internal static Dictionary<Cell, WaitDoorCell> DefenseFightCensus(O.DefenseSiteSnapshot site)
    {
        var census = new Dictionary<Cell, WaitDoorCell>();
        foreach (var c in site.Cells)
        {
            if (c is O.DefenseCell.Seen seen)
            {
                census[c.Cell] = new WaitDoorCell { Edifice = seen.Edifice?.DefName ?? "", Stuff = seen.Edifice?.Stuff ?? "", Walkable = seen.Walkable };
            }
        }
        return census;
    }

    internal static string DefenseWallAffordance(DefinitionCatalog catalog, string wall, string stuff)
    {
        var def = catalog.FindThing(wall);
        if (def is null)
        {
            return "";
        }
        if (def.useStuffTerrainAffordance)
        {
            var s = catalog.FindThing(stuff);
            if (s?.terrainAffordanceNeeded is { } need && need.defName != "")
            {
                return need.defName;
            }
        }
        return def.terrainAffordanceNeeded?.defName ?? "";
    }
}
