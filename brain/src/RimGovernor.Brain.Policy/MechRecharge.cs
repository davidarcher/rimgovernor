namespace RimGovernor.Brain.Policy;

/// <summary>One charger's state as the colony section reads it.</summary>
public sealed record MechCharger
{
    public Fact<bool> Powered { get; init; }
    public Fact<bool> FullOfWaste { get; init; }
    public Fact<bool> Charging { get; init; }
}

/// <summary>
/// Ports <c>mech_recharge.go</c>. Mechs spend Need_MechEnergy working and refill it on a charger
/// (Building_MechCharger, one mech at a time). Each control group carries its own recharge band
/// (mechRechargeThresholds, read natively per mech as PawnMech.RechargeBelow/RechargeAbove): the game sends a mech
/// to a charger below the band's lower bound. The bot reuses that band instead of a number of its own: a group
/// whose mech is below its lower bound moves to the Recharge mode, provided a charger is ready, so no mech returns
/// to work half charged; a group in Recharge stays there until every mech is at or above the band's upper bound
/// (or no charger is ready), then returns to the role mode PlanMechControl would give it. Chargers come before
/// more mechs: MechChargerOwed answers whether the colony needs one more before gestation adds a mech. Charger
/// waste disposal belongs to waste management.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>
    /// The mode a planned group should run: the catalog's Recharge mode while the group is low (or still charging
    /// up), the group's role mode otherwise. A mech whose energy or band is unread never makes the group low; a
    /// charging group with an unread mech stays charging.
    /// </summary>
    public static string MechRechargeMode(MechCatalog catalog, MechGroup g, IReadOnlyList<MechInput> all, bool chargerReady)
    {
        if (!chargerReady || catalog.Recharge == "")
        {
            return g.Mode;
        }
        var charging = g.Holds(all, catalog.Recharge);
        var low = false;
        var recovered = true;
        foreach (var mech in g.Mechs)
        {
            var ek = mech.Energy.TryGetValue(out var energy);
            var bk = mech.RechargeBelow.TryGetValue(out var below);
            var ak = mech.RechargeAbove.TryGetValue(out var above);
            if (!ek || !bk || !ak)
            {
                recovered = false;
                continue;
            }
            if (energy < below)
            {
                low = true;
            }
            if (energy < above)
            {
                recovered = false;
            }
        }
        if (low || (charging && !recovered))
        {
            return catalog.Recharge;
        }
        return g.Mode;
    }

    /// <summary>Whether some charger is powered and not full of waste (Building_MechCharger.IsFullOfWaste stops it charging).</summary>
    public static bool MechChargerReady(IReadOnlyList<MechCharger> chargers)
    {
        foreach (var c in chargers)
        {
            if (c.Powered.TryGetValue(out var powered) && powered && c.FullOfWaste.TryGetValue(out var full) && !full)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the colony owes one more charger: a mechanitor exists and every standing charger is charging a mech
    /// (none standing counts as all busy). An unread charger list or charging state owes nothing, and neither does
    /// a charger full of waste: emptying it is owned by waste management, not a reason to build. An idle unpowered
    /// charger owes nothing either, though MechChargerReady does not count it ready: the power planner covers every
    /// power consumer (PowerCoverage, connectLiveBeforeUpgrade), so wiring it is the power goal's and a second
    /// charger would stand unpowered too. Gestation waits on MechChargerReady meanwhile.
    /// </summary>
    public static bool MechChargerOwed(int mechanitors, Fact<EquatableArray<MechCharger>> chargers)
    {
        if (!chargers.TryGetValue(out var rows) || mechanitors == 0)
        {
            return false;
        }
        foreach (var c in rows)
        {
            var ok = c.Charging.TryGetValue(out var busy);
            var fk = c.FullOfWaste.TryGetValue(out var full);
            if (!ok || !busy || !fk || full)
            {
                return false;
            }
        }
        return true;
    }
}
