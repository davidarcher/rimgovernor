package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestBedStuffScoreUsesStuffFactors pins the stuffProps.statFactors Beauty
// (not the item's statBases Beauty) times market value from the game XML.
func TestBedStuffScoreUsesStuffFactors(t *testing.T) {
	for stuff, want := range map[Resource]float64{
		"Silver": 2, "Gold": 40, "Jade": 12.5, "Uranium": 3, "Plasteel": 9,
		"BlocksSandstone": 0.99, "BlocksGranite": 0.9, "BlocksMarble": 1.215,
	} {
		got, err := CoreItemFacts().StuffScore(stuff)
		if err != nil || math.Abs(got-want) > 1e-9 {
			t.Errorf("StuffScore(%s) = %v, %v; want %v", stuff, got, err, want)
		}
	}
}

func replacementBed(id, quality string, owners ...PawnID) SleepingBed {
	return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Room: domain.Known("Room_1"), Quality: domain.Known(quality), Owners: owners, Cell: domain.Cell{X: 2, Z: 3}}
}

func TestBedReplacementBuildsAssignsThenRemoves(t *testing.T) {
	low := RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	obs, rooms, _ := upgradeFixture(t, low)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	all := func(string) bool { return true }

	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Poor", "a")}
	u, ok := NextBedReplacement(obs, targets, rooms, all, BedMaterials{})
	if !ok || u.Step != BedReplaceBuild || u.Def != "Bed" {
		t.Fatalf("build = %+v %v", u, ok)
	}
	r := OccupiedRect(u.Cell, domain.Cell{X: 1, Z: 2}, u.Rot)
	if roomPieceOverlaps(rooms[0].Pieces, r) {
		t.Fatalf("new bed %v overlaps the old", r)
	}
	if _, ok := NextBedReplacement(obs, targets, rooms, func(d string) bool { return d != "Bed" }, BedMaterials{}); ok {
		t.Fatal("no bed buildable: nothing due")
	}

	// The new bed stands: the owner moves onto it.
	obs.Beds = append(obs.Beds, replacementBed("Bed_2", "Good"))
	if u, ok := NextBedReplacement(obs, targets, rooms, all, BedMaterials{}); !ok || u.Step != BedReplaceAssign || u.Pawn != "a" || u.Bed != "Bed_2" || u.PreviousBed != "Bed_1" {
		t.Fatalf("assign = %+v %v", u, ok)
	}

	// Moved: the old bed goes.
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Poor"), replacementBed("Bed_2", "Good", "a")}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, BedMaterials{}); !ok || u.Step != BedReplaceRemove || u.Bed != "Bed_1" || u.Def != "Bed" || u.Cell != (domain.Cell{X: 2, Z: 3}) {
		t.Fatalf("remove = %+v %v", u, ok)
	}

	// A new bed no better than the old is never moved onto: it goes.
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Poor", "a"), replacementBed("Bed_2", "Poor")}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, BedMaterials{}); !ok || u.Step != BedReplaceRemove || u.Bed != "Bed_2" {
		t.Fatalf("equal spare = %+v %v", u, ok)
	}
}

func TestBedReplacementLeavesGoodBedsAndMetTargets(t *testing.T) {
	low := RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	all := func(string) bool { return true }
	obs, rooms, _ := upgradeFixture(t, low)
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Normal", "a")}
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, BedMaterials{}); ok {
		t.Fatalf("normal bed = %+v", u)
	}
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Awful", "a")}
	targets["Room_1"] = RoomTarget{Room: "Room_1", Min: ImpressivenessMediocre}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, BedMaterials{}); ok {
		t.Fatalf("met target = %+v", u)
	}
}

func stuffBed(id, quality, stuff string, owners ...PawnID) SleepingBed {
	b := replacementBed(id, quality, owners...)
	b.Stuff = domain.Known(stuff)
	return b
}

