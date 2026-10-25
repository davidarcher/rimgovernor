namespace RimGovernor.Brain.Policy;

/// <summary>
/// Mechs spend Need_MechEnergy working and refill it on a charger
/// (Building_MechCharger, one mech at a time). Each control group carries its own recharge band
/// (mechRechargeThresholds, read natively per mech as PawnMech.Group): the game sends a mech
/// to a charger below the band's lower bound. The bot reuses that band instead of a number of its own: a group
/// whose mech is below its lower bound moves to the Recharge mode, provided a charger is ready, so no mech returns
/// to work half charged; a group in Recharge stays there until every mech is at or above the band's upper bound
/// (or no charger is ready), then returns to the role mode PlanMechControl would give it. Chargers come before
/// more mechs: MechChargerOwed answers whether the colony needs one more before gestation adds a mech. Charger
/// waste disposal belongs to waste management.
/// </summary>
public static partial class MechRules
{
    /// <summary>
    /// The mode a planned group should run: the catalog's Recharge mode while the group is low (or still charging
    /// up), the group's role mode otherwise. A mech without an energy need or outside a group never makes the group
    /// low; a charging group with such a mech stays charging.
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
            if (mech.Energy is not RimGovernor.Contracts.Wire.Observations.PawnMechEnergy.Level { Fraction: var energy } || mech.Group is not { } band)
            {
                recovered = false;
                continue;
            }
            if (energy < band.RechargeBelow)
            {
                low = true;
            }
            if (energy < band.RechargeAbove)
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
    public static bool MechChargerReady(IReadOnlyList<RimGovernor.Contracts.Wire.Observations.MechChargerState> chargers)
    {
        foreach (var c in chargers)
        {
            if (c.Powered && !c.FullOfWaste)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the colony owes one more charger: a mechanitor exists and every standing charger is charging a mech
    /// (none standing counts as all busy). A charger full of waste owes nothing: emptying it is owned by waste
    /// management, not a reason to build. An idle unpowered
    /// charger owes nothing either, though MechChargerReady does not count it ready: the power planner covers every
    /// power consumer (PowerCoverage, connectLiveBeforeUpgrade), so wiring it is the power goal's and a second
    /// charger would stand unpowered too. Gestation waits on MechChargerReady meanwhile.
    /// </summary>
    public static bool MechChargerOwed(int mechanitors, IReadOnlyList<RimGovernor.Contracts.Wire.Observations.MechChargerState> chargers) =>
        mechanitors > 0 && chargers.All(c => c.ChargingMechId is not null && !c.FullOfWaste);
}
