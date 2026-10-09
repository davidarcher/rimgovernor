package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

const clearanceTool = "rimgovernor/observations_get_clearance_targets"

// ReadClearanceTargets is read-only and requires no authority. An unsupported
// native stub returns ErrUnavailable, never a successful empty census.
func (client *Client) ReadClearanceTargets(ctx context.Context, identity *c.Identity, includeSalvage bool) (*o.ClearanceTargetsReply, Result, error) {
	return client.ReadClearanceTargetsOnGround(ctx, identity, includeSalvage, nil)
}

// ReadClearanceTargetsOnGround widens the census to planned ground:
// on those rectangles native also reports the player's own buildings (with
// encloses_room) and constructed floors, one row per cell.
func (client *Client) ReadClearanceTargetsOnGround(ctx context.Context, identity *c.Identity, includeSalvage bool, planned []*o.Rectangle) (*o.ClearanceTargetsReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	for _, rect := range planned {
		if validRectangle(rect, 4096) != nil {
			return nil, Result{}, contract("invalid planned ground rectangle")
		}
	}
	request := &o.ClearanceTargetsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, IncludeSalvage: includeSalvage}
	for _, rect := range planned {
		request.PlannedGround = append(request.PlannedGround, proto.Clone(rect).(*o.Rectangle))
	}
	reply := &o.ClearanceTargetsReply{}
	raw, err := client.protoRead(ctx, clearanceTool, request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err := buildingUnknown(reply); err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ClearanceTargetsReply_Observed:
		err = ValidateClearanceTargets(v.Observed, identity)
		if err == nil {
			err = validatePlannedFloors(v.Observed, planned)
		}
	case *o.ClearanceTargetsReply_Unavailable:
		err = unavailable(v.Unavailable, raw)
	case *o.ClearanceTargetsReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("clearance outcome missing")
	}
	return reply, raw, err
}

// ValidateClearanceTargets rejects partial censuses and omitted safety facts.
// A null faction and a null roof blocker have defined meanings only in a
// fully validated row; false and absent booleans are never interchangeable.
// Chunk rows carry every storage fact and the dump footprint is a bounded set
// of distinct cells; both are bounded by the same census rules.
func ValidateClearanceTargets(v *o.ClearanceTargetsSnapshot, identity *c.Identity) error {
	if v == nil || buildingUnknown(v) != nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return contract("invalid clearance context")
	}
	chunks := map[string]bool{}
	for _, row := range v.Chunks {
		if row == nil || validID(row.GetEntityId()) != nil || validID(row.GetDefName()) != nil || chunks[row.GetEntityId()] || row.Cell == nil || row.Cell.X == nil || row.Cell.Z == nil || row.Cell.GetX() < 0 || row.Cell.GetZ() < 0 || row.Forbidden == nil || row.Stored == nil || row.Destination == nil || (row.GetStored() && row.GetDestination()) {
			return contract("invalid clearance chunk")
		}
		chunks[row.GetEntityId()] = true
	}
	seen := map[string]bool{}
	for _, row := range v.Targets {
		if row == nil || validID(row.GetEntityId()) != nil || validID(row.GetDefName()) != nil || seen[row.GetEntityId()] || row.Deconstructible == nil || !row.GetDeconstructible() || row.InHome == nil || row.AncientDanger == nil || row.Designated == nil {
			return contract("invalid clearance target")
		}
		seen[row.GetEntityId()] = true
		if s := row.Salvage; s != nil {
			finite := func(n float64) bool { return !math.IsNaN(n) && !math.IsInf(n, 0) && n >= 0 && n <= 1e12 }
			if !finite(s.PathLength) || !finite(s.Labor) {
				return contract("invalid salvage costs")
			}
			yields := map[string]bool{}
			for _, y := range s.Yields {
				if y == nil || validID(y.DefName) != nil || yields[y.DefName] || y.Count <= 0 || y.Count > 1e9 || y.StorageHeadroom < 0 || y.StorageHeadroom > 1e9 || !finite(y.UnitValue) {
					return contract("invalid salvage yield")
				}
				yields[y.DefName] = true
			}
		}
		if row.RoofBlocker != nil && row.GetRoofBlocker() == "" {
			return contract("empty clearance roof blocker")
		}
		if row.Class < o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_WALL_DOOR || row.Class > o.ClearanceClass_CLEARANCE_CLASS_OTHER {
			return contract("unknown clearance class")
		}
		if validRectangle(row.Occupied, 4096) != nil {
			return contract("invalid clearance occupied rectangle")
		}
	}
	return nil
}

// validatePlannedFloors requires every floor row to be a distinct complete
// cell inside the requested planned ground; with none requested, no floors.
func validatePlannedFloors(v *o.ClearanceTargetsSnapshot, planned []*o.Rectangle) error {
	inside := func(x, z int32) bool {
		for _, r := range planned {
			if x >= r.Minimum.GetX() && x <= r.Maximum.GetX() && z >= r.Minimum.GetZ() && z <= r.Maximum.GetZ() {
				return true
			}
		}
		return false
	}
	seen := map[[2]int32]bool{}
	for _, row := range v.Floors {
		if row == nil || row.Cell == nil || row.Cell.X == nil || row.Cell.Z == nil || validID(row.GetDefName()) != nil || row.Designated == nil {
			return contract("invalid clearance floor")
		}
		key := [2]int32{row.Cell.GetX(), row.Cell.GetZ()}
		if seen[key] || !inside(key[0], key[1]) {
			return contract("clearance floor outside planned ground")
		}
		seen[key] = true
	}
	return nil
}
