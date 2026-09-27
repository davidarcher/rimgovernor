package policy

import "testing"

func knightTitle() *RoyalTitle {
	return &RoyalTitle{Definition: "Knight", BedroomThings: []BedroomThing{
		{AnyOf: []Resource{"RoyalBed"}, Count: 1},
		{AnyOf: []Resource{"EndTable"}, Count: 1},
		{AnyOf: []Resource{"Dresser"}, Count: 1},
	}}
}

func TestTitleRequiresRoyalBedRegardlessOfTarget(t *testing.T) {
	good := RoomQuality{Wealth: 3000, Beauty: 3, Space: 100, Cleanliness: 0, Impressiveness: 80}
	obs, rooms, _ := upgradeFixture(t, good)
	obs.People = []SleepingPerson{{ID: "a", Title: knightTitle()}}
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Good", "a")}
	all := func(string) bool { return true }
	u, ok := NextBedReplacement(obs, nil, rooms, all, BedMaterials{})
	if !ok || u.Step != BedReplaceBuild || u.Def != "RoyalBed" {
		t.Fatalf("royal build = %+v %v", u, ok)
	}
	royal := replacementBed("Bed_2", "Poor")
	royal.Definition = "RoyalBed"
	obs.Beds = append(obs.Beds, royal)
	if u, ok := NextBedReplacement(obs, nil, rooms, all, BedMaterials{}); !ok || u.Step != BedReplaceAssign || u.Bed != "Bed_2" {
		t.Fatalf("royal assign = %+v %v", u, ok)
	}
	royal.Owners = []PawnID{"a"}
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Good"), royal}
	if u, ok := NextBedReplacement(obs, nil, rooms, all, BedMaterials{}); !ok || u.Step != BedReplaceRemove || u.Bed != "Bed_1" {
		t.Fatalf("royal remove = %+v %v", u, ok)
	}
	// No royal bed researched: nothing.
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Good", "a")}
	if u, ok := NextBedReplacement(obs, nil, rooms, func(d string) bool { return d != "RoyalBed" }, BedMaterials{}); ok {
		t.Fatalf("unavailable = %+v", u)
	}
}

func TestTitleFurnitureFillsUnmetThings(t *testing.T) {
	good := RoomQuality{Wealth: 3000, Beauty: 3, Space: 100, Impressiveness: 80}
	obs, rooms, plan := upgradeFixture(t, good)
	obs.People = []SleepingPerson{{ID: "a", Title: knightTitle()}}
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Good", "a")}
	all := func(string) bool { return true }
	u, ok := NextTitleFurniture(obs, rooms, all)
	if !ok || u.Def != "EndTable" {
		t.Fatalf("end table = %+v %v", u, ok)
	}
	for _, p := range plan.Pieces {
		if p.Def == "EndTable" && p.Anchor() != u.Anchor {
			t.Fatalf("anchor %v, slot %v", u.Anchor, p.Anchor())
		}
	}
	rooms[0].Pieces = append(rooms[0].Pieces, TidyPiece{Def: "EndTable", Rect: Rectangle{u.Anchor.X, u.Anchor.Z, 1, 1}})
	if u, ok := NextTitleFurniture(obs, rooms, all); !ok || u.Def != "Dresser" {
		t.Fatalf("dresser = %+v %v", u, ok)
	}
	obs.People[0].Title = nil
	if u, ok := NextTitleFurniture(obs, rooms, all); ok {
		t.Fatalf("untitled = %+v", u)
	}
}
