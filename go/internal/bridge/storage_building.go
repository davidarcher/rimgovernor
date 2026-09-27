package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// StorageBuildingTarget is one player storage building's (shelf's) presence
// with settings.
type StorageBuildingTarget struct {
	Context *c.ObservationContext
	Thing   string
	Present bool
}

// ReadStorageBuildingTarget observes one exact storage building's presence
// via the building listing; an absent building reads back as not
// present rather than as an error.
func (client *Client) ReadStorageBuildingTarget(ctx context.Context, identity *c.Identity, thing string) (StorageBuildingTarget, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return StorageBuildingTarget{}, Result{}, err
	}
	if validID(thing) != nil {
		return StorageBuildingTarget{}, Result{}, contract("invalid storage building identity")
	}
	request := &o.ListBuildingsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Ids: []string{thing}, Statuses: []string{"built"}}
	reply := &o.ListBuildingsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
	if err != nil {
		return StorageBuildingTarget{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return StorageBuildingTarget{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ListBuildingsReply_Failure:
		return StorageBuildingTarget{}, raw, failure(v.Failure, raw)
	case *o.ListBuildingsReply_Unavailable:
		return StorageBuildingTarget{}, raw, unavailable(v.Unavailable, raw)
	case *o.ListBuildingsReply_Observed:
		if err = ValidateConstructionBuildings(v.Observed, identity, request.Ids); err != nil {
			return StorageBuildingTarget{}, raw, err
		}
	default:
		return StorageBuildingTarget{}, raw, contract("building read outcome missing")
	}
	v := reply.GetObserved()
	out := StorageBuildingTarget{Context: v.Context, Thing: thing}
	if len(v.Buildings) == 0 {
		return out, raw, nil
	}
	row := v.Buildings[0]
	settings := row.GetSettings()
	if len(v.Buildings) != 1 || row.GetBuilding().GetId() != thing || settings == nil {
		return StorageBuildingTarget{}, raw, contract("storage building target missing settings")
	}
	out.Present = true
	return out, raw, nil
}
