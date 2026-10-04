package buildingruntime

import (
	"os"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/encoding/prototext"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// The full throne requirement set end to end (#1866): the Knight row of the
// recorded def mirror (area, impressiveness, throne, floors, two braziers, two
// columns, an instrument, glowing, forbidden buildings) becomes the need
// through the review's own path (WithTitleDefs, NextThroneNeed), and
// each unmet requirement of a standing room plans its fix through throneStep,
// withThroneFloor and withThroneTargets, the planners' views of the snapshot.

// knightMirror is a catalog holding the recorded Empire titles and the
// building rows the forbidden-building tags resolve over.
func knightMirror(t *testing.T) *bridge.DefinitionCatalog {
	t.Helper()
	raw, err := os.ReadFile("../bridge/testdata/royal_title_defs.textproto")
	if err != nil {
		t.Fatal(err)
	}
	var sets d.DefSets
	if err := prototext.Unmarshal(raw, &sets); err != nil {
		t.Fatal(err)
	}
	titles := map[string]proto.Message{}
	for _, row := range sets.GetRoyalTitleDefs() {
		titles[row.GetDefName()] = row
	}
	tagged := func(tags ...string) *d.ThingDef {
		return &d.ThingDef{Building: &d.BuildingProperties{BuildingTags: tags}}
	}
	return &bridge.DefinitionCatalog{
		Defs: map[protoreflect.FullName]map[string]proto.Message{(&d.RoyalTitleDef{}).ProtoReflect().Descriptor().FullName(): titles},
		ThingDefs: map[string]*d.ThingDef{
			"Bed": tagged("Bed"), "TableButcher": tagged("Production"),
			"Throne": {}, "Brazier": {}, "Column": {}, "Harp": {},
		},
		TerrainDefs: map[string]*d.TerrainDef{"Soil": {}, "Carpet": {Tags: []string{"FineFloor"}}},
	}
}

// knightSnapshot is a colony whose Yeoman Alice is owed the Knight's room: a
// 6x5 planned throne room standing, nothing built in it yet, every furnishing
// definition available and a hauler at hand.
func knightSnapshot(t *testing.T) (observation.ColonyProjection, policy.LayoutRoom, *bridge.DefinitionCatalog) {
	t.Helper()
	catalog := knightMirror(t)
	royalty, err := catalog.WithTitleDefs(policy.RoyaltyFacts{
		Holders: map[policy.PawnID][]policy.RoyalHolding{"Alice": {{FactionDef: "Empire", Title: "Yeoman", Favor: domain.Known(2)}}},
	})
	if err != nil {
		t.Fatal(err)
	}
	facts, room := throneProjection(true)
	facts.Royalty = domain.Known(royalty)
	one := domain.Known(policy.Bounds{Width: 1, Height: 1})
	facts.Definitions = nil
	for _, name := range []string{"Throne", "Brazier", "Column", "Harp"} {
		facts.Definitions = append(facts.Definitions, observation.PlanningDefinition{Name: name, Available: domain.Known(true), Size: one})
	}
	facts.WorkPawns = domain.Known([]policy.WorkPawn{{ID: "hauler", Available: domain.Known(true), Applies: domain.Known(true),
		Work: domain.Known([]policy.WorkPriority{{Work: policy.WorkHauling, Priority: 2}})}})
	facts.Facts.Upkeep.Lighting = domain.Known(policy.LightingObservation{})
	return facts, room, catalog
}

// standing adds a building of def at cell to the construction census.
func standing(t *testing.T, facts *observation.ColonyProjection, id, def string, cell domain.Cell) {
	t.Helper()
	building, err := domain.NewBuilding(def, cell, domain.South, "")
	if err != nil {
		t.Fatal(err)
	}
	census, _ := facts.Facts.CurrentConstruction.Value()
	census.Buildings = append(census.Buildings, policy.CurrentBuilding{ID: id, Building: building, Cells: []domain.Cell{cell}})
	facts.Facts.CurrentConstruction = domain.Known(census)
}

// furnish places every piece throneStep asks for until it asks for none
// and returns what was placed.
func furnish(t *testing.T, facts *observation.ColonyProjection) map[string]int {
	t.Helper()
	placed := map[string]int{}
	for i := 0; i < 12; i++ {
		step := throneStep(*facts)
		if step.Kind != policy.ThronePlace {
			return placed
		}
		placed[step.Piece.Def]++
		standing(t, facts, step.Piece.Def+string(rune('a'+i)), step.Piece.Def, step.Piece.Anchor())
		if step.Piece.Def == "Throne" {
			royalty, _ := facts.Royalty.Value()
			royalty.Thrones = []policy.RoyalThrone{{ID: "Throne" + string(rune('a'+i)), Def: "Throne", Owner: "Alice"}}
			facts.Royalty = domain.Known(royalty)
		}
	}
	t.Fatal("furnishing never ended")
	return nil
}

// without drops the n-th building of def from the census.
func without(facts *observation.ColonyProjection, def string, n int) {
	census, _ := facts.Facts.CurrentConstruction.Value()
	census.Buildings = slices.Clone(census.Buildings)
	for i, b := range census.Buildings {
		if b.Building.Definition() != def {
			continue
		}
		if n == 0 {
			census.Buildings = slices.Delete(census.Buildings, i, i+1)
			break
		}
		n--
	}
	facts.Facts.CurrentConstruction = domain.Known(census)
}

func TestThroneRoomMissingAPieceStaysOwedUntilFurnished(t *testing.T) {
	facts, room, _ := knightSnapshot(t)
	need, ok := throneNeed(facts)
	if !ok || need.Title != "Knight" || need.MinArea != 30 || len(need.AnyOfCounts) != 1 || len(need.Counts) != 1 || len(need.AnyOf) != 1 || len(need.Glowing) != 1 {
		t.Fatalf("the Knight's full requirement set is the need: %+v %v", need, ok)
	}
	placed := furnish(t, &facts)
	if placed["Throne"] != 1 || placed["Brazier"] != 2 || placed["Column"] != 2 || placed["Harp"] != 1 {
		t.Fatalf("a bare room is furnished to the title: %v", placed)
	}
	if step := throneStep(facts); step.Owed() || step.Kind == policy.ThroneBlocked {
		t.Fatalf("a complete room owes nothing: %+v", step)
	}
	for _, c := range []struct{ missing, def string }{{"brazier", "Brazier"}, {"column", "Column"}, {"instrument", "Harp"}} {
		t.Run(c.missing, func(t *testing.T) {
			short := facts
			without(&short, c.def, 0)
			step := throneStep(short)
			if step.Kind != policy.ThronePlace || step.Piece.Def != c.def || !step.Room.Same(room) {
				t.Fatalf("a room missing a %s plans it: %+v", c.missing, step)
			}
			if owed, known := bedroomsOwed(short, policy.StageReserves).Value(); !known || !owed {
				t.Fatalf("the missing %s holds MaintainHousing open: %v %v", c.missing, owed, known)
			}
			if built, _ := facts.Facts.CurrentConstruction.Value(); len(built.Buildings) != 6 {
				t.Fatalf("the snapshot was written: %d buildings", len(built.Buildings))
			}
		})
	}
}

func TestThroneRoomFloorTierPlansTheRequiredTerrain(t *testing.T) {
	facts, _, catalog := knightSnapshot(t)
	furnish(t, &facts)
	var cells []policy.FloorCell
	for z := int32(20); z < 25; z++ {
		for x := int32(10); x < 16; x++ {
			cells = append(cells, policy.FloorCell{Cell: domain.Cell{X: x, Z: z}, Terrain: "Soil"})
		}
	}
	census := policy.FlooringObservation{
		Rooms:    []policy.FloorRoom{{ID: "r1", Cells: cells}},
		Terrains: map[string]policy.FloorTerrain{"Soil": {Natural: true}, "Carpet": {Tags: []string{"FineFloor"}}},
	}
	review, err := policy.ReviewFlooring(domain.Known(withThroneFloor(facts, census)), facts.Rooms, nil, policy.DefaultFlooringPolicy())
	if err != nil || len(review.Deficits) != 1 || review.Deficits[0].Tier != policy.FloorTierThrone || len(review.Deficits[0].Cells) != 30 {
		t.Fatal(review, err)
	}
	royalty, _ := facts.Royalty.Value()
	if terrains := throneFloorTerrains(catalog, royalty); !slices.Equal(terrains, []string{"Carpet"}) {
		t.Fatalf("the flooring read carries the terrains the title accepts: %v", terrains)
	}
	proposal, err := policy.SelectFlooringMethod(review, policy.FlooringFacts{Definitions: map[string]policy.FloorDefinition{
		"Carpet": {Available: domain.Known(true), Terrain: domain.Known(true), Tags: []string{"FineFloor"}},
	}}, policy.DefaultFlooringPolicy())
	if err != nil || proposal.Method != policy.FlooringBuild || proposal.Definition != "Carpet" {
		t.Fatal(proposal, err)
	}
}

func TestThroneRoomForbiddenBuildingBlocks(t *testing.T) {
	facts, room, _ := knightSnapshot(t)
	furnish(t, &facts)
	inside := domain.Cell{X: room.Interior.X, Z: room.Interior.Z}
	standing(t, &facts, "TableButcher_9", "TableButcher", inside)
	step := throneStep(facts)
	if step.Kind != policy.ThroneBlocked || step.Owed() || len(step.Intruders) != 1 || step.Intruders[0].ID != "TableButcher_9" {
		t.Fatalf("a Production building in the room blocks it: %+v", step)
	}
	if owed, known := bedroomsOwed(facts, policy.StageReserves).Value(); known && owed {
		t.Fatalf("a blocked room plans nothing: %v", owed)
	}
	// A bed elsewhere is no intrusion.
	facts, _, _ = knightSnapshot(t)
	furnish(t, &facts)
	standing(t, &facts, "Bed_1", "Bed", domain.Cell{X: 60, Z: 60})
	if step := throneStep(facts); step.Kind != policy.ThroneNone {
		t.Fatalf("a bed outside the room: %+v", step)
	}
}

func TestThroneRoomUnlitBrazierPlansARefuel(t *testing.T) {
	facts, room, _ := knightSnapshot(t)
	furnish(t, &facts)
	in := room.Interior
	lamp := func(id string, lit bool, empty bool) policy.Lamp {
		return policy.Lamp{ID: id, Definition: "Brazier", Cell: domain.Cell{X: in.X, Z: in.Z}, Lit: lit, OutOfFuel: domain.Known(empty)}
	}
	facts.Facts.Upkeep.Lighting = domain.Known(policy.LightingObservation{Lamps: []policy.Lamp{lamp("b1", true, false), lamp("b2", false, true)}})
	step := throneStep(facts)
	if step.Kind != policy.ThroneRefuel || step.Lamp != "b2" || step.Pawn != "hauler" || !step.Owed() {
		t.Fatalf("an unlit empty brazier plans a refuel: %+v", step)
	}
	facts.Facts.Upkeep.Lighting = domain.Known(policy.LightingObservation{Lamps: []policy.Lamp{lamp("b1", true, false), lamp("b2", true, false)}})
	if step := throneStep(facts); step.Kind != policy.ThroneNone {
		t.Fatalf("lit braziers: %+v", step)
	}
}
