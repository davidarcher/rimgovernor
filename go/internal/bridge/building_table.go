package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Buildings is a frame's building table by id (#1343): the canonical rows
// every other section's building reference resolves against. A reference
// the table does not hold waits for the next frame: the fact it feeds is
// unknown until then.
type Buildings map[string]*o.BuildingState

// BuildingTable indexes v's rows by id; a nil v is an empty table.
func BuildingTable(v *o.BuildingsSnapshot) Buildings {
	out := make(Buildings, len(v.GetBuildings()))
	for _, row := range v.GetBuildings() {
		if id := row.GetBuilding().GetId(); id != "" {
			out[id] = row
		}
	}
	return out
}

// Row is ref's canonical row, false when the table does not hold it or
// the row's service or settings are malformed.
// Entity is the head of ref's row, nil when the table does not hold it.
func (b Buildings) Entity(ref Reference) *o.EntityRef {
	if ref == nil {
		return nil
	}
	return b[ref.GetId()].GetBuilding()
}

func (b Buildings) Row(ref Reference) (*o.BuildingState, bool) {
	row, ok := b[ref.GetId()]
	if !ok || validBuildingService(row.GetService()) != nil || row.GetSettings() == nil {
		return nil, false
	}
	return row, true
}

// validBuildingService checks a canonical row's service state: finite
// quantities, valid fuel definitions and network id, and a "fuel" issue
// only where no fuel is reported.
func validBuildingService(s *o.BuildingServiceState) error {
	if s == nil {
		return contract("building row without service")
	}
	for _, value := range []*float64{s.Fuel, s.TargetFuel} {
		if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || *value < 0 || *value > 1e12) {
			return contract("invalid building fuel quantity")
		}
	}
	if v := s.PowerOutputW; v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0) || math.Abs(*v) > 1e12) {
		return contract("invalid building power output")
	}
	if s.PowerNetId != nil && (validID(s.GetPowerNetId()) != nil || s.Connected != nil && !s.GetConnected()) {
		return contract("invalid building power network")
	}
	defs := map[string]bool{}
	for _, d := range s.AllowedFuelDefs {
		if validID(d) != nil || defs[d] {
			return contract("invalid building fuel definition")
		}
		defs[d] = true
	}
	if err := pawnsIssues(s.Issues, s.ProtoReflect()); err != nil {
		return err
	}
	for _, issue := range s.Issues {
		if issue.GetField() != "fuel" || issue.GetUnavailable().GetReason() != c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE || s.Fuel != nil || s.TargetFuel != nil || len(s.AllowedFuelDefs) > 0 {
			return contract("conflicting building fuel availability")
		}
	}
	return nil
}

// FrameBuildings is the building table of the newest frame (the bundle's
// player building list), or that list read over GABP without a stream.
func (caller *Client) FrameBuildings(ctx context.Context, identity *c.Identity) (Buildings, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	request := buildingsListRequest(identity)
	reply := &o.ListBuildingsReply{}
	served, err := caller.frameRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
	if err != nil {
		return nil, err
	}
	if !served {
		raw, err := caller.protoRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
		if err != nil {
			return nil, err
		}
		switch v := reply.Outcome.(type) {
		case *o.ListBuildingsReply_Failure:
			return nil, failure(v.Failure, raw)
		case *o.ListBuildingsReply_Unavailable:
			return nil, unavailable(v.Unavailable, raw)
		}
	}
	observed := reply.GetObserved()
	if observed == nil || ValidateContext(observed.Context) != nil || !sameIdentity(observed.Context.Identity, identity) {
		return nil, contract("building table without the expected context")
	}
	return BuildingTable(observed), nil
}
