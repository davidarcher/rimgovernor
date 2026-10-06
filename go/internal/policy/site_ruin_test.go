package policy

import "testing"

// ruinThings is the things of an unowned edifice of def: claimable when def
// is named, under hold (ancient_danger or casket) when set.
func ruinThings(def, hold string) []Thing {
	t := Thing{Def: def, Category: ThingBuilding, Flags: FlagEdifice | FlagImpassable | FlagDeconstructible, Count: 1, Building: &BuildingState{}}
	if def != "" {
		t.Flags |= FlagClaimable
	}
	switch hold {
	case "ancient_danger":
		t.Flags |= FlagAncientDanger
	case "casket":
		t.Building.Casket = []string{"Human"}
	}
	return []Thing{t}
}

// TestSiteCellOccupiedAndNaturalRock is the native CellOccupied (an edifice,
// a blueprint or a frame) and the natural-rock edifice, per thing category.
func TestSiteCellOccupiedAndNaturalRock(t *testing.T) {
	building := func(f ThingFlags) Thing {
		return Thing{Def: "Wall", Category: ThingBuilding, Flags: f, Building: &BuildingState{}}
	}
	cases := []struct {
		name           string
		things         []Thing
		occupied, rock bool
	}{
		{"empty cell", nil, false, false},
		{"edifice", []Thing{building(FlagEdifice)}, true, false},
		{"natural rock", []Thing{building(FlagEdifice | FlagNaturalRock)}, true, true},
		{"blueprint", []Thing{building(FlagBlueprint)}, true, false},
		{"frame", []Thing{building(FlagFrame)}, true, false},
		{"building that is no edifice", []Thing{building(0)}, false, false},
		{"plant", []Thing{{Def: "Plant_Rice", Category: ThingPlant, Flags: FlagImpassable}}, false, false},
		{"item", []Thing{{Def: "Steel", Category: ThingItem, Flags: FlagHaulable, Count: 10}}, false, false},
		{"filth", []Thing{{Def: "Filth_Dirt", Category: ThingFilth}}, false, false},
		{"item beside an edifice", []Thing{{Def: "Steel", Category: ThingItem}, building(FlagEdifice)}, true, false},
		{"rock flag off the edifice", []Thing{building(FlagNaturalRock)}, false, false},
	}
	for _, c := range cases {
		cell := SiteCell{Things: c.things}
		if cell.Occupied() != c.occupied || cell.NaturalRock() != c.rock {
			t.Errorf("%s: occupied %v rock %v, want %v %v", c.name, cell.Occupied(), cell.NaturalRock(), c.occupied, c.rock)
		}
	}
	held := SiteCell{Things: append(RockThings(true), Thing{Def: "Steel", Category: ThingItem})}
	if cleared := held.Cleared(); cleared.Occupied() || cleared.NaturalRock() || len(cleared.Things) != 1 || len(held.Things) != 2 {
		t.Errorf("Cleared must drop the occupants and leave the original: %+v", cleared)
	}
}

func TestSiteCellEdificePredicates(t *testing.T) {
	cases := []struct {
		name                   string
		things                 []Thing
		ruin                   bool
		claimable, hold, owned string
	}{
		{"none", nil, false, "", "", ""},
		{"claimable ruin", ruinThings("Wall", ""), true, "Wall", "", ""},
		{"ruin not claimable", ruinThings("", ""), true, "", "", ""},
		{"ancient danger", ruinThings("Wall", "ancient_danger"), false, "", "ancient_danger", ""},
		{"casket", ruinThings("Wall", "casket"), true, "Wall", "casket", ""},
		{"player wall", []Thing{{Def: "Wall", Category: ThingBuilding, Faction: FactionPlayer, Flags: FlagEdifice | FlagDeconstructible | FlagClaimable, Building: &BuildingState{}}}, false, "", "", "Wall"},
		{"blueprint is no edifice", []Thing{{Def: "Wall", Category: ThingBuilding, Faction: FactionPlayer, Flags: FlagBlueprint, Building: &BuildingState{}}}, false, "", "", ""},
	}
	for _, c := range cases {
		cell := SiteCell{Things: c.things}
		if cell.Ruin() != c.ruin || cell.ClaimableRuin() != c.claimable || cell.RuinHold() != c.hold || cell.PlayerEdifice() != c.owned {
			t.Errorf("%s: ruin %v claimable %q hold %q owned %q", c.name, cell.Ruin(), cell.ClaimableRuin(), cell.RuinHold(), cell.PlayerEdifice())
		}
	}
}
