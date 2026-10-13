using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the pre-fight loadout: the map bounds and the weapons lying on the map.</summary>
internal interface ILoadoutWeaponSource
{
    Task<(MapBounds Bounds, BridgeResult Result)> ReadMapBoundsAsync(C.Identity identity, Cell origin, CancellationToken ct);

    Task<(EquipRead Read, BridgeResult Result)> ReadEquipWeaponsAsync(C.Identity identity, Cell from, Cell to, CancellationToken ct);
}

public sealed partial class RoundsDefensePlanner
{
    internal const string ShieldBeltDef = "Apparel_ShieldBelt";

    /// <summary>The equip and wear orders that arm the squad before the fight: threat weapons from the map and shield belts from the colony.</summary>
    internal async Task<List<LoadoutOrder>> FightLoadoutAsync(ControlState state, CombatView view, IReadOnlyDictionary<string, O.PawnState> rows, IReadOnlyList<PawnId> pawns, DefinitionCatalog catalog, CancellationToken call)
    {
        var threat = PolicyApi.ClassifyLoadoutThreat(view, false);
        var live = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns)
        {
            live[p.Id] = p;
        }
        var things = await FrameThingsOfAsync(_native, Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
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
                facts = RoundsEquipPlanner.EquipCandidatePawnFacts(row, catalog, things);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseAction("routine-defense", TelemetryLevel.Warn, "failed", "loadout_pawn", pawn.Value, new Dictionary<string, object?> { ["error"] = ex }, call);
                return [];
            }
            var d = new LoadoutDefender { Candidate = facts, Primary = PrimaryDef(row, things) };
            if (live.TryGetValue(pawn, out var lp))
            {
                d = d with { ShieldBelt = lp.ShieldBelt };
                if (lp.Weapon != "")
                {
                    d = d with { Primary = lp.Weapon };
                }
            }
            try
            {
                d = d with { PrimaryFacts = catalog.WeaponOf(d.Primary) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseAction("routine-defense", TelemetryLevel.Warn, "failed", "loadout_primary", pawn.Value, new Dictionary<string, object?> { ["error"] = ex }, call);
                return [];
            }
            beltless = beltless || !d.ShieldBelt;
            defenders.Add(d);
        }
        if (defenders.Count == 0)
        {
            return [];
        }
        var identity = Boundary.Identity(state.Snapshot);
        IReadOnlyList<EquipCandidateWeapon> weapons = [];
        if (_native is ILoadoutWeaponSource source && threat != Pol.LoadoutThreat.None)
        {
            try
            {
                weapons = await LoadoutWeaponsAsync(source, identity, state, catalog, call).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseAction("routine-defense", TelemetryLevel.Warn, "failed", "loadout_weapons", "", new Dictionary<string, object?> { ["error"] = ex }, call);
            }
        }
        IReadOnlyList<LoadoutApparel> belts = [];
        if (_native is IColonyFactsReader facts2 && beltless)
        {
            try
            {
                belts = await LoadoutBeltsAsync(facts2, identity, state, call).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseAction("routine-defense", TelemetryLevel.Warn, "failed", "loadout_belts", "", new Dictionary<string, object?> { ["error"] = ex }, call);
            }
        }
        return PolicyApi.ThreatLoadout(threat, defenders, weapons, belts);
    }

    internal static async Task<List<EquipCandidateWeapon>> LoadoutWeaponsAsync(ILoadoutWeaponSource source, C.Identity identity, ControlState state, DefinitionCatalog catalog, CancellationToken call)
    {
        var bounds = (await source.ReadMapBoundsAsync(identity, default, call).ConfigureAwait(false)).Bounds;
        try
        {
            Boundary.Context(bounds.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("loadoutWeapons: bounds");
        }
        if (bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0)
        {
            throw new ControlException("loadoutWeapons: bounds");
        }
        var read = (await source.ReadEquipWeaponsAsync(identity, default, new Cell(bounds.Bounds.Width - 1, bounds.Bounds.Height - 1), call).ConfigureAwait(false)).Read;
        try
        {
            Boundary.Context(read.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("loadoutWeapons: context");
        }
        var output = new List<EquipCandidateWeapon>(read.Targets.Count);
        foreach (var w in read.Targets)
        {
            output.Add(RoundsEquipPlanner.EquipCandidateWeaponOf(catalog, w));
        }
        return output;
    }

    internal async Task<List<LoadoutApparel>> LoadoutBeltsAsync(IColonyFactsReader source, C.Identity identity, ControlState state, CancellationToken call)
    {
        var reply = await _reviewer.ColonyFactsAsync(source, identity, true, call).ConfigureAwait(false);
        var observed = reply.Observed ?? throw new ControlException("loadoutBelts: observed == nil");
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("loadoutBelts: context");
        }
        var things = await FrameThingsOfAsync(source, identity, call).ConfigureAwait(false);
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
                things.TryRow(thing, out var row);
                if (row?.Thing_?.DefName != ShieldBeltDef || thing.Id == "" || !seen.Add(thing.Id))
                {
                    continue;
                }
                var cell = row.Thing_.Position is { } at ? new Cell(at.X, at.Z) : default;
                output.Add(new LoadoutApparel(thing.Id, ShieldBeltDef, cell));
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
                action = DomainAction.NewGearReplaceAction(id, new GearReplace((Domain.PawnId)order.Pawn, order.Thing, order.Definition));
            }
            else
            {
                action = DomainAction.NewEquipAction(id, new Equip((Domain.PawnId)order.Pawn, order.Thing, order.Definition, order.Cell));
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
            if (!Standard.WorkOpen([progress]))
            {
                continue;
            }
            if (progress.Action.Equip is { } equip)
            {
                output.Add((PawnId)equip.Pawn);
            }
            else if (progress.Action.GearReplace is { } wear)
            {
                output.Add((PawnId)wear.Pawn);
            }
        }
        return output;
    }
}
