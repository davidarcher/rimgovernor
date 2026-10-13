using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the pre-fight loadout: the map bounds and the weapons lying on the map.</summary>
internal interface ILoadoutWeaponSource
{
    (MapBounds Bounds, BridgeResult Result) ReadMapBounds(C.Identity identity, Cell origin, CancellationToken ct);

    (EquipRead Read, BridgeResult Result) ReadEquipWeapons(C.Identity identity, Cell from, Cell to, CancellationToken ct);
}

public sealed partial class RoundsDefensePlanner
{
    internal const string ShieldBeltDef = "Apparel_ShieldBelt";

    /// <summary>The equip and wear orders that arm the squad before the fight: threat weapons from the map and shield belts from the colony.</summary>
    internal List<LoadoutOrder> FightLoadout(CancellationToken call, ControlState state, CombatView view, IReadOnlyDictionary<string, O.PawnState> rows, IReadOnlyList<PawnId> pawns, DefinitionCatalog catalog)
    {
        var threat = PolicyApi.ClassifyLoadoutThreat(view, false);
        var live = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns)
        {
            live[p.Id] = p;
        }
        FrameThings(call, _native, BoundaryApi.Identity(state.Snapshot), out var things);
        var defenders = new List<LoadoutDefender>();
        var beltless = false;
        foreach (var pawn in pawns)
        {
            if (!rows.TryGetValue(pawn.Value, out var row) || row.Pawn is null)
            {
                continue;
            }
            EquipCandidatePawn facts;
            try
            {
                facts = EquipCandidatePawnFacts(row, catalog, things);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseAction(call, "routine-defense", LogLevel.Warn, "failed", "loadout_pawn", pawn.Value, new Dictionary<string, object?> { ["error"] = ex });
                return [];
            }
            var d = new LoadoutDefender { EquipCandidatePawn = facts, Primary = PrimaryDef(row, things) };
            if (live.TryGetValue(pawn, out var lp))
            {
                d.ShieldBelt = lp.ShieldBelt;
                if (lp.Weapon != "")
                {
                    d.Primary = lp.Weapon;
                }
            }
            try
            {
                d.PrimaryFacts = catalog.WeaponOf(d.Primary);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseAction(call, "routine-defense", LogLevel.Warn, "failed", "loadout_primary", pawn.Value, new Dictionary<string, object?> { ["error"] = ex });
                return [];
            }
            beltless = beltless || !d.ShieldBelt;
            defenders.Add(d);
        }
        if (defenders.Count == 0)
        {
            return [];
        }
        var identity = BoundaryApi.Identity(state.Snapshot);
        IReadOnlyList<EquipCandidateWeapon> weapons = [];
        if (_native is ILoadoutWeaponSource source && threat != PolicyApi.LoadoutNone)
        {
            try
            {
                weapons = LoadoutWeapons(call, source, identity, state, catalog);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseAction(call, "routine-defense", LogLevel.Warn, "failed", "loadout_weapons", "", new Dictionary<string, object?> { ["error"] = ex });
            }
        }
        IReadOnlyList<LoadoutApparel> belts = [];
        if (_native is IColonyFactsReader facts2 && beltless)
        {
            try
            {
                belts = LoadoutBelts(call, facts2, identity, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseAction(call, "routine-defense", LogLevel.Warn, "failed", "loadout_belts", "", new Dictionary<string, object?> { ["error"] = ex });
            }
        }
        return PolicyApi.ThreatLoadout(threat, defenders, weapons, belts);
    }

    internal static List<EquipCandidateWeapon> LoadoutWeapons(CancellationToken call, ILoadoutWeaponSource source, C.Identity identity, ControlState state, DefinitionCatalog catalog)
    {
        var bounds = source.ReadMapBounds(identity, default, call).Bounds;
        try
        {
            BoundaryApi.Context(bounds.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("loadoutWeapons: bounds");
        }
        if (bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0)
        {
            throw new ControlException("loadoutWeapons: bounds");
        }
        var read = source.ReadEquipWeapons(identity, default, new Cell(bounds.Bounds.Width - 1, bounds.Bounds.Height - 1), call).Read;
        try
        {
            BoundaryApi.Context(read.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("loadoutWeapons: context");
        }
        var output = new List<EquipCandidateWeapon>(read.Targets.Count);
        foreach (var w in read.Targets)
        {
            output.Add(EquipCandidateWeapon(catalog, w));
        }
        return output;
    }

    internal List<LoadoutApparel> LoadoutBelts(CancellationToken call, IColonyFactsReader source, C.Identity identity, ControlState state)
    {
        var reply = _reviewer.ColonyFacts(call, source, identity, true).Reply;
        var observed = reply.Observed ?? throw new ControlException("loadoutBelts: observed == nil");
        try
        {
            BoundaryApi.Context(observed.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("loadoutBelts: context");
        }
        if (!FrameThings(call, source, identity, out var things))
        {
            throw new ControlException("loadoutBelts: things");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<LoadoutApparel>();
        foreach (var pawn in observed.Planning?.Observed?.Gear?.Pawns ?? [])
        {
            foreach (var candidate in pawn.Candidates)
            {
                var thing = candidate.Item?.Thing;
                if (thing is null)
                {
                    continue;
                }
                things.Row(thing, out var row);
                if (row?.Thing?.DefName != ShieldBeltDef || thing.Id == "" || !seen.Add(thing.Id))
                {
                    continue;
                }
                var item = new LoadoutApparel { Thing = thing.Id, Definition = ShieldBeltDef };
                if (row.Thing.Position is { } at)
                {
                    item.Cell = new Cell(at.X, at.Z);
                }
                output.Add(item);
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Thing, b.Thing));
        return output;
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static string PrimaryDef(O.PawnState row, Things things)
    {
        var equipment = row.Equipment;
        if (equipment is null)
        {
            return "";
        }
        foreach (var item in equipment.Equipped)
        {
            var id = equipment.PrimaryId;
            if (id != "" && item.Thing?.Id == id)
            {
                return GearDef(things, item.Thing);
            }
        }
        return "";
    }

    /// <summary>The wear or equip action per loadout order and the pawns that now hold a loadout action.</summary>
    internal static (IReadOnlyList<DomainAction> Actions, HashSet<PawnId> Held) LoadoutActions(PlanId plan, IReadOnlyList<LoadoutOrder> orders)
    {
        var held = new HashSet<PawnId>();
        var actions = new List<DomainAction>(orders.Count);
        for (var i = 0; i < orders.Count; i++)
        {
            var order = orders[i];
            var id = new ActionId(string.Create(CultureInfo.InvariantCulture, $"{plan}-loadout-{i}"));
            DomainAction action;
            if (order.Wear)
            {
                action = DomainAction.NewGearReplaceAction(id, new GearReplace(order.Pawn, order.Thing, order.Definition));
            }
            else
            {
                action = DomainAction.NewEquipAction(id, new Equip(order.Pawn, order.Thing, order.Definition, order.Cell));
            }
            held.Add(order.Pawn);
            actions.Add(action);
        }
        return (actions, held);
    }

    /// <summary>The pawns with an open equip or wear action in the plan.</summary>
    internal static HashSet<PawnId> LoadoutPending(PlanState plan)
    {
        var output = new HashSet<PawnId>();
        foreach (var progress in plan.Progress)
        {
            if (!DomainApi.StandardWorkOpen([progress]))
            {
                continue;
            }
            if (progress.Action.Equip is { } equip)
            {
                output.Add(equip.Pawn);
            }
            else if (progress.Action.GearReplace is { } wear)
            {
                output.Add(wear.Pawn);
            }
        }
        return output;
    }
}
