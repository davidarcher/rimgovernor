package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The audited apparel ThoughtDefs (thought_triggers.tsv, dependency "apparel"):
// ApparelDamaged (the ratty and tattered stages, worn garment under half hit
// points), DeadMansApparel (tainted) and WrongApparelGender. MaintainEquipment
// owns them: the outfit excludes tainted gear, floors hit points at
// apparelMinHP, offers only gender-correct definitions (canWear), and the
// clothing runway replaces garments before they tatter.
var apparelThoughts = []string{"ApparelDamaged", "DeadMansApparel", "WrongApparelGender"}

func TestApparelThoughtsAreOwnedByEquipment(t *testing.T) {
	for _, def := range apparelThoughts {
		if owners := thoughtOwners(def); len(owners) != 1 || owners[0] != MaintainEquipment {
			t.Errorf("%s owners = %v", def, owners)
		}
	}
	pawns := []MoodLedgerPawn{ledgerPawn("a", MoodThought{"ApparelDamaged", -6}, MoodThought{"DeadMansApparel", -4}), ledgerPawn("b", MoodThought{"WrongApparelGender", -2})}
	l := BuildMoodLedger(pawns, nil)
	if len(l.Sources) != 3 || len(l.Unowned) != 0 {
		t.Fatalf("apparel thoughts unowned: %+v", l)
	}
	for _, s := range l.Sources {
		if len(s.Owners) != 1 || s.Owners[0] != MaintainEquipment {
			t.Errorf("%s ledger owners = %v", s.Def, s.Owners)
		}
	}
}

// A pawn losing mood to ratty apparel raises MaintainEquipment's deficit and,
// through the same worn garment, the clothing runway's needs; a pawn in good
// apparel raises neither.
func TestApparelThoughtRaisesEquipmentDeficitAndRunway(t *testing.T) {
	garments := clothingGarments(40, 30)
	ratty := wornShirt("Leather_Plain", .4)
	good := wornShirt("Leather_Plain", .9)

	p := moodPawn()
	p.Thoughts = domain.Known([]MoodThought{{"ApparelDamaged", -8}, {"Insulted", -1}})
	h := moodReview(t, p, MoodHistory{})
	deficit, raised := MoodProvisionDeficits(h)[MaintainEquipment]
	if !raised || deficit != 1 {
		t.Fatalf("ratty apparel deficit = %v, %v", deficit, raised)
	}
	if got := PlanClothingRunway(clothingInput([]GearOption{ratty}, nil, garments)).Needs; got["Leather_Light"] != 40 {
		t.Fatalf("ratty worn shirt runway: %v", got)
	}

	p.Thoughts = domain.Known([]MoodThought{{"Insulted", -8}})
	if _, raised := MoodProvisionDeficits(moodReview(t, p, MoodHistory{}))[MaintainEquipment]; raised {
		t.Fatal("a pawn without an apparel thought raised the equipment deficit")
	}
	if got := PlanClothingRunway(clothingInput([]GearOption{good}, nil, garments)).Needs; len(got) != 0 {
		t.Fatalf("good apparel asked for replacements: %v", got)
	}
}
