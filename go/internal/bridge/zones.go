package bridge

import (
	"context"
	"sort"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const ZoneDeltaExpired = "entity_tombstone_window_expired"
const ZoneTrackingUnavailable = "entity_tracking_not_available"

// ZonesRead is the complete zone census or its inclusive-tick delta.
// Rows are immutable after publication to the facts store.
type ZonesRead struct {
	Context     *c.ObservationContext
	Rows        []*o.ZoneState
	Removed     []string
	Unchanged   uint32
	AsOf        int64
	Delta       bool
	Fallback    bool
	MapSnapshot *o.SnapshotRef
}

// zoneSectionRequest is one page of the zone census read, shared with the
// bundle's zones family (#593).
func zoneSectionRequest(identity *c.Identity, since int64, cursor string) *o.ListZonesRequest {
	q := &o.ListZonesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Page: &c.PageRequest{Limit: proto.Uint32(16)}}
	if since > 0 {
		q.ChangedSinceTick = proto.Int64(since)
	}
	if cursor != "" {
		q.Page.Cursor = proto.String(cursor)
	}
	return q
}

// ReadZoneSection falls back only for the native's explicit retention/startup
// refusals. Other failures never silently replace a held census.
func (client *Client) ReadZoneSection(ctx context.Context, identity *c.Identity, since int64) (ZonesRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ZonesRead{}, Result{}, err
	}
	if since < 0 {
		return ZonesRead{}, Result{}, contract("negative zone delta tick")
	}
	out := ZonesRead{}
	cursor := ""
	seen := map[string]bool{}
	var raw Result
	for page := 0; page < 256; page++ {
		q := zoneSectionRequest(identity, since, cursor)
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
			if since > 0 && f.GetCode() == c.FailureCode_FAILURE_CODE_UNAVAILABLE && (f.GetDetail() == ZoneDeltaExpired || f.GetDetail() == ZoneTrackingUnavailable) {
				full, result, err := client.ReadZoneSection(ctx, identity, 0)
				full.Fallback = err == nil
				return full, result, err
			}
			return ZonesRead{}, raw, failure(f, raw)
		}
		if u := reply.GetUnavailable(); u != nil {
			if since > 0 && u.GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_STALE && (u.GetDetail() == ZoneDeltaExpired || u.GetDetail() == ZoneTrackingUnavailable) {
				full, result, err := client.ReadZoneSection(ctx, identity, 0)
				full.Fallback = err == nil
				return full, result, err
			}
			return ZonesRead{}, raw, unavailable(u, raw)
		}
		v := reply.GetObserved()
		if err := validateZonePage(v, identity, since); err != nil {
			return ZonesRead{}, raw, err
		}
		asOf := v.Context.GetTick()
		delta := since > 0 && v.AsOfTick != nil
		if page == 0 {
			out = ZonesRead{Context: v.Context, AsOf: asOf, Delta: delta, Unchanged: v.GetUnchanged(), Removed: append([]string(nil), v.RemovedIds...), MapSnapshot: v.MapSnapshot}
		} else if !proto.Equal(out.Context, v.Context) || out.Delta != delta || out.Unchanged != v.GetUnchanged() || !proto.Equal(out.MapSnapshot, v.MapSnapshot) || !equalZoneIDs(out.Removed, v.RemovedIds) {
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
			for _, id := range out.Removed {
				if seen[id] {
					return ZonesRead{}, raw, contract("zone both changed and removed")
				}
			}
			return out, raw, nil
		}
		if next == cursor {
			return ZonesRead{}, raw, contract("zone cursor did not advance")
		}
		cursor = next
	}
	return ZonesRead{}, raw, contract("zone census exceeds page bound")
}

func equalZoneIDs(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func validateZonePage(v *o.ZonesSnapshot, identity *c.Identity, since int64) error {
	if v == nil {
		return contract("missing zone snapshot")
	}
	if err := ValidateContext(v.Context); err != nil {
		return err
	}
	if !sameIdentity(v.Context.Identity, identity) {
		return contract("zone identity mismatch")
	}
	if v.AsOfTick != nil && v.GetAsOfTick() != v.Context.GetTick() || v.GetUnchanged() > 0 && (since <= 0 || v.AsOfTick == nil) || len(v.RemovedIds) > 0 && (since <= 0 || v.AsOfTick == nil) || since > v.Context.GetTick() {
		return contract("invalid zone delta metadata")
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
	removed := map[string]bool{}
	for _, id := range v.RemovedIds {
		if validID(id) != nil || removed[id] {
			return contract("invalid zone tombstone")
		}
		removed[id] = true
	}
	for _, row := range v.Zones {
		if row == nil || validID(row.GetId()) != nil || removed[row.GetId()] || row.FoodStorage == nil {
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

// MergeZones replaces changed rows and applies tombstones. It never mutates
// the old value; a full read (including refusal fallback) replaces it.
func MergeZones(held ZonesRead, read ZonesRead) ZonesRead {
	if !read.Delta {
		return read
	}
	rows := map[string]*o.ZoneState{}
	for _, row := range held.Rows {
		rows[row.GetId()] = row
	}
	for _, id := range read.Removed {
		delete(rows, id)
	}
	for _, row := range read.Rows {
		rows[row.GetId()] = row
	}
	read.Rows = make([]*o.ZoneState, 0, len(rows))
	for _, row := range rows {
		read.Rows = append(read.Rows, row)
	}
	sort.Slice(read.Rows, func(i, j int) bool { return read.Rows[i].GetId() < read.Rows[j].GetId() })
	read.Delta, read.Removed, read.Unchanged = false, nil, 0
	return read
}

// ZoneDrift compares facts, excluding CAS context stamps (held unchanged
// rows deliberately retain the tick they were last emitted at).
func ZoneDrift(held, full ZonesRead) int {
	rows := map[string]*o.ZoneState{}
	for _, row := range held.Rows {
		rows[row.GetId()] = row
	}
	drift := 0
	for _, row := range full.Rows {
		old := rows[row.GetId()]
		if old == nil {
			drift++
		} else {
			a, b := proto.Clone(old).(*o.ZoneState), proto.Clone(row).(*o.ZoneState)
			a.Snapshot, b.Snapshot = nil, nil
			if !proto.Equal(a, b) {
				drift++
			}
		}
		delete(rows, row.GetId())
	}
	return drift + len(rows)
}
