package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// joyCatalog lists the joy buildings the test names: a buildable chess table
// (no power) and television (100 W) that the television's job ranks first by
// joy a session, a billiards table the player cannot build and a horseshoes
// pin whose def gives no joy.
func joyCatalog(t *testing.T) *bridge.DefinitionCatalog {
	t.Helper()
	building := func(name, kind string) *d.ThingDef {
		return &d.ThingDef{DefName: name, Building: &d.BuildingProperties{JoyKind: kind}}
	}
	planning := func(name string, powerW float64) *o.PlanningDefinition {
		row := &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String(name)}, Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(1)}}
		if powerW > 0 {
			row.PowerW = proto.Float64(powerW)
		}
		return row
	}
	v := &o.DefinitionCatalog{
		ThingDefs:   []*d.ThingDef{building("TubeTelevision", "Television"), building("ChessTable", "Cerebral"), building("BilliardsTable", "Dexterity"), building("HorseshoesPin", "")},
		Definitions: []*o.PlanningDefinition{planning("TubeTelevision", 100), planning("ChessTable", 0), planning("HorseshoesPin", 0)},
		StatValues:  &o.DefStatTable{Rows: []*o.DefStatRow{{DefName: "TubeTelevision"}, {DefName: "ChessTable"}, {DefName: "BilliardsTable"}, {DefName: "HorseshoesPin"}}},
	}
	v.Defs = &d.DefSets{
		JobDefs:      []*d.JobDef{{DefName: "Watch", JoyGainRate: 1, JoyDuration: 2000}, {DefName: "Play", JoyGainRate: .5, JoyDuration: 3000}},
		JoyGiverDefs: []*d.JoyGiverDef{{DefName: "WatchTelevision", ThingDefs: []string{"TubeTelevision"}, JobDef: "Watch"}, {DefName: "PlayChess", ThingDefs: []string{"ChessTable"}, JobDef: "Play"}},
	}
	return decodeCatalog(t, v)
}

func TestColonyProjectsRecreationKindsAndNativeBoredom(t *testing.T) {
	c := &o.ComfortFacts{People: []string{"p"}, Recreation: []*o.ComfortFacility{{Id: proto.String("pin"), Kind: proto.String("Dexterity"), AccessibleTo: bridge.NewRefs([]string{"p"})}}, Joy: &o.RecreationCensus{Kinds: []string{"Dexterity"}, Pawns: []*o.JoyTolerance{{Pawn: "p", Tolerance: []float64{.4}, Bored: []bool{true}}}}}
	wire := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Observed{Observed: c}}}}}}
	v, known := colonyComfort(wire, joyCatalog(t)).Value()
	if !known || v.Recreation[0].Kind != "Dexterity" || v.Joy == nil || v.Joy.Pawns[0].Pawn != policy.PawnID("p") || v.Joy.Pawns[0].Tolerance[0] != .4 || !v.Joy.Pawns[0].Bored[0] {
		t.Fatal(v)
	}
	// The catalog's buildable joy defs, in the order of the joy a session gives, with the kind their
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
	catalog := joyCatalog(t)
	delete(catalog.ThingDefs, "ChessTable")
	if v, known = colonyComfort(wire, catalog).Value(); known {
		t.Fatal("a buildable def without a row decided the census", v)
	}
	c.Joy = nil
	v, _ = colonyComfort(wire, joyCatalog(t)).Value()
	if v.Joy != nil {
		t.Fatal("unavailable census became complete")
	}
}
