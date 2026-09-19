package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadHomeColonists lists every current-map living colonist's identity and
// Doctor work-type enablement, the exact census CaravanDepartureBoundary
// needs to decide whether a caravan crew would leave home without any
// available doctor. Unlike ReadPawns/ReadTendPawns this is not id-scoped:
// this family must first learn who stays home before it can ask about their
// facts, not merely confirm facts about pawns it already named.
func (client *Client) ReadHomeColonists(ctx context.Context, identity *c.Identity) (*o.ListPawnsReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &o.ListPawnsRequest{
		Scope:   &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Filter:  &o.PawnFilter{Colonist: proto.Bool(true), IncludeDead: proto.Bool(false)},
		// Native defaults every unset detail family to requested, and
		// homeColonistsSelected refuses any family beyond work and needs, so
		// the rest are declined explicitly (the same shape pawnDetailsRequest
		// sends).
		Details: &o.PawnDetails{Work: proto.Bool(true), Needs: proto.Bool(true), Health: proto.Bool(false), Equipment: proto.Bool(false), Biography: proto.Bool(false), Settings: proto.Bool(false), Social: proto.Bool(false), Animals: proto.Bool(false), Schedule: proto.Bool(false)},
		Page:    &c.PageRequest{Limit: proto.Uint32(256)},
	}
	reply := &o.ListPawnsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_pawns", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ListPawnsReply_Failure:
		err = failure(v.Failure, raw)
	case *o.ListPawnsReply_Unavailable:
		err = unavailable(v.Unavailable, raw)
	case *o.ListPawnsReply_Observed:
		err = homeColonistsSelected(v.Observed, request.Scope.ExpectedIdentity)
	default:
		err = contract("home colonist read outcome missing")
	}
	return reply, raw, err
}

func homeColonistsSelected(v *o.PawnSnapshot, id *c.Identity) error {
	if v == nil {
		return contract("home colonist snapshot missing")
	}
	if len(v.Pawns) > 256 {
		return contract("home colonist census exceeds bound")
	}
	requested := make(map[string]bool, len(v.Pawns))
	for _, row := range v.Pawns {
		if row == nil || row.Pawn == nil {
			return contract("home colonist pawn missing")
		}
		requested[row.Pawn.GetId()] = true
	}
	return pawnsSnapshotSelected(v, id, requested, false, true, false, false, false)
}
