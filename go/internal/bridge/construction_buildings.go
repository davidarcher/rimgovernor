package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
)

// constructionBuildingsRequest is the built census read (nil ids), shared
// with the bundle's built buildings family, or the exact targets.
func constructionBuildingsRequest(identity *c.Identity, ids []string) *o.ListBuildingsRequest {
	limit := len(ids)
	if limit == 0 {
		limit = 256
	}
	return &o.ListBuildingsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Ids: append([]string{}, ids...), Statuses: []string{"built"}, PlayerOnly: proto.Bool(true), Category: proto.String("artificial")}
}

// ReadConstructionBuildings requests built artificial player-faction buildings.
// Empty IDs requests the complete bounded colony census; nonempty IDs refresh
// exact targets. Native faction filtering supplies planning ownership, not lineage.
func (client *Client) ReadConstructionBuildings(ctx context.Context, identity *c.Identity, ids []string) (*o.ListBuildingsReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if len(ids) > 256 {
		return nil, Result{}, contract("building IDs exceed 256")
	}
	seen := map[string]bool{}
	for _, id := range ids {
		if validID(id) != nil || seen[id] {
			return nil, Result{}, contract("invalid requested building identity")
		}
		seen[id] = true
	}
	request := constructionBuildingsRequest(identity, ids)
	reply := &o.ListBuildingsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ListBuildingsReply_Failure:
		err = failure(v.Failure, raw)
	case *o.ListBuildingsReply_Unavailable:
		err = unavailable(v.Unavailable, raw)
	case *o.ListBuildingsReply_Observed:
		err = ValidateConstructionBuildings(v.Observed, request.Scope.ExpectedIdentity, request.Ids)
	default:
		err = contract("building read outcome missing")
	}
	return reply, raw, err
}

// Only exact identity, status and geometry fields are consumed for ownership.
// Other typed building details do not supply upkeep authority.
func ValidateConstructionBuildings(v *o.BuildingsSnapshot, identity *c.Identity, ids []string) error {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) || len(ids) > 256 {
		return contract("invalid building query context")
	}
	requested := map[string]bool{}
	for _, id := range ids {
		if validID(id) != nil || requested[id] {
			return contract("invalid requested building")
		}
		requested[id] = true
	}
	if len(ids) > 0 && len(v.Buildings) > len(ids) {
		return contract("incomplete building query")
	}
	seen := map[string]bool{}
	for _, row := range v.Buildings {
		if row == nil || row.Building == nil || (len(ids) > 0 && !requested[row.Building.GetId()]) || seen[row.Building.GetId()] {
			return contract("unexpected building")
		}
		seen[row.Building.GetId()] = true
		if err := validateConstructionRow(row, v.Context); err != nil {
			return err
		}
	}
	return nil
}

// checkBuildingListRow is the per-row part of the building list read's
// checks: a row with no unknown wire fields and a valid id.
func checkBuildingListRow(row *o.BuildingState) error {
	if err := buildingUnknown(row); err != nil {
		return err
	}
	if row == nil || row.Building == nil || validID(row.Building.GetId()) != nil {
		return contract("invalid entity identity")
	}
	if err := validateBuildingOdyssey(row.Odyssey); err != nil {
		return err
	}
	return validateBuildingAnomaly(row.Anomaly)
}

// checkBuiltRow validates a building table row as the construction census
// reads it: a built row against ValidateConstructionBuildings' row rules,
// any other status unchecked.
func checkBuiltRow(row *o.BuildingState, ctx *c.ObservationContext) error {
	if row.GetStatus() != o.BuildingStatus_BUILDING_STATUS_BUILT {
		return nil
	}
	return validateConstructionRow(row, ctx)
}

// validateConstructionRow is one built player building's identity,
// geometry and issue checks.
func validateConstructionRow(row *o.BuildingState, ctx *c.ObservationContext) error {
	e := row.Building
	if e.GetDefName() == "" && e.DefName == nil || e.MapId == nil || e.Position == nil || pawnsEntity(e, ctx) != nil || row.GetStatus() != o.BuildingStatus_BUILDING_STATUS_BUILT || row.Rotation == nil || row.Stuff != nil && validID(row.GetStuff()) != nil {
		return contract("invalid building identity or geometry")
	}
	switch row.GetRotation() {
	case p.Rotation_ROTATION_NORTH, p.Rotation_ROTATION_EAST, p.Rotation_ROTATION_SOUTH, p.Rotation_ROTATION_WEST:
	default:
		return contract("invalid building rotation")
	}
	if err := validateBuildingOdyssey(row.Odyssey); err != nil {
		return err
	}
	if err := validateBuildingAnomaly(row.Anomaly); err != nil {
		return err
	}
	return pawnsIssues(row.Issues, row.ProtoReflect())
}
