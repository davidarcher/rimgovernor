package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ClaimBuildingTarget refreshes one exact claimable building's faction and
// CAS token (#459), the scoped-refresh shape BedMedicalTarget uses. The
// listing row is read without the player-only filter: the target is by
// definition not the player's yet.
type ClaimBuildingTarget struct {
	Context     *c.ObservationContext
	Thing       string
	Token       string
	PlayerOwned bool
}

// ReadClaimBuildingTarget observes one exact claimable building's faction
// and CAS token via the building listing.
func (client *Client) ReadClaimBuildingTarget(ctx context.Context, identity *c.Identity, thing string) (ClaimBuildingTarget, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ClaimBuildingTarget{}, Result{}, err
	}
	if validID(thing) != nil {
		return ClaimBuildingTarget{}, Result{}, contract("invalid claim building target identity")
	}
	request := &o.ListBuildingsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Ids: []string{thing}, Statuses: []string{"built"}, PlayerOnly: proto.Bool(false), Category: proto.String("artificial"), Page: &c.PageRequest{Limit: proto.Uint32(1)}}
	reply := &o.ListBuildingsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
	if err != nil {
		return ClaimBuildingTarget{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return ClaimBuildingTarget{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ListBuildingsReply_Failure:
		return ClaimBuildingTarget{}, raw, failure(v.Failure, raw)
	case *o.ListBuildingsReply_Unavailable:
		return ClaimBuildingTarget{}, raw, unavailable(v.Unavailable, raw)
	case *o.ListBuildingsReply_Observed:
		if err = ValidateConstructionBuildings(v.Observed, identity, request.Ids); err != nil {
			return ClaimBuildingTarget{}, raw, err
		}
	default:
		return ClaimBuildingTarget{}, raw, contract("building read outcome missing")
	}
	v := reply.GetObserved()
	if len(v.Buildings) != 1 {
		return ClaimBuildingTarget{}, raw, contract("claim building target missing or ambiguous")
	}
	row := v.Buildings[0]
	e := row.GetBuilding()
	if e == nil || e.GetId() != thing {
		return ClaimBuildingTarget{}, raw, contract("claim building target identity mismatch")
	}
	settings := row.GetSettings()
	if settings == nil || settings.Snapshot == nil || settings.Snapshot.GetEntityId() != thing || settings.PlayerOwned == nil || settings.Medical != nil || settings.TargetTemperatureC != nil || settings.CropDefName != nil {
		return ClaimBuildingTarget{}, raw, contract("building is not claimable")
	}
	return ClaimBuildingTarget{Context: v.Context, Thing: thing, Token: settings.Snapshot.GetToken(), PlayerOwned: settings.GetPlayerOwned()}, raw, nil
}
