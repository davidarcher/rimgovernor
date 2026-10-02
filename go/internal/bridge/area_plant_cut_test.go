package bridge

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// An area plant cut builds one AreaPlantCutIntent (#1547) with canonical cells.
func TestAreaPlantCutBuildsIntent(t *testing.T) {
	value, err := domain.NewAreaPlantCut([]domain.Cell{{X: 5, Z: 2}, {X: 1, Z: 9}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewAreaPlantCutAction("cut1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("area plant cut is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	cells := wire.GetAreaPlantCut().GetCells()
	if len(cells) != 2 || cells[0].GetX() != 1 || cells[0].GetZ() != 9 || cells[1].GetX() != 5 || cells[1].GetZ() != 2 {
		t.Fatalf("%v", wire)
	}
}

func TestAreaPlantCutRefusesBadCells(t *testing.T) {
	many := make([]domain.Cell, domain.MaxAreaPlantCutCells+1)
	for i := range many {
		many[i] = domain.Cell{X: int32(i), Z: 0}
	}
	for _, cells := range [][]domain.Cell{nil, {{X: -1, Z: 0}}, {{X: 1, Z: 1}, {X: 1, Z: 1}}, many} {
		if _, err := domain.NewAreaPlantCut(cells); err == nil {
			t.Fatalf("accepted %d cells", len(cells))
		}
	}
	if _, err := domain.NewAreaPlantCutAction("", domain.AreaPlantCut{}); err == nil {
		t.Fatal("accepted an empty action")
	}
}

func plantCutRow(id, def string, x, z int32, chop bool) *o.PlantCutTarget {
	return &o.PlantCutTarget{Plant: &o.EntityRef{Id: proto.String(id), DefName: proto.String(def), Position: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}}, ChopWood: proto.Bool(chop)}
}

func readPlantCutCensus(t *testing.T, reply *o.PlantCutCensusReply, cells []domain.Cell) (PlantCutCensus, error) {
	t.Helper()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != plantCutCensusTool {
			t.Fatal(arg.Tool)
		}
		return pbResult(reply), nil
	}}, time.Second)
	census, _, err := client.ReadPlantCutCensus(context.Background(), pbIdentity(), cells)
	return census, err
}

func plantCutReply(rows ...*o.PlantCutTarget) *o.PlantCutCensusReply {
	return &o.PlantCutCensusReply{Outcome: &o.PlantCutCensusReply_Observed{Observed: &o.PlantCutCensusSnapshot{
		Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(7), NativeGeneration: proto.Uint64(1)}, Plants: rows}}}
}

// The census maps each standing plant to its identity, cell and chop-wood
// flag, and refuses a row outside the requested cells.
func TestReadPlantCutCensusMapsRows(t *testing.T) {
	cells := []domain.Cell{{X: 3, Z: 4}, {X: 5, Z: 6}}
	census, err := readPlantCutCensus(t, plantCutReply(plantCutRow("Plant_Grass1", "Plant_Grass", 3, 4, false), plantCutRow("Plant_TreeOak2", "Plant_TreeOak", 5, 6, true)), cells)
	if err != nil {
		t.Fatal(err)
	}
	want := []PlantCutStanding{{"Plant_Grass1", "Plant_Grass", domain.Cell{X: 3, Z: 4}, false}, {"Plant_TreeOak2", "Plant_TreeOak", domain.Cell{X: 5, Z: 6}, true}}
	if len(census.Plants) != 2 || census.Plants[0] != want[0] || census.Plants[1] != want[1] || census.Context.GetTick() != 7 {
		t.Fatal(census)
	}
	if _, err := readPlantCutCensus(t, plantCutReply(plantCutRow("Plant_Grass1", "Plant_Grass", 9, 9, false)), cells); err == nil {
		t.Fatal("accepted a row outside the requested cells")
	}
	if _, err := readPlantCutCensus(t, plantCutReply(plantCutRow("", "Plant_Grass", 3, 4, false)), cells); err == nil {
		t.Fatal("accepted a row without identity")
	}
}

func TestReadPlantCutCensusNoCellsReadsNothing(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		t.Fatal(arg.Tool)
		return nil, nil
	}}, time.Second)
	census, _, err := client.ReadPlantCutCensus(context.Background(), pbIdentity(), nil)
	if err != nil || len(census.Plants) != 0 {
		t.Fatal(census, err)
	}
}
