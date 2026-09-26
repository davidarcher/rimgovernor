package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const (
	constructionDeficitMaxPages = 16
)

// ConstructionDeficitRead is native's own view of what the colony's still
// unbuilt player orders are still short of: for every blueprint and frame on
// the map, the sum of its ConstructionState.resources[].still_needed by
// definition. It is the `resourceDeficit` list home/list_buildings reports,
// which the economic floors add so a trade never sells material a
// construction already in flight is waiting on.
//
// Completeness is a precondition: this read refuses a truncated or partially
// unreadable census rather than under-reporting a commitment.
type ConstructionDeficitRead struct {
	Context    *c.ObservationContext
	StillNeed  map[string]int64
	Structures int
}

// ReadConstructionDeficits reads every player blueprint and frame on the map
// and sums their outstanding material needs by definition.
func (client *Client) ReadConstructionDeficits(ctx context.Context, identity *c.Identity) (ConstructionDeficitRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ConstructionDeficitRead{}, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	out := ConstructionDeficitRead{StillNeed: map[string]int64{}}
	cursor := ""
	var raw Result
	for page := 0; page < constructionDeficitMaxPages; page++ {
		request := &o.ListBuildingsRequest{
			Scope:      &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
			Statuses:   []string{"blueprint", "frame"},
			PlayerOnly: proto.Bool(true),
			Page:       &c.PageRequest{Limit: proto.Uint32(buildingsPage)},
		}
		if cursor != "" {
			request.Page.Cursor = proto.String(cursor)
		}
		reply := &o.ListBuildingsReply{}
		var err error
		raw, err = client.protoRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
		if err != nil {
			return ConstructionDeficitRead{}, raw, err
		}
		if buildingUnknown(reply) != nil {
			return ConstructionDeficitRead{}, raw, contract("unknown construction deficit fields")
		}
		var observed *o.BuildingsSnapshot
		switch v := reply.Outcome.(type) {
		case *o.ListBuildingsReply_Failure:
			return ConstructionDeficitRead{}, raw, failure(v.Failure, raw)
		case *o.ListBuildingsReply_Unavailable:
			return ConstructionDeficitRead{}, raw, unavailable(v.Unavailable, raw)
		case *o.ListBuildingsReply_Observed:
			observed = v.Observed
		default:
			return ConstructionDeficitRead{}, raw, contract("construction deficit outcome missing")
		}
		if observed == nil {
			return ConstructionDeficitRead{}, raw, contract("construction deficit snapshot missing")
		}
		if err = buildingContext(observed.Context, identity, 0, false); err != nil {
			return ConstructionDeficitRead{}, raw, err
		}
		counts := observed.Completeness
		if counts == nil || counts.Page == nil || counts.Returned == nil || counts.Unreadable == nil ||
			counts.GetUnreadable() != 0 || counts.GetReturned() != uint64(len(observed.Buildings)) {
			return ConstructionDeficitRead{}, raw, contract("incomplete construction deficit observation")
		}
		next := counts.Page.GetNextCursor()
		if next == "" && !counts.Page.GetComplete() {
			return ConstructionDeficitRead{}, raw, contract("incomplete construction deficit page")
		}
		if page == 0 {
			out.Context = observed.Context
		} else if observed.Context.GetTick() != out.Context.GetTick() {
			return ConstructionDeficitRead{}, raw, contract("construction deficit census changed during pagination")
		}
		for _, row := range observed.Buildings {
			if row == nil || row.Building == nil || validID(row.Building.GetId()) != nil {
				return ConstructionDeficitRead{}, raw, contract("invalid construction deficit row")
			}
			switch row.GetStatus() {
			case "blueprint", "frame":
			default:
				return ConstructionDeficitRead{}, raw, contract("unexpected construction deficit status")
			}
			out.Structures++
			for _, need := range row.GetConstruction().GetResources() {
				if need == nil || validID(need.GetDefName()) != nil {
					return ConstructionDeficitRead{}, raw, contract("invalid construction material deficit")
				}
				// An absent still_needed is not zero: it is unknown, and
				// silently treating it as satisfied would under-protect the
				// material.
				if need.StillNeeded == nil || need.GetStillNeeded() < 0 {
					return ConstructionDeficitRead{}, raw, contract("construction material deficit unknown")
				}
				out.StillNeed[need.GetDefName()] += need.GetStillNeeded()
			}
		}
		if next == "" {
			return out, raw, ctx.Err()
		}
		cursor = next
	}
	return ConstructionDeficitRead{}, raw, contract("construction deficit census exceeds pagination bound")
}
