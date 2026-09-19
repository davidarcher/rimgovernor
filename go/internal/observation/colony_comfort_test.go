package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestColonyProjectsRecreationKindsAndNativeBoredom(t *testing.T) {
	c := &o.ComfortFacts{People: []string{"p"}, Recreation: []*o.ComfortFacility{{Id: proto.String("pin"), Kind: proto.String("Dexterity"), AccessibleTo: []string{"p"}}}, Joy: &o.RecreationCensus{Kinds: []string{"Dexterity"}, Pawns: []*o.JoyTolerance{{Pawn: "p", Tolerance: []float64{.4}, Bored: []bool{true}}}, Methods: []*o.JoyBuildingMethod{{Definition: "ChessTable", Kind: "Cerebral"}}}}
	wire := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Observed{Observed: c}}}}}}
	v, known := colonyComfort(wire).Value()
	if !known || v.Recreation[0].Kind != "Dexterity" || v.Joy == nil || v.Joy.Pawns[0].Pawn != policy.PawnID("p") || v.Joy.Pawns[0].Tolerance[0] != .4 || !v.Joy.Pawns[0].Bored[0] || v.Joy.Methods[0].Definition != "ChessTable" {
		t.Fatal(v)
	}
	c.Joy.Pawns[0].Tolerance[0] = 0
	if v.Joy.Pawns[0].Tolerance[0] != .4 {
		t.Fatal("projection aliases native vectors")
	}
	c.Joy = nil
	v, _ = colonyComfort(wire).Value()
	if v.Joy != nil {
		t.Fatal("unavailable census became complete")
	}
}
