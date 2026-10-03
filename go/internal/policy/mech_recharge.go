package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Mech recharging (#1688, epic #1667). Mechs spend Need_MechEnergy working
// and refill it on a charger (Building_MechCharger, one mech at a time).
// Each control group carries its own recharge band (mechRechargeThresholds,
// read natively per mech as PawnMech.RechargeBelow/RechargeAbove): the game
// sends a mech to a charger below the band's lower bound. The bot reuses
// that band instead of a number of its own:
//   - a group whose mech is below its lower bound moves to the Recharge
//     mode (the catalog's MechWorkModeDefOf.Recharge row), provided a
//     charger is ready, so no mech returns to work half charged;
//   - a group in Recharge stays there until every mech is at or above the
//     band's upper bound (or no charger is ready), then returns to the role
//     mode PlanMechControl would give it.
//
// Chargers come before more mechs: MechChargerOwed answers whether the
// colony needs one more before gestation (#1686) adds a mech. Charger waste
// disposal is #1683's.

// MechRechargeMode is the mode a planned group should run: the catalog's
// Recharge mode while the group is low (or still charging up), the group's
// role mode otherwise. A mech whose energy or band is unread never makes
// the group low; a charging group with an unread mech stays charging.
func MechRechargeMode(catalog MechCatalog, g mechGroup, all []MechInput, chargerReady bool) string {
	if !chargerReady || catalog.Recharge == "" {
		return g.mode
	}
	charging := g.holds(all, catalog.Recharge)
	low, recovered := false, true
	for _, mech := range g.mechs {
		energy, ek := mech.Energy.Value()
		below, bk := mech.RechargeBelow.Value()
		above, ak := mech.RechargeAbove.Value()
		if !ek || !bk || !ak {
			recovered = false
			continue
		}
		if energy < below {
			low = true
		}
		if energy < above {
			recovered = false
		}
	}
	if low || charging && !recovered {
		return catalog.Recharge
	}
	return g.mode
}

// MechCharger is one charger's state as the colony section reads it.
type MechCharger struct {
	Powered, FullOfWaste, Charging domain.Fact[bool]
}

// MechChargerReady is whether some charger is powered and not full of
// waste (Building_MechCharger.IsFullOfWaste stops it charging).
func MechChargerReady(chargers []MechCharger) bool {
	for _, c := range chargers {
		powered, pk := c.Powered.Value()
		full, fk := c.FullOfWaste.Value()
		if pk && powered && fk && !full {
			return true
		}
	}
	return false
}

// MechChargerOwed reports whether the colony owes one more charger: a
// mechanitor exists and every standing charger is charging a mech (none
// standing counts as all busy). An unread charger list or charging state
// owes nothing, and neither does a charger full of waste: emptying it is
// #1683's, not a reason to build.
func MechChargerOwed(mechanitors int, chargers domain.Fact[[]MechCharger]) bool {
	rows, known := chargers.Value()
	if !known || mechanitors == 0 {
		return false
	}
	for _, c := range rows {
		busy, ok := c.Charging.Value()
		full, fk := c.FullOfWaste.Value()
		if !ok || !busy || !fk || full {
			return false
		}
	}
	return true
}