func TestBedReplacementUpgradesMaterial(t *testing.T) {
	low := RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	obs, rooms, _ := upgradeFixture(t, low)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	all := func(string) bool { return true }
	materials := BedMaterials{Items: CoreItemFacts(), Stock: map[Resource]int64{"Silver": 100, "Steel": 100, "Gold": 10}, Cost: map[Resource]int64{"Bed": 45}}

	obs.Beds = []SleepingBed{stuffBed("Bed_1", "Normal", "WoodLog", "a")}
	u, ok := NextBedReplacement(obs, targets, rooms, all, materials)
	if !ok || u.Step != BedReplaceBuild || u.Def != "Bed" || u.Stuff != "Silver" {
		t.Fatalf("build = %+v %v", u, ok)
	}
	// The silver bed stands: the owner moves onto it, then the wood goes.
	obs.Beds = append(obs.Beds, stuffBed("Bed_2", "Normal", "Silver"))
	if u, ok := NextBedReplacement(obs, targets, rooms, all, materials); !ok || u.Step != BedReplaceAssign || u.Bed != "Bed_2" {
		t.Fatalf("assign = %+v %v", u, ok)
	}
	obs.Beds = []SleepingBed{stuffBed("Bed_1", "Normal", "WoodLog"), stuffBed("Bed_2", "Normal", "Silver", "a")}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, materials); !ok || u.Step != BedReplaceRemove || u.Bed != "Bed_1" {
		t.Fatalf("remove = %+v %v", u, ok)
	}
	// A better quality in a worse stuff is no improvement.
	obs.Beds = []SleepingBed{stuffBed("Bed_1", "Normal", "Silver", "a"), stuffBed("Bed_2", "Good", "WoodLog")}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, materials); !ok || u.Step != BedReplaceRemove || u.Bed != "Bed_2" {
		t.Fatalf("worse stuff spare = %+v %v", u, ok)
	}

	// Nothing better in stock, a NeverUpgrade room, an unknown stuff or
	// space weakest: nothing due.
	obs.Beds = []SleepingBed{stuffBed("Bed_1", "Normal", "Silver", "a")}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, materials); ok {
		t.Fatalf("best stocked = %+v", u)
	}
	obs.Beds = []SleepingBed{stuffBed("Bed_1", "Normal", "WoodLog", "a")}
	never := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive, NeverUpgrade: true}}
	if u, ok := NextBedReplacement(obs, never, rooms, all, materials); ok {
		t.Fatalf("never upgrade = %+v", u)
	}
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Normal", "a")}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, materials); ok {
		t.Fatalf("unknown stuff = %+v", u)
	}
	cramped, crooms, _ := upgradeFixture(t, RoomQuality{Wealth: 3000, Beauty: 5, Space: 5, Cleanliness: 0, Impressiveness: 35})
	cramped.Beds = []SleepingBed{stuffBed("Bed_1", "Normal", "WoodLog", "a")}
	if u, ok := NextBedReplacement(cramped, targets, crooms, all, materials); ok {
		t.Fatalf("space weakest = %+v", u)
	}

	// A poor-quality rebuild keeps at least the owned stuff.
	obs.Beds = []SleepingBed{stuffBed("Bed_1", "Poor", "Steel", "a")}
	steel := BedMaterials{Items: CoreItemFacts(), Stock: map[Resource]int64{"Steel": 45}, Cost: map[Resource]int64{"Bed": 45}}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, steel); !ok || u.Stuff != "Steel" {
		t.Fatalf("quality rebuild = %+v %v", u, ok)
	}
	wood := BedMaterials{Items: CoreItemFacts(), Stock: map[Resource]int64{"WoodLog": 450}, Cost: map[Resource]int64{"Bed": 45}}
	if u, ok := NextBedReplacement(obs, targets, rooms, all, wood); ok {
		t.Fatalf("quality rebuild in worse stuff = %+v", u)
	}
}
