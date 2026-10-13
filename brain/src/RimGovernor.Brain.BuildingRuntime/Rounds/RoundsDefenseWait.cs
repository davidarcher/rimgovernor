using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseWaitTier = "wait";

    /// <summary>The fight-time work of the layout planner: fuel a burn, else harden the waiting rooms.</summary>
    internal RoundsDefenseLayoutResult Fight(CancellationToken call, CancellationToken epoch, StandardState goal, ControlState state, CombatMemory m)
    {
        if (m.Burn is not null && m.Burn.Fueling())
        {
            return Fuel(call, epoch, goal, state, m.Burn);
        }
        return Harden(call, epoch, goal, state, m);
    }

    internal (RoundsReading Read, IReadOnlyDictionary<Cell, WaitDoorCell> Census) FightCensus(CancellationToken call, ControlState state, BridgeCellRect region)
    {
        var p = _reviewer.Player;
        var expected = StepScope(call, _reviewer.Native);
        var claims = p.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
        var read = _reviewer.ObserveOwned(call, _reviewer.Native, expected, claims, []);
        var (site, _) = _native.ReadDefenseSite(BoundaryApi.Identity(state.Snapshot), region, call);
        SameTick(site.Context, state, read.Projection.Identity.Tick);
        return (read, DefenseFightCensus(site));
    }

    internal RoundsDefenseLayoutResult Harden(CancellationToken call, CancellationToken epoch, StandardState goal, ControlState state, CombatMemory m)
    {
        if (m.WaitRooms.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseWaitTier };
        }
        var (read, census) = FightCensus(call, state, DefenseWaitRegion(m));
        read.Projection.Resources.TryGetValue(out var stock);
        var doorStock = stock is not null && stock.TryGetValue(new Resource(PolicyApi.WaitDoorStuff), out var count) ? count : 0;
        var buildings = PolicyApi.WaitHardening(m, census, doorStock, DefenseDefinitionNames.Door, DefenseDefinitionNames.Wall, DefenseDefinitionNames.WallStuff);
        if (buildings.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseWaitTier };
        }
        return AdmitFightBuilds(call, epoch, goal, state, read, buildings, DefenseWaitTier);
    }

    internal RoundsDefenseLayoutResult AdmitFightBuilds(CancellationToken call, CancellationToken epoch, StandardState goal, ControlState state, RoundsReading read, IReadOnlyList<Building> buildings, string tier)
    {
        var p = _reviewer.Player;
        var tick = read.Projection.Identity.Tick;
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = 1 };
        var actions = new List<DomainAction>();
        var previews = new List<Preview>();
        var stockSeen = new StockObservation { Snapshot = snapshot, Tick = tick };
        var promoted = DefensePromoted(read.Projection);
        foreach (var b in buildings)
        {
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), b, PolicyApi.DefenseBuildTier(promoted, false, false));
            var (preview, ok) = Preview(call, action, snapshot, tick, b.Cell);
            if (!ok)
            {
                continue;
            }
            actions.Add(action);
            previews.Add(preview.Preview);
            MergeRoundsStock(stockSeen, preview.Stock, actions.Count == 1);
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
        var decision = AdmitMethod(call, p.Journal, new BuildingMethodRequest
        {
            Owner = goal,
            Method = method,
            Plan = plan,
            Current = snapshot,
            Tick = tick,
            Bounds = Known(read.Projection.Bounds),
            Stock = stockSeen,
            Previews = previews,
            Purpose = PolicyApi.Defense,
        });
        if (!decision.Admitted)
        {
            return new RoundsDefenseLayoutResult { Verdict = AdmissionRefused(decision), Plan = id, Tier = tier };
        }
        DefenseAction(call, "defense-layout", LogLevel.Info, "applied", "builds", tier, new Dictionary<string, object?> { ["builds"] = actions.Count, ["method"] = method.Value });
        return new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = tier };
    }

    /// <summary>Marks which cells of the request can carry the wall and the trap, from the terrain affordances of the site.</summary>
    internal void MarkWallSupport(CancellationToken call, C.Identity identity, DefenseRequest request, IReadOnlyList<BridgeDefenseCell> site)
    {
        var catalog = PawnCatalog(call, _native, identity);
        if (catalog is null)
        {
            return;
        }
        var wallNeed = DefenseWallAffordance(catalog, request.Definitions.Wall, request.Definitions.WallStuff);
        var trapNeed = DefenseWallAffordance(catalog, request.Definitions.Trap, request.Definitions.TrapStuff);
        var terrain = new Dictionary<Cell, string>();
        foreach (var cell in site)
        {
            if (!cell.Fogged)
            {
                terrain[cell.Cell] = cell.Terrain;
            }
        }
        foreach (var cell in request.Cells)
        {
            terrain.TryGetValue(cell.Cell, out var terrainName);
            var def = catalog.TerrainDef(terrainName ?? "");
            if (def is null)
            {
                continue;
            }
            if (wallNeed != "")
            {
                cell.WallSupport = Known(def.Affordances.Contains(wallNeed));
            }
            if (trapNeed != "")
            {
                cell.TrapSupport = Known(def.Affordances.Contains(trapNeed));
            }
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The open combat memory that needs layout work (a waiting room to harden or a burn to fuel) in the world, if any.</summary>
    internal static CombatMemory? DefenseWaitingFight(CancellationToken ct, StoreJournal journal, StoreWorld world)
    {
        foreach (var f in journal.OpenCombatFights(ct))
        {
            if (f.Open && f.World == world && ((f.Memory.Wait && f.Memory.WaitRooms.Count > 0) || (f.Memory.Burn is not null && f.Memory.Burn.Fueling())))
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

    internal static Dictionary<Cell, WaitDoorCell> DefenseFightCensus(BridgeDefenseSite site)
    {
        var census = new Dictionary<Cell, WaitDoorCell>();
        foreach (var c in site.Cells)
        {
            if (!c.Fogged)
            {
                census[c.Cell] = new WaitDoorCell { Edifice = c.EdificeDefName, Stuff = c.EdificeStuff, Walkable = c.Walkable, Roofed = c.Roofed };
            }
        }
        return census;
    }

    internal static string DefenseWallAffordance(DefinitionCatalog catalog, string wall, string stuff)
    {
        var def = catalog.ThingDef(wall);
        if (def is null)
        {
            return "";
        }
        if (def.UseStuffTerrainAffordance)
        {
            var s = catalog.ThingDef(stuff);
            if (s is not null && s.TerrainAffordanceNeeded != "")
            {
                return s.TerrainAffordanceNeeded;
            }
        }
        return def.TerrainAffordanceNeeded;
    }
}
