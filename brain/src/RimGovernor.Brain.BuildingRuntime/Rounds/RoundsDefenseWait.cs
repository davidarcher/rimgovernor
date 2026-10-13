using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseWaitTier = "wait";

    /// <summary>The fight-time work of the layout planner: fuel a burn, else harden the waiting rooms.</summary>
    internal async Task<RoundsDefenseLayoutResult> FightAsync(StandardState goal, ControlState state, CombatMemory m, CancellationToken call, CancellationToken epoch)
    {
        if (m.Burn is not null && m.Burn.Fueling())
        {
            return await FuelAsync(goal, state, m.Burn, call, epoch).ConfigureAwait(false);
        }
        return await HardenAsync(goal, state, m, call, epoch).ConfigureAwait(false);
    }

    internal async Task<(RoundsReading Read, IReadOnlyDictionary<Cell, WaitDoorCell> Census)> FightCensusAsync(ControlState state, BridgeCellRect region, CancellationToken call)
    {
        var p = _reviewer.Player;
        var expected = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, [], call).ConfigureAwait(false);
        var (site, _) = await _native.ReadDefenseSiteAsync(Boundary.Identity(state.Snapshot), region, call).ConfigureAwait(false);
        SameTick(site.Context, state, read.Projection.Identity.Tick);
        return (read, DefenseFightCensus(site));
    }

    internal async Task<RoundsDefenseLayoutResult> HardenAsync(StandardState goal, ControlState state, CombatMemory m, CancellationToken call, CancellationToken epoch)
    {
        if (m.WaitRooms.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseWaitTier };
        }
        var (read, census) = await FightCensusAsync(state, DefenseWaitRegion(m), call).ConfigureAwait(false);
        read.Projection.Resources.TryGetValue(out var stock);
        var doorStock = stock is not null && stock.TryGetValue(new Resource(PolicyApi.WaitDoorStuff), out var count) ? count : 0;
        var buildings = PolicyApi.WaitHardening(m, census, doorStock, DefenseDefinitionNames.Door, DefenseDefinitionNames.Wall, DefenseDefinitionNames.WallStuff);
        if (buildings.Count == 0)
        {
            return new RoundsDefenseLayoutResult { Verdict = BuildingReasonNoDeficit, Tier = DefenseWaitTier };
        }
        return await AdmitFightBuildsAsync(goal, state, read, buildings, DefenseWaitTier, call, epoch).ConfigureAwait(false);
    }

    internal async Task<RoundsDefenseLayoutResult> AdmitFightBuildsAsync(StandardState goal, ControlState state, RoundsReading read, IReadOnlyList<Building> buildings, string tier, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var tick = read.Projection.Identity.Tick;
        var id = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = id, Revision = new PlanRevision(1) };
        var actions = new List<DomainAction>();
        var previews = new List<Preview>();
        var stockSeen = new StockObservation { Snapshot = snapshot, Tick = tick };
        var promoted = DefensePromoted(read.Projection);
        foreach (var b in buildings)
        {
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), b, PolicyApi.DefenseBuildTier(promoted, false, false));
            var (preview, ok) = await PreviewAsync(action, snapshot, tick, b.Cell, call).ConfigureAwait(false);
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
        var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
        {
            Owner = goal,
            Method = method,
            Plan = plan,
            Current = snapshot,
            Tick = tick,
            Bounds = Known(read.Projection.Bounds),
            Stock = stockSeen,
            Previews = previews,
            Purpose = Purpose.Defense,
        }, call).ConfigureAwait(false);
        if (!decision.Admitted)
        {
            return new RoundsDefenseLayoutResult { Verdict = AdmissionLog.AdmissionRefused(decision), Plan = id, Tier = tier };
        }
        DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "builds", tier, new Dictionary<string, object?> { ["builds"] = actions.Count, ["method"] = method.Value }, call);
        return new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = tier };
    }

    /// <summary>Marks which cells of the request can carry the wall and the trap, from the terrain affordances of the site.</summary>
    internal async Task<DefenseRequest> MarkWallSupportAsync(C.Identity identity, DefenseRequest request, IReadOnlyList<BridgeDefenseCell> site, CancellationToken call)
    {
        var catalog = await PawnCatalogAsync(_native, identity, call).ConfigureAwait(false);
        if (catalog is null)
        {
            return request;
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
        var cells = new List<DefenseCell>(request.Cells.Count);
        foreach (var row in request.Cells)
        {
            var cell = row;
            terrain.TryGetValue(cell.Cell, out var terrainName);
            var def = catalog.FindTerrain(terrainName ?? "");
            if (def is not null)
            {
                if (wallNeed != "")
                {
                    cell = cell with { WallSupport = Known(def.affordances.Any(a => a.defName == wallNeed)) };
                }
                if (trapNeed != "")
                {
                    cell = cell with { TrapSupport = Known(def.affordances.Any(a => a.defName == trapNeed)) };
                }
            }
            cells.Add(cell);
        }
        return request with { Cells = [.. cells] };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The open combat memory that needs layout work (a waiting room to harden or a burn to fuel) in the world, if any.</summary>
    internal static CombatMemory? DefenseWaitingFight(StoreJournal journal, StoreWorld world, CancellationToken ct)
    {
        foreach (var (_, f) in journal.OpenCombatFights(ct))
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
