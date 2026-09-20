package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ZoneDeleteTarget refreshes one exact zone's presence and per-zone CAS
// token (#611), the scoped-refresh shape ClaimBuildingTarget uses, through
// the zone listing filtered to the one id. Present is false once the zone
// is gone from the census, which is what a completed deletion looks like.
type ZoneDeleteTarget struct {
	Context *c.ObservationContext
	Zone    string
	Present bool
	Token   string
	Type    string
	// Planted counts a growing zone's crop plants (the census farm facts).
	Planted uint32
}

// ReadZoneDeleteTarget observes one exact zone's presence and CAS token
// via the zone listing; an absent zone reads back as not present rather
// than as an error, so a deletion's observer can tell success from a
// stale read.
func (client *Client) ReadZoneDeleteTarget(ctx context.Context, identity *c.Identity, zone string) (ZoneDeleteTarget, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ZoneDeleteTarget{}, Result{}, err
	}
	if validID(zone) != nil {
		return ZoneDeleteTarget{}, Result{}, contract("invalid zone delete target identity")
	}
	request := &o.ListZonesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Ids: []string{zone}, Page: &c.PageRequest{Limit: proto.Uint32(1)}}
	reply := &o.ListZonesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_zones", request, reply)
	if err != nil {
		return ZoneDeleteTarget{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return ZoneDeleteTarget{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ListZonesReply_Failure:
		return ZoneDeleteTarget{}, raw, failure(v.Failure, raw)
	case *o.ListZonesReply_Unavailable:
		return ZoneDeleteTarget{}, raw, unavailable(v.Unavailable, raw)
	case *o.ListZonesReply_Observed:
		if err = validateZonePage(v.Observed, identity, 0); err != nil {
			return ZoneDeleteTarget{}, raw, err
		}
	default:
		return ZoneDeleteTarget{}, raw, contract("zone read outcome missing")
	}
	v := reply.GetObserved()
	if v.Completeness.Page.GetNextCursor() != "" || len(v.Zones) > 1 {
		return ZoneDeleteTarget{}, raw, contract("zone delete target ambiguous")
	}
	out := ZoneDeleteTarget{Context: v.Context, Zone: zone}
	if len(v.Zones) == 0 {
		return out, raw, nil
	}
	row := v.Zones[0]
	if row.GetId() != zone {
		return ZoneDeleteTarget{}, raw, contract("zone delete target identity mismatch")
	}
	out.Present, out.Token, out.Type = true, row.Snapshot.GetToken(), row.GetType()
	if row.Farm != nil {
		out.Planted = row.Farm.GetPlantedCells()
	}
	return out, raw, nil
}
