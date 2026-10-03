package observation

import (
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// joyCatalog lists the joy buildings the test names: a buildable chess table
// (no power) and television (100 W), each behind research, that the
// television's job ranks first by joy a session, a billiards table the player
// cannot build and a horseshoes pin (watch-building giver, no power, no
// research) that is the recreation foothold.
func joyCatalog(t *testing.T) *bridge.DefinitionCatalog {
	t.Helper()
	building := func(name, kind string, research ...string) *d.ThingDef {
		return &d.ThingDef{DefName: name, Building: &d.BuildingProperties{JoyKind: kind}, ResearchPrerequisites: research}
	}
	planning := func(name string, powerW float64) *o.PlanningDefinition {
		row := &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String(name)}, Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(1)}}
		if powerW > 0 {
			row.PowerW = proto.Float64(powerW)
		}
		return row
	}
	v := &o.DefinitionCatalog{
		ThingDefs:   []*d.ThingDef{building("TubeTelevision", "Television", "TubeTelevision"), building("ChessTable", "Cerebral", "ComplexFurniture"), building("BilliardsTable", "Dexterity"), building("HorseshoesPin", "Dexterity")},
		Definitions: []*o.PlanningDefinition{planning("TubeTelevision", 100), planning("ChessTable", 0), planning("HorseshoesPin", 0)},
		ClassChains: []*o.ClassChain{{Name: "RimWorld.JoyGiver_WatchBuilding", Bases: []string{"RimWorld.JoyGiver"}}, {Name: "RimWorld.JoyGiver_InteractBuildingSitAdjacent", Bases: []string{"RimWorld.JoyGiver"}}},
		StatValues:  &o.DefStatTable{Rows: []*o.DefStatRow{{DefName: "TubeTelevision"}, {DefName: "ChessTable"}, {DefName: "BilliardsTable"}, {DefName: "HorseshoesPin"}}},
	}
	v.Defs = &d.DefSets{
		JobDefs: []*d.JobDef{{DefName: "Watch", JoyGainRate: 1, JoyDuration: 2000}, {DefName: "Play", JoyGainRate: .5, JoyDuration: 3000}, {DefName: "Toss", JoyGainRate: .1, JoyDuration: 3000}},
		JoyGiverDefs: []*d.JoyGiverDef{
			{DefName: "WatchTelevision", GiverClass: "RimWorld.JoyGiver_WatchBuilding", ThingDefs: []string{"TubeTelevision"}, JobDef: "Watch"},
			{DefName: "PlayChess", GiverClass: "RimWorld.JoyGiver_InteractBuildingSitAdjacent", ThingDefs: []string{"ChessTable"}, JobDef: "Play"},
			{DefName: "PlayHorseshoes", GiverClass: "RimWorld.JoyGiver_WatchBuilding", ThingDefs: []string{"HorseshoesPin"}, JobDef: "Toss"}},
	}
	return decodeCatalog(t, v)
}

func TestColonyProjectsRecreationKindsAndNativeBoredom(t *testing.T) {
	c := &o.ComfortFacts{People: []string{"p"}, Recreation: []*o.ComfortFacility{{Id: proto.String("pin"), Kind: proto.String("Dexterity"), AccessibleTo: bridge.NewRefs([]string{"p"})}}, Joy: &o.RecreationCensus{Kinds: []string{"Dexterity"}, Pawns: []*o.JoyTolerance{{Pawn: "p", Tolerance: []float64{.4}, Bored: []bool{true}}}}}
	wire := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Observed{Observed: c}}}}}}
	v, known := comfortOf(t, wire, joyCatalog(t)).Value()
	if !known || v.Recreation[0].Kind != "Dexterity" || v.Joy == nil || v.Joy.Pawns[0].Pawn != policy.PawnID("p") || v.Joy.Pawns[0].Tolerance[0] != .4 || !v.Joy.Pawns[0].Bored[0] {
		t.Fatal(v)
	}
	// The catalog's buildable joy defs, in the order of the joy a session gives, with the kind their
	// def gives and the power their planning row draws.
	want := []policy.JoyBuildingMethod{{Definition: "TubeTelevision", Kind: "Television", PowerW: 100}, {Definition: "ChessTable", Kind: "Cerebral"}, {Definition: "HorseshoesPin", Kind: "Dexterity"}}
	if !slices.Equal(v.Joy.Methods, want) {
		t.Fatal(v.Joy.Methods)
	}
	// The foothold is the one joy building needing neither power nor
	// research; the watch buildings are those a watch-building giver offers.
	if v.RecreationFoothold != "HorseshoesPin" || !slices.Equal(v.WatchBuildings, []string{"HorseshoesPin", "TubeTelevision"}) {
		t.Fatal(v.RecreationFoothold, v.WatchBuildings)
	}
	c.Joy.Pawns[0].Tolerance[0] = 0
	if v.Joy.Pawns[0].Tolerance[0] != .4 {
		t.Fatal("projection aliases native vectors")
	}
	if _, err := colonyComfort(wire, nil); err == nil {
		t.Fatal("a comfort census without a catalog was not an error")
	}
	catalog := joyCatalog(t)
	delete(catalog.ThingDefs, "ChessTable")
	if _, err := colonyComfort(wire, catalog); err == nil || !strings.Contains(err.Error(), "ChessTable") {
		t.Fatal("a buildable def without a row was not a named error", err)
	}
	c.Joy = nil
	v, _ = comfortOf(t, wire, joyCatalog(t)).Value()
	if v.Joy != nil {
		t.Fatal("unavailable census became complete")
	}
}

func comfortOf(t *testing.T, v *o.ColonyFactsSnapshot, catalog *bridge.DefinitionCatalog) domain.Fact[policy.ComfortObservation] {
	t.Helper()
	fact, err := colonyComfort(v, catalog)
	if err != nil {
		t.Fatal(err)
	}
	return fact
}
