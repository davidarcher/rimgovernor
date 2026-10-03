package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A tribal start with leather and Bed locked stages bedrolls on the
// shelter-beds rung (#1181), as many as the leather covers, in the first
// stuff option in stock.
func TestShelterBedsStageBedrollsFromStockedLeather(t *testing.T) {
	bedroll := observation.PlanningDefinition{Name: "Bedroll", Available: domain.Known(true), Stuffed: true, StuffOptions: []observation.StuffOption{
		{Stuff: "Cloth", Costs: []policy.Amount{{Resource: "Cloth", Count: 40}}},
		{Stuff: "Leather_Plain", Costs: []policy.Amount{{Resource: "Leather_Plain", Count: 40}}},
	}}
	facts := observation.ColonyProjection{
		Definitions: []observation.PlanningDefinition{{Name: "Bed", Available: domain.Known(false)}, bedroll},
		Resources:   domain.Known(map[policy.Resource]int64{"Leather_Plain": 95, "Cloth": 10}),
	}
	anchors := []domain.Cell{{X: 1}, {X: 2}, {X: 3}}
	definition, got := shelterBeds(facts, anchors)
	if definition != "Bedroll" || len(got) != 2 {
		t.Fatal(definition, got)
	}
	if stuff := bedStuff(facts, definition); stuff != "Leather_Plain" {
		t.Fatal(stuff)
	}
	// No stuff on hand: the rung stays on Bed, which admitBunks refuses.
	facts.Resources = domain.Known(map[policy.Resource]int64{"Cloth": 39})
	if definition, got = shelterBeds(facts, anchors); definition != "Bed" || len(got) != 3 {
		t.Fatal(definition, got)
	}
	// Bed buildable: beds, whatever the stock.
	facts.Definitions[0].Available = domain.Known(true)
	facts.Resources = domain.Known(map[policy.Resource]int64{"Leather_Plain": 400})
	if definition, got = shelterBeds(facts, anchors); definition != "Bed" || len(got) != 3 {
		t.Fatal(definition, got)
	}
}
