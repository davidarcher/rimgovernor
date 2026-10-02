package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// CutPlantTarget is one undesignated blighted plant the census offers (#245).
type CutPlantTarget struct {
	Plant domain.CutPlant
}
type CutPlantRead struct {
	Context *c.ObservationContext
	Targets []CutPlantTarget
}

// ReadBlightedPlants is the colony read's blighted_plants census (no
// planning section) narrowed to the plants not yet designated: what the
// blight planner proposes from and the executor re-reads at inspect. A
// blighted_plants issue is ErrUnavailable, never an empty census.
func (client *Client) ReadBlightedPlants(ctx context.Context, identity *c.Identity) (CutPlantRead, Result, error) {
	if ValidateIdentity(identity) != nil {
		return CutPlantRead{}, Result{}, contract("invalid cut plant scope")
	}
	reply, raw, err := client.ReadColonyFacts(ctx, identity, false)
	if err != nil {
		return CutPlantRead{}, raw, err
	}
	v := reply.GetObserved()
	for _, issue := range v.Issues {
		if issue.GetField() == "blighted_plants" {
			return CutPlantRead{}, raw, ErrUnavailable
		}
	}
	tables, err := client.FrameTables(ctx, identity)
	if err != nil {
		return CutPlantRead{}, raw, err
	}
	out := CutPlantRead{Context: proto.Clone(v.Context).(*c.ObservationContext), Targets: []CutPlantTarget{}}
	for _, row := range v.BlightedPlants {
		if row.GetDesignated() {
			continue
		}
		// A plant the frame's things table does not hold yet waits for
		// the next frame (#1342).
		plant := tables.Entity(row.GetPlant())
		if plant == nil {
			return CutPlantRead{}, raw, ErrUnavailable
		}
		target, err := domain.NewCutPlant(plant.GetId(), plant.GetDefName(), domain.Cell{X: plant.GetPosition().GetX(), Z: plant.GetPosition().GetZ()})
		if err != nil {
			return CutPlantRead{}, raw, err
		}
		out.Targets = append(out.Targets, CutPlantTarget{target})
	}
	return out, raw, nil
}

// cutPlantAction is the DesignateIntent that orders one exact blighted plant
// cut (#245). Native checks the plant live when it applies; a plant already
// designated applies again.
func cutPlantAction(action domain.Action) (*op.Action, error) {
	v, ok := action.CutPlant()
	if !ok {
		return nil, contract("not a cut plant action")
	}
	if validID(v.Plant()) != nil {
		return nil, contract("cut plant target invalid")
	}
	return &op.Action{Intent: &op.Action_Designate{Designate: &op.DesignateIntent{ThingId: proto.String(v.Plant()), Designation: op.ThingDesignation_THING_DESIGNATION_CUT_PLANT.Enum()}}}, nil
}
