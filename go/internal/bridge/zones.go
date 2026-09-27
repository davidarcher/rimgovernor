package bridge

import (
	"context"
	"sort"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ZonesRead is the complete zone census, rows in id order. Rows are
// immutable after publication to the facts store.
type ZonesRead struct {
	Context     *c.ObservationContext
	Rows        []*o.ZoneState
	AsOf        int64
	MapSnapshot *o.SnapshotRef
}

// zoneSectionRequest is one page of the zone census read, shared with the
// bundle's zones family (#593).
func zoneSectionRequest(identity *c.Identity, cursor string) *o.ListZonesRequest {
	q := &o.ListZonesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Page: &c.PageRequest{Limit: proto.Uint32(16)}}
	if cursor != "" {
		q.Page.Cursor = proto.String(cursor)
	}
	return q
}

// ReadZoneSection reads the whole census. A failure never silently
// replaces a held census.
func (client *Client) ReadZoneSection(ctx context.Context, identity *c.Identity) (ZonesRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ZonesRead{}, Result{}, err
	}
	out := ZonesRead{}
	cursor := ""
	seen := map[string]bool{}
	var raw Result
	for page := 0; page < 256; page++ {
		q := zoneSectionRequest(identity, cursor)
		reply := &o.ListZonesReply{}
		var err error
		raw, err = client.protoRead(ctx, "rimgovernor/observations_list_zones", q, reply)
		if err != nil {
			return ZonesRead{}, raw, err
		}
		if err := buildingUnknown(reply); err != nil {
			return ZonesRead{}, raw, err
		}
		if f := reply.GetFailure(); f != nil {
			return ZonesRead{}, raw, failure(f, raw)
		}
		if u := reply.GetUnavailable(); u != nil {
			return ZonesRead{}, raw, unavailable(u, raw)
		}
		v := reply.GetObserved()
		if err := validateZonePage(v, identity); err != nil {
			return ZonesRead{}, raw, err
		}
		if page == 0 {
			out = ZonesRead{Context: v.Context, AsOf: v.Context.GetTick(), MapSnapshot: v.MapSnapshot}
		} else if !proto.Equal(out.Context, v.Context) || !proto.Equal(out.MapSnapshot, v.MapSnapshot) {
			return ZonesRead{}, raw, contract("zone census changed during pagination")
		}
		for _, row := range v.Zones {
			if seen[row.GetId()] {
				return ZonesRead{}, raw, contract("duplicate zone across pages")
			}
			seen[row.GetId()] = true
			out.Rows = append(out.Rows, row)
		}
		next := v.Completeness.Page.GetNextCursor()
		if next == "" {
			sort.Slice(out.Rows, func(i, j int) bool { return out.Rows[i].GetId() < out.Rows[j].GetId() })
			return out, raw, nil
		}
		if next == cursor {
			return ZonesRead{}, raw, contract("zone cursor did not advance")
		}
		cursor = next
	}
	return ZonesRead{}, raw, contract("zone census exceeds page bound")
}

func validateZonePage(v *o.ZonesSnapshot, identity *c.Identity) error {
	if v == nil {
		return contract("missing zone snapshot")
	}
	if err := ValidateContext(v.Context); err != nil {
		return err
	}
	if !sameIdentity(v.Context.Identity, identity) {
		return contract("zone identity mismatch")
	}
	counts := v.Completeness
	if counts == nil || counts.Page == nil || counts.Page.Complete == nil || counts.GetUnreadable() != 0 || counts.Returned == nil || counts.GetReturned() != uint64(len(v.Zones)) || counts.GetMatched() != uint64(len(v.Zones)) || len(v.Zones) > 16 || counts.Page.GetComplete() != (counts.Page.GetNextCursor() == "") {
		return contract("incomplete zone census")
	}
	if counts.Page.GetNextCursor() != "" && len(v.Zones) == 0 {
		return contract("empty zone page with cursor")
	}
	if snapshot := v.MapSnapshot; snapshot != nil && (!proto.Equal(snapshot.Context, v.Context) || snapshot.GetEntityId() == "" || validID(snapshot.GetToken()) != nil) {
		return contract("invalid zone map snapshot")
	}
	for _, row := range v.Zones {
		if row == nil || validID(row.GetId()) != nil || row.FoodStorage == nil {
			return contract("invalid zone row")
		}
		if row.Snapshot == nil || !proto.Equal(row.Snapshot.Context, v.Context) || row.Snapshot.GetEntityId() != row.GetId() || validID(row.Snapshot.GetToken()) != nil {
			return contract("invalid zone snapshot reference")
		}
		if farm := row.Farm; farm != nil {
			if farm.GetZoneId() != row.GetId() || validID(farm.GetCrop()) != nil || farm.UsableCells == nil || farm.PlantedCells == nil || farm.GrowingCells == nil || farm.EdibleCrop == nil || farm.GetGrowingCells() > farm.GetPlantedCells() || !combatNumber(farm.HarvestLowerBoundDays, true) || !combatNumber(farm.NutritionPerHarvestCell, true) {
				return contract("invalid zone farm facts")
			}
		} else if row.GetType() == "growing" {
			return contract("missing zone farm facts")
		}
	}
	return nil
}
