package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

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
	limit := len(ids)
	if limit == 0 {
		limit = 256
	}
	request := &o.ListBuildingsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Ids: append([]string{}, ids...), Statuses: []string{"built"}, PlayerOnly: proto.Bool(true), Category: proto.String("artificial"), Page: &c.PageRequest{Limit: proto.Uint32(uint32(limit))}}
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
	counts := v.Completeness
	if (len(ids) > 0 && len(v.Buildings) > len(ids)) || len(v.Buildings) > 256 || counts == nil || counts.Page == nil || counts.Page.Complete == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Filtered == nil || counts.Unreadable == nil || counts.GetMatched() != uint64(len(v.Buildings)) || counts.GetReturned() != uint64(len(v.Buildings)) || counts.GetUnreadable() != 0 || counts.GetFiltered() > math.MaxUint64-counts.GetReturned() {
		return contract("incomplete building query")
	}
	seen := map[string]bool{}
	for _, row := range v.Buildings {
		if row == nil || row.Building == nil || (len(ids) > 0 && !requested[row.Building.GetId()]) || seen[row.Building.GetId()] {
			return contract("unexpected building")
		}
		seen[row.Building.GetId()] = true
		e := row.Building
		if e.DefName == nil || e.MapId == nil || e.Position == nil || pawnsEntity(e, v.Context) != nil || row.GetStatus() != "built" || row.Rotation == nil || row.Stuff != nil && validID(row.GetStuff()) != nil {
			return contract("invalid building identity or geometry")
		}
		switch row.GetRotation() {
		case "North", "East", "South", "West":
		default:
			return contract("invalid building rotation")
		}
		if err := pawnsIssues(row.Issues, row.ProtoReflect()); err != nil {
			return err
		}
	}
	return nil
}
