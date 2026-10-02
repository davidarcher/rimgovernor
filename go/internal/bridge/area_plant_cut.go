package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

const plantCutCensusTool = "rimgovernor/observations_read_plant_cut_census"

// areaPlantCutAction is the AreaPlantCutIntent over canonical cells (#1547).
// Native designates the cells' non-crop plants live (NativeAreaPlantCut.cs).
func areaPlantCutAction(action domain.Action) (*op.Action, error) {
	v, ok := action.AreaPlantCut()
	if !ok {
		return nil, contract("not an area plant cut action")
	}
	if _, err := domain.NewAreaPlantCutAction(action.ID(), v); err != nil {
		return nil, contract("%v", err)
	}
	intent := &op.AreaPlantCutIntent{}
	for _, cell := range v.Cells() {
		intent.Cells = append(intent.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	return &op.Action{Intent: &op.Action_AreaPlantCut{AreaPlantCut: intent}}, nil
}

// PlantCutStanding is one undesignated non-crop plant the census reports:
// what an AreaPlantCutIntent over its cell would designate.
type PlantCutStanding struct {
	Plant    string
	Cell     domain.Cell
	ChopWood bool // a harvestable tree: chop-wood, not CutPlant
}

type PlantCutCensus struct {
	Context *c.ObservationContext
	Plants  []PlantCutStanding
}

// ReadPlantCutCensus reads, for cells, the undesignated non-crop plants
// still standing (#1547): the planner re-sweeps while any stand and the
// executor inspects the sweep's postcondition with it. No cells reads
// nothing.
func (client *Client) ReadPlantCutCensus(ctx context.Context, identity *c.Identity, cells []domain.Cell) (PlantCutCensus, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return PlantCutCensus{}, Result{}, err
	}
	if len(cells) > domain.MaxAreaPlantCutCells {
		return PlantCutCensus{}, Result{}, contract("plant cut census takes at most %d cells", domain.MaxAreaPlantCutCells)
	}
	if len(cells) == 0 {
		return PlantCutCensus{Plants: []PlantCutStanding{}}, Result{}, nil
	}
	request := &o.PlantCutCensusRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	for _, cell := range cells {
		request.Cells = append(request.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	reply := &o.PlantCutCensusReply{}
	raw, err := client.protoRead(ctx, plantCutCensusTool, request, reply)
	if err != nil {
		return PlantCutCensus{}, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *o.PlantCutCensusReply_Failure:
		return PlantCutCensus{}, raw, failure(value.Failure, raw)
	case *o.PlantCutCensusReply_Unavailable:
		return PlantCutCensus{}, raw, unavailable(value.Unavailable, raw)
	case *o.PlantCutCensusReply_Observed:
		out, err := plantCutCensus(value.Observed, identity, cells)
		return out, raw, err
	default:
		return PlantCutCensus{}, raw, contract("missing plant cut census outcome")
	}
}

func plantCutCensus(snapshot *o.PlantCutCensusSnapshot, identity *c.Identity, cells []domain.Cell) (PlantCutCensus, error) {
	requested := make(map[domain.Cell]bool, len(cells))
	for _, cell := range cells {
		requested[cell] = true
	}
	if snapshot == nil {
		return PlantCutCensus{}, contract("missing plant cut census snapshot")
	}
	if err := ValidateContext(snapshot.Context); err != nil {
		return PlantCutCensus{}, err
	}
	if !sameIdentity(snapshot.Context.Identity, identity) {
		return PlantCutCensus{}, contract("plant cut census identity mismatch")
	}
	out := PlantCutCensus{Context: proto.Clone(snapshot.Context).(*c.ObservationContext), Plants: []PlantCutStanding{}}
	for _, row := range snapshot.Plants {
		plant := row.GetPlant()
		position := row.GetCell()
		if !validRef(plant) || position == nil || position.X == nil || position.Z == nil || !requested[domain.Cell{X: position.GetX(), Z: position.GetZ()}] {
			return PlantCutCensus{}, contract("plant cut census row invalid")
		}
		out.Plants = append(out.Plants, PlantCutStanding{Plant: plant.GetId(), Cell: domain.Cell{X: position.GetX(), Z: position.GetZ()}, ChopWood: row.GetChopWood()})
	}
	return out, nil
}
