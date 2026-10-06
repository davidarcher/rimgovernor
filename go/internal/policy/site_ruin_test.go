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
