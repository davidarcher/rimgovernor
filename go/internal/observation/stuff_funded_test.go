package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestStuffFundedIsStockNetOfHolds(t *testing.T) {
	p := ColonyProjection{Definitions: []PlanningDefinition{{Name: "Sarcophagus", Stuffed: true, StuffOptions: []StuffOption{
		{Stuff: "BlocksGranite", Costs: []policy.Amount{{Resource: "BlocksGranite", Count: 60}}},
		{Stuff: "WoodLog", Costs: []policy.Amount{{Resource: "WoodLog", Count: 60}}},
	}}}}
	if p.StuffFunded("Sarcophagus", nil) {
		t.Fatal("unread stock funded")
	}
	p.Resources = domain.Known(map[policy.Resource]int64{"WoodLog": 100})
	if !p.StuffFunded("Sarcophagus", nil) {
		t.Fatal("100 wood left the sarcophagus unfunded")
	}
	if p.StuffFunded("Sarcophagus", map[policy.Resource]int64{"WoodLog": 50}) {
		t.Fatal("holds were not netted out")
	}
	if p.StuffFunded("Missing", nil) {
		t.Fatal("unknown definition funded")
	}
}
