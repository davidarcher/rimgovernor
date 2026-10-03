package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// energyMech is a mech at the given energy in a 0.3-0.7 recharge band.
func energyMech(id string, group int, mode string, energy float64) MechInput {
	m := mech(id, "Mech_Lifter", group, mode)
	m.Energy, m.RechargeBelow, m.RechargeAbove = domain.Known(energy), domain.Known(0.3), domain.Known(0.7)
	return m
}

func TestMechRechargeMovesALowGroupToRechargeWhenAChargerIsReady(t *testing.T) {
	in := []MechInput{energyMech("A", 0, "Work", 0.2), energyMech("B", 0, "Work", 0.9)}
	got, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(1)}, in, false, true)
	if err != nil {
		t.Fatal(err)
	}
	if r := rows(t, got); !equalRows(r, []settingRow{{pawn: "A", mode: "Recharge"}}) {
		t.Fatal("one low mech sends its group to Recharge", r)
	}
	if got, err = PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(1)}, in, false, false); err != nil || len(got) != 0 {
		t.Fatal("no ready charger leaves the group working", got, err)
	}
}

func TestMechRechargeHoldsUntilTheBandTopThenReturnsToRole(t *testing.T) {
	charging := []MechInput{energyMech("A", 0, "Recharge", 0.5), energyMech("B", 0, "Recharge", 0.9)}
	if got, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(1)}, charging, false, true); err != nil || len(got) != 0 {
		t.Fatal("a mech still under the band top keeps the group charging", got, err)
	}
	full := []MechInput{energyMech("A", 0, "Recharge", 0.7), energyMech("B", 0, "Recharge", 0.9)}
	got, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(1)}, full, false, true)
	if err != nil {
		t.Fatal(err)
	}
	if r := rows(t, got); !equalRows(r, []settingRow{{pawn: "A", mode: "Work"}}) {
		t.Fatal("a recovered group returns to its role mode", r)
	}
	if got, err = PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(1)}, charging, false, false); err != nil || len(rows(t, got)) != 1 {
		t.Fatal("with no charger ready a charging group returns to work", got, err)
	}
}

func TestMechRechargeNeverGuessesFromUnreadEnergy(t *testing.T) {
	unread := mech("A", "Mech_Lifter", 0, "Work")
	if got, err := PlanMechControl(mechCatalog(), []MechanitorInput{mechanitor(1)}, []MechInput{unread}, false, true); err != nil || len(got) != 0 {
		t.Fatal("unread energy is not low", got, err)
	}
}

func TestMechRechargeNeedsTheCatalogRole(t *testing.T) {
	catalog := mechCatalog()
	catalog.Recharge = ""
	if _, err := PlanMechControl(catalog, []MechanitorInput{mechanitor(1)}, nil, false, true); err == nil {
		t.Fatal("a catalog with no recharge role must fail loudly")
	}
}

func TestMechChargerReadyAndOwed(t *testing.T) {
	yes, no := domain.Known(true), domain.Known(false)
	idle := MechCharger{Powered: yes, FullOfWaste: no, Charging: no}
	busy := MechCharger{Powered: yes, FullOfWaste: no, Charging: yes}
	full := MechCharger{Powered: yes, FullOfWaste: yes, Charging: no}
	dark := MechCharger{Powered: no, FullOfWaste: no, Charging: no}
	if !MechChargerReady([]MechCharger{dark, idle}) || MechChargerReady([]MechCharger{dark, full}) || MechChargerReady(nil) {
		t.Fatal("ready is a powered charger not full of waste")
	}
	cases := []struct {
		name      string
		mechanits int
		chargers  domain.Fact[[]MechCharger]
		owed      bool
	}{
		{"none standing", 1, domain.Known([]MechCharger{}), true},
		{"all busy", 1, domain.Known([]MechCharger{busy, busy}), true},
		{"one idle", 1, domain.Known([]MechCharger{busy, idle}), false},
		{"full of waste is #1683's", 1, domain.Known([]MechCharger{busy, full}), false},
		{"no mechanitor", 0, domain.Known([]MechCharger{}), false},
		{"unread list", 1, domain.Unknown[[]MechCharger](), false},
		{"unread charging", 1, domain.Known([]MechCharger{{Powered: yes, FullOfWaste: no}}), false},
	}
	for _, c := range cases {
		if got := MechChargerOwed(c.mechanits, c.chargers); got != c.owed {
			t.Fatalf("%s: owed %v, want %v", c.name, got, c.owed)
		}
	}
}
