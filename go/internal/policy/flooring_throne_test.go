package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The throne room's floor (#1863): a room carrying RequiredTags is measured
// against the terrain tags, so natural ground and a coarse floor are both
// deficient, and the floor chosen is any known available terrain carrying one
// of the tags (here "FineFloor"), whatever its name.
func TestFlooringThroneTier(t *testing.T) {
	terrains := flooringTerrains()
	terrains["Carpet"] = FloorTerrain{Beauty: 1, Tags: []string{"Carpet", "FineFloor"}}
	terrains["Gold"] = FloorTerrain{Beauty: 5, Tags: []string{"FineFloor"}}
	terrains["WoodPlankFloor"] = FloorTerrain{Flammability: 0.22, Tags: []string{"Wood"}}
	room := func(terrain string) FloorRoom {
		r := flooringRoom("throne", RoomRoleThroneRoom, terrain, 1, 1)
		r.RequiredTags, r.RequiredLabel = []string{"FineFloor"}, "RoomRequirementAllFineFloored"
		return r
	}
	review := func(terrain string) FlooringReview {
		r, err := ReviewFlooring(domain.Known(FlooringObservation{Rooms: []FloorRoom{room(terrain)}, Terrains: terrains}), domain.Unknown[RoomObservation](), nil, flooringPolicy())
		if err != nil {
			t.Fatal(err)
		}
		return r
	}
	facts := flooringDefinitions()
	facts.Style = nil
	amount := domain.Known([]Amount{{Resource: "WoodLog", Count: 2}})
	facts.Definitions["Carpet"] = FloorDefinition{Available: domain.Known(true), Terrain: domain.Known(true), Beauty: domain.Known(1.0), Flammability: domain.Known(0.0), PathCost: domain.Known[int32](0), Costs: amount, Tags: []string{"Carpet", "FineFloor"}}
	facts.Definitions["Gold"] = FloorDefinition{Available: domain.Known(false), Terrain: domain.Known(true), Beauty: domain.Known(5.0), PathCost: domain.Known[int32](0), Costs: amount, Tags: []string{"FineFloor"}}
	for _, from := range []string{"Soil", "WoodPlankFloor"} {
		r := review(from)
		if len(r.Deficits) != 1 || r.Deficits[0].Tier != FloorTierThrone || len(r.Deficits[0].Cells) != 6 {
			t.Fatal(from, r)
		}
		got, err := SelectFlooringMethod(r, facts, flooringPolicy())
		if err != nil || got.Method != FlooringBuild || got.Definition != "Carpet" || got.Tier != FloorTierThrone || len(got.Cells) != 6 {
			t.Fatal(from, got, err)
		}
	}
	for _, from := range []string{"Carpet", "Gold"} {
		if r := review(from); r.Active {
			t.Fatal(from, r)
		}
	}
	// No terrain carries the tag: the gap is reported, not guessed around.
	delete(facts.Definitions, "Carpet")
	delete(facts.Definitions, "Gold")
	got, err := SelectFlooringMethod(review("Soil"), facts, flooringPolicy())
	if err != nil || got.Method != FlooringUnknown {
		t.Fatal(got, err)
	}
}
