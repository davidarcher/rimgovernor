package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

const clearanceTool = "rimgovernor/observations_get_clearance_targets"
const clearanceLimit = 256
const clearanceDumpLimit = 64

// ReadClearanceTargets is read-only and requires no authority. An unsupported
// native stub returns ErrUnavailable, never a successful empty census.
func (client *Client) ReadClearanceTargets(ctx context.Context, identity *c.Identity) (*o.ClearanceTargetsReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &o.ClearanceTargetsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
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
	if len(v.Chunks) > clearanceLimit || len(v.DumpSites) > clearanceDumpLimit {
		return contract("clearance chunk census exceeds bound")
	}
	chunks := map[string]bool{}
	for _, row := range v.Chunks {
		if row == nil || validID(row.GetEntityId()) != nil || validID(row.GetDefName()) != nil || chunks[row.GetEntityId()] || row.Cell == nil || row.Cell.X == nil || row.Cell.Z == nil || row.Cell.GetX() < 0 || row.Cell.GetZ() < 0 || row.Forbidden == nil || row.Stored == nil || row.Destination == nil || (row.GetStored() && row.GetDestination()) {
			return contract("invalid clearance chunk")
		}
		chunks[row.GetEntityId()] = true
	}
	sites := map[[2]int32]bool{}
	for _, cell := range v.DumpSites {
		if cell == nil || cell.X == nil || cell.Z == nil || cell.GetX() < 0 || cell.GetZ() < 0 || sites[[2]int32{cell.GetX(), cell.GetZ()}] {
			return contract("invalid clearance dump site")
		}
		sites[[2]int32{cell.GetX(), cell.GetZ()}] = true
	}
	n := uint64(len(v.Targets))
	p := v.Completeness
	if n > 4096 || p == nil || p.Page == nil || !p.Page.GetComplete() || p.Page.GetNextCursor() != "" || p.Matched == nil || p.Returned == nil || p.Filtered == nil || p.Unreadable == nil || p.GetMatched() != n || p.GetReturned() != n || p.GetFiltered() != 0 || p.GetUnreadable() != 0 {
		return contract("incomplete clearance census")
	}
	seen := map[string]bool{}
	for _, row := range v.Targets {
		if row == nil || validID(row.GetEntityId()) != nil || validID(row.GetDefName()) != nil || seen[row.GetEntityId()] || row.Deconstructible == nil || !row.GetDeconstructible() || row.InHome == nil || row.AncientDanger == nil || row.Designated == nil {
			return contract("invalid clearance target")
		}
		seen[row.GetEntityId()] = true
		if s := row.Salvage; s != nil {
			finite := func(n float64) bool { return !math.IsNaN(n) && !math.IsInf(n, 0) && n >= 0 && n <= 1e12 }
			if !finite(s.PathLength) || !finite(s.Labor) || len(s.Yields) > 256 {
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
		if row.Faction != nil && validID(row.GetFaction()) != nil {
			return contract("invalid clearance faction")
		}
		if row.RoofBlocker != nil && row.GetRoofBlocker() == "" {
			return contract("empty clearance roof blocker")
		}
		if row.Class < o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_WALL_DOOR || row.Class > o.ClearanceClass_CLEARANCE_CLASS_OTHER {
			return contract("unknown clearance class")
		}
		rect := row.Occupied
		if rect == nil || rect.Minimum == nil || rect.Maximum == nil || rect.Minimum.X == nil || rect.Minimum.Z == nil || rect.Maximum.X == nil || rect.Maximum.Z == nil || rect.Minimum.GetX() < 0 || rect.Minimum.GetZ() < 0 || rect.Maximum.GetX() < rect.Minimum.GetX() || rect.Maximum.GetZ() < rect.Minimum.GetZ() || int64(rect.Maximum.GetX())-int64(rect.Minimum.GetX()) >= 4096 || int64(rect.Maximum.GetZ())-int64(rect.Minimum.GetZ()) >= 4096 || (int64(rect.Maximum.GetX())-int64(rect.Minimum.GetX())+1)*(int64(rect.Maximum.GetZ())-int64(rect.Minimum.GetZ())+1) > 4096 {
			return contract("invalid clearance occupied rectangle")
		}
	}
	return nil
}
