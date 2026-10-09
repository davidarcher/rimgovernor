package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A Met owner with a stale bill is filed Unmet and raised so its planner
// removes the bill; an Unmet or unknown owner, or another owner's
// stale bill, changes nothing.
func TestAssessFilesAMetOwnerWithAStaleBillUnmet(t *testing.T) {
	t.Parallel()
	stale := []StaleBill{{Owner: MaintainArt, Bench: "bench", ID: "Bill_1"}}
	cases := []struct {
		name      string
		bills     []StaleBill
		recovered domain.Fact[bool]
		finding   domain.Finding
		raised    int
	}{
		{"met with a stale bill", stale, domain.Known(true), domain.FindingUnmet, 1},
		{"met without one", nil, domain.Known(true), domain.FindingMet, 0},
		{"met, another owner's bill", []StaleBill{{Owner: MaintainEquipment, ID: "Bill_2"}}, domain.Known(true), domain.FindingMet, 0},
		{"unmet", stale, domain.Known(false), domain.FindingUnmet, 0},
		{"unclear", stale, domain.Unknown[bool](), domain.FindingUnclear, 0},
	}
	for _, tc := range cases {
		c := &roundsRun{f: RoundsFacts{StaleBills: tc.bills}}
		a := c.assess(MaintainArt, 3, tc.recovered)
		if a.Finding != tc.finding || a.StaleBill != (tc.raised == 1) || len(c.r.Concerns) != tc.raised {
			t.Errorf("%s: finding %v stale %v concerns %d", tc.name, a.Finding, a.StaleBill, len(c.r.Concerns))
		}
	}
}

// Food is filed Unmet for no bill (that would start the food machinery) but its
// hunter-weapon bills are removed once outside the demand; gestation bills are
// excluded from both rules.
func TestStaleBillOwners(t *testing.T) {
	t.Parallel()
	for _, id := range []ConcernID{MaintainMechs, EnsureFoodSupply, MaintainFoodStorage} {
		if StaleBillOwner(id) {
			t.Errorf("%s is filed Unmet for a stale bill", id)
		}
	}
	for _, id := range []ConcernID{MaintainMechs, MaintainFoodStorage} {
		if UnwantedBillOwner(id) {
			t.Errorf("%s removes unwanted bills", id)
		}
	}
	for _, id := range []ConcernID{MaintainEquipment, MaintainArt, MaintainSurgery} {
		if !StaleBillOwner(id) || !UnwantedBillOwner(id) {
			t.Errorf("%s does not remove stale bills", id)
		}
	}
	if !UnwantedBillOwner(EnsureFoodSupply) {
		t.Error("food does not remove unwanted hunter-weapon bills")
	}
}

// The gear wanted set is the replacements of every loadout, armor families by
// rung, and nothing once the census is recovered; an unread census is unknown.
func TestGearBillsWanted(t *testing.T) {
	t.Parallel()
	parka := loadoutOption("Parka", GearSkinTorso)
	wanted, known, err := GearBillsWanted(domain.Known(GearObservation{Pawns: []GearPawn{gearDeficitPawn("pawn", parka)}}))
	if err != nil || !known || !wanted["Parka"] || len(wanted) != 1 {
		t.Fatalf("deficit census: %v %v %v", wanted, known, err)
	}
	wanted, known, err = GearBillsWanted(domain.Known(GearObservation{Pawns: []GearPawn{gearDressedPawn("pawn", parka)}}))
	if err != nil || !known || len(wanted) != 0 {
		t.Fatalf("recovered census: %v %v %v", wanted, known, err)
	}
	if _, known, err = GearBillsWanted(domain.Unknown[GearObservation]()); err != nil || known {
		t.Fatalf("unread census: %v %v", known, err)
	}
	jacket := loadoutOption("Apparel_FlakJacket", GearOuter)
	wanted, known, err = GearBillsWanted(domain.Known(GearObservation{Pawns: []GearPawn{gearDeficitPawn("pawn", jacket)}}))
	if err != nil || !known || !wanted["Apparel_FlakJacket"] || !wanted["Apparel_FlakVest"] || wanted["Apparel_ArmorRecon"] {
		t.Fatalf("armor family rungs: %v %v %v", wanted, known, err)
	}
}

// A bill is a weapon bill when the armory ladder models a product, and wanted
// when any product is in the set.
func TestBillWantedAndWeaponBill(t *testing.T) {
	t.Parallel()
	if !WeaponBill([]Resource{"Bow_Short"}) || WeaponBill([]Resource{"Apparel_Parka"}) || WeaponBill(nil) {
		t.Error("weapon classification")
	}
	wanted := WeaponsWanted([]Amount{{Resource: "Bow_Short", Count: 2}}, []Amount{{Resource: "Gun_Revolver", Count: 0}})
	if !BillWanted([]Resource{"Bow_Short"}, wanted) || BillWanted([]Resource{"Gun_Revolver"}, wanted) || BillWanted(nil, wanted) {
		t.Errorf("wanted = %v", wanted)
	}
	items := SurgeryPartsWanted([]SurgeryPart{{Items: []Resource{"SimpleProstheticLeg"}}})
	if !BillWanted([]Resource{"SimpleProstheticLeg"}, items) || BillWanted([]Resource{"Silver"}, items) {
		t.Errorf("part items = %v", items)
	}
}
