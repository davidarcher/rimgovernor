package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// joyCatalog lists the joy buildings of policy.RecreationDefinitions that the
// test names: a buildable chess table (no power), a buildable television
// (100 W), a billiards table the player cannot build and a horseshoes pin
// whose def gives no joy.
func joyCatalog() *bridge.DefinitionCatalog {
	building := func(name, kind string) *d.ThingDef {
		return &d.ThingDef{DefName: name, Building: &d.BuildingProperties{JoyKind: kind}}
	}
	return &bridge.DefinitionCatalog{
		Definitions: map[string]*o.PlanningDefinition{
			"TubeTelevision": {PowerW: proto.Float64(100)},
			"ChessTable":     {},
			"HorseshoesPin":  {},
		},
		ThingDefs: map[string]*d.ThingDef{
			"TubeTelevision": building("TubeTelevision", "Television"),
			"ChessTable":     building("ChessTable", "Cerebral"),
			"BilliardsTable": building("BilliardsTable", "Dexterity"),
			"HorseshoesPin":  building("HorseshoesPin", ""),
		},
	}
}

func TestColonyProjectsRecreationKindsAndNativeBoredom(t *testing.T) {
	c := &o.ComfortFacts{People: []string{"p"}, Recreation: []*o.ComfortFacility{{Id: proto.String("pin"), Kind: proto.String("Dexterity"), AccessibleTo: bridge.NewRefs([]string{"p"})}}, Joy: &o.RecreationCensus{Kinds: []string{"Dexterity"}, Pawns: []*o.JoyTolerance{{Pawn: "p", Tolerance: []float64{.4}, Bored: []bool{true}}}}}
	wire := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Observed{Observed: c}}}}}}
	v, known := colonyComfort(wire, joyCatalog()).Value()
	if !known || v.Recreation[0].Kind != "Dexterity" || v.Joy == nil || v.Joy.Pawns[0].Pawn != policy.PawnID("p") || v.Joy.Pawns[0].Tolerance[0] != .4 || !v.Joy.Pawns[0].Bored[0] {
		t.Fatal(v)
	}
	// The catalog's buildable joy defs, in policy order, with the kind their
	// def gives and the power their planning row draws.
	want := []policy.JoyBuildingMethod{{Definition: "TubeTelevision", Kind: "Television", PowerW: 100}, {Definition: "ChessTable", Kind: "Cerebral"}}
	if len(v.Joy.Methods) != len(want) || v.Joy.Methods[0] != want[0] || v.Joy.Methods[1] != want[1] {
		t.Fatal(v.Joy.Methods)
	}
	c.Joy.Pawns[0].Tolerance[0] = 0
	if v.Joy.Pawns[0].Tolerance[0] != .4 {
		t.Fatal("projection aliases native vectors")
	}
	if v, _ = colonyComfort(wire, nil).Value(); len(v.Joy.Methods) != 0 {
		t.Fatal("methods without a catalog", v.Joy.Methods)
	}
	catalog := joyCatalog()
	delete(catalog.ThingDefs, "ChessTable")
	if v, known = colonyComfort(wire, catalog).Value(); known {
		t.Fatal("a buildable def without a row decided the census", v)
	}
	c.Joy = nil
	v, _ = colonyComfort(wire, joyCatalog()).Value()
	if v.Joy != nil {
		t.Fatal("unavailable census became complete")
	}
}
