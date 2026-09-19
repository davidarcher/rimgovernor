package bridge

import (
	"context"
	"errors"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ErrDeltaExpired is a delta read the native refused because the since
// tick is older than its tombstone window (#358): the caller reads in
// full instead.
var ErrDeltaExpired = errors.New("entity delta expired")

// EntityTombstoneWindow is how long the native keeps a removed entity's
// id (ticks): a delta asked since an older tick is refused as expired.
const EntityTombstoneWindow int64 = 2500

// EntityRows is one entity list read (zones, buildings or bill stacks)
// keyed by entity id, with the tick the reply described. A read asked with
// a since tick is a delta when Delta is set: Rows lists only the entities
// changed at or after that tick, Removed the ids the native removed since
// it (kept for EntityTombstoneWindow ticks) and Unchanged counts the rest,
// so the caller merges Rows over what it holds and drops Removed. A native
// without entity tracking ignores the ask and answers in full (Delta
// false).
type EntityRows[T proto.Message] struct {
	Context   *c.ObservationContext
	Rows      map[string]T
	Removed   []string
	Unchanged uint64
	Delta     bool
}

// AsOf is the tick the reply described.
func (e EntityRows[T]) AsOf() int64 { return e.Context.GetTick() }

const (
	zonesPage     = 16
	buildingsPage = 256
	billsPage     = 256
)

// ReadZones lists every zone on the map (bounds and settings, no cells,
// contents or filter) through observations_list_zones, every page. A
// positive since asks for the zones changed at or after that tick (#358).
func (client *Client) ReadZones(ctx context.Context, identity *c.Identity, since int64) (EntityRows[*o.ZoneState], Result, error) {
	return readEntities(ctx, client, identity, since, "rimgovernor/observations_list_zones",
		func(cursor string) proto.Message {
			request := &o.ListZonesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, IncludeCells: proto.Bool(false), IncludeContents: proto.Bool(false), IncludeFilter: proto.Bool(false), Page: entityPage(zonesPage, cursor)}
			if since > 0 {
				request.ChangedSinceTick = proto.Int64(since)
			}
			return request
		},
		func() proto.Message { return &o.ListZonesReply{} },
		func(reply proto.Message) (entityPageReply[*o.ZoneState], error) {
			switch v := reply.(*o.ListZonesReply).Outcome.(type) {
			case *o.ListZonesReply_Failure:
				return entityPageReply[*o.ZoneState]{failure: v.Failure}, nil
			case *o.ListZonesReply_Unavailable:
				return entityPageReply[*o.ZoneState]{unavailable: v.Unavailable}, nil
			case *o.ListZonesReply_Observed:
				s := v.Observed
				if s == nil {
					return entityPageReply[*o.ZoneState]{}, contract("missing zones snapshot")
				}
				ids := make([]string, len(s.Zones))
				for i, row := range s.Zones {
					if row == nil {
						return entityPageReply[*o.ZoneState]{}, contract("invalid zone row")
					}
					ids[i] = row.GetId()
				}
				return entityPageReply[*o.ZoneState]{context: s.Context, completeness: s.Completeness, rows: s.Zones, ids: ids, removed: s.RemovedIds, unchanged: s.Unchanged, asOf: s.AsOfTick, limit: zonesPage}, nil
			}
			return entityPageReply[*o.ZoneState]{}, contract("missing zones outcome")
		})
}

// ReadBuildings lists every built, pending or blueprint player building
// (artificial) through observations_list_buildings, every page. A
// positive since asks for the buildings changed at or after that tick.
func (client *Client) ReadBuildings(ctx context.Context, identity *c.Identity, since int64) (EntityRows[*o.BuildingState], Result, error) {
	return readEntities(ctx, client, identity, since, "rimgovernor/observations_list_buildings",
		func(cursor string) proto.Message {
			request := &o.ListBuildingsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, PlayerOnly: proto.Bool(true), Category: proto.String("artificial"), Page: entityPage(buildingsPage, cursor)}
			if since > 0 {
				request.ChangedSinceTick = proto.Int64(since)
			}
			return request
		},
		func() proto.Message { return &o.ListBuildingsReply{} },
		func(reply proto.Message) (entityPageReply[*o.BuildingState], error) {
			switch v := reply.(*o.ListBuildingsReply).Outcome.(type) {
			case *o.ListBuildingsReply_Failure:
				return entityPageReply[*o.BuildingState]{failure: v.Failure}, nil
			case *o.ListBuildingsReply_Unavailable:
				return entityPageReply[*o.BuildingState]{unavailable: v.Unavailable}, nil
			case *o.ListBuildingsReply_Observed:
				s := v.Observed
				if s == nil {
					return entityPageReply[*o.BuildingState]{}, contract("missing buildings snapshot")
				}
				ids := make([]string, len(s.Buildings))
				for i, row := range s.Buildings {
					if row == nil || row.Building == nil {
						return entityPageReply[*o.BuildingState]{}, contract("invalid building row")
					}
					ids[i] = row.Building.GetId()
				}
				return entityPageReply[*o.BuildingState]{context: s.Context, completeness: s.Completeness, rows: s.Buildings, ids: ids, removed: s.RemovedIds, unchanged: s.Unchanged, asOf: s.AsOfTick, limit: buildingsPage}, nil
			}
			return entityPageReply[*o.BuildingState]{}, contract("missing buildings outcome")
		})
}

// ReadBillStacks lists every player bench's bill stack through
// observations_read_bills, every page, keyed by bench id. A positive since
// asks for the stacks changed at or after that tick.
func (client *Client) ReadBillStacks(ctx context.Context, identity *c.Identity, since int64) (EntityRows[*o.BillStack], Result, error) {
	return readEntities(ctx, client, identity, since, "rimgovernor/observations_read_bills",
		func(cursor string) proto.Message {
			request := &o.BillsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Page: entityPage(billsPage, cursor)}
			if since > 0 {
				request.ChangedSinceTick = proto.Int64(since)
			}
			return request
		},
		func() proto.Message { return &o.BillsReply{} },
		func(reply proto.Message) (entityPageReply[*o.BillStack], error) {
			switch v := reply.(*o.BillsReply).Outcome.(type) {
			case *o.BillsReply_Failure:
				return entityPageReply[*o.BillStack]{failure: v.Failure}, nil
			case *o.BillsReply_Unavailable:
				return entityPageReply[*o.BillStack]{unavailable: v.Unavailable}, nil
			case *o.BillsReply_Observed:
				s := v.Observed
				if s == nil {
					return entityPageReply[*o.BillStack]{}, contract("missing bills snapshot")
				}
				ids := make([]string, len(s.Benches))
				for i, row := range s.Benches {
					if row == nil || row.Bench == nil {
						return entityPageReply[*o.BillStack]{}, contract("invalid bill stack row")
					}
					ids[i] = row.Bench.GetId()
				}
				return entityPageReply[*o.BillStack]{context: s.Context, completeness: s.Completeness, rows: s.Benches, ids: ids, removed: s.RemovedIds, unchanged: s.Unchanged, asOf: s.AsOfTick, limit: billsPage}, nil
			}
			return entityPageReply[*o.BillStack]{}, contract("missing bills outcome")
		})
}

func entityPage(limit uint32, cursor string) *c.PageRequest {
	page := &c.PageRequest{Limit: proto.Uint32(limit)}
	if cursor != "" {
		page.Cursor = proto.String(cursor)
	}
	return page
}

// entityPageReply is one list page as the family's decoder hands it to
// readEntities: the outcome, or the rows with their ids in row order.
type entityPageReply[T proto.Message] struct {
	failure      *c.Failure
	unavailable  *c.Unavailable
	context      *c.ObservationContext
	completeness *o.Completeness
	rows         []T
	ids          []string
	removed      []string
	unchanged    *uint32
	asOf         *int64
	limit        int
}

// readEntities pages one entity list read to the end and folds the pages
// into EntityRows. Every page must describe the same context; a delta's
// as_of_tick is that context's tick, unchanged and removed ids are
// admitted only in a delta the read asked for, ids are valid and unique
// across pages, and a removed id is never also listed. A STALE
// unavailability on a delta is ErrDeltaExpired.
func readEntities[T proto.Message](ctx context.Context, client *Client, identity *c.Identity, since int64, method string, request func(cursor string) proto.Message, reply func() proto.Message, decode func(proto.Message) (entityPageReply[T], error)) (EntityRows[T], Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return EntityRows[T]{}, Result{}, err
	}
	if since < 0 {
		return EntityRows[T]{}, Result{}, contract("invalid entity since tick")
	}
	out := EntityRows[T]{Rows: map[string]T{}}
	removed := map[string]bool{}
	var last Result
	cursor := ""
	for pages := 0; ; pages++ {
		if pages >= 256 {
			return EntityRows[T]{}, last, contract("entity list exceeds 256 pages")
		}
		message := reply()
		raw, err := client.protoRead(ctx, method, request(cursor), message)
		last = raw
		if err != nil {
			return EntityRows[T]{}, raw, err
		}
		if err = buildingUnknown(message); err != nil {
			return EntityRows[T]{}, raw, err
		}
		page, err := decode(message)
		if err != nil {
			return EntityRows[T]{}, raw, err
		}
		switch {
		case page.failure != nil:
			return EntityRows[T]{}, raw, failure(page.failure, raw)
		case page.unavailable != nil:
			err = unavailable(page.unavailable, raw)
			if since > 0 && page.unavailable.GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_STALE {
				err = errors.Join(ErrDeltaExpired, err)
			}
			return EntityRows[T]{}, raw, err
		}
		if err = ValidateContext(page.context); err != nil {
			return EntityRows[T]{}, raw, err
		}
		if !sameIdentity(page.context.Identity, identity) {
			return EntityRows[T]{}, raw, contract("entity list identity mismatch")
		}
		delta := since > 0 && page.asOf != nil
		if out.Context != nil && (!proto.Equal(out.Context, page.context) || out.Delta != delta) {
			return EntityRows[T]{}, raw, contract("entity list pages differ in context")
		}
		out.Context, out.Delta = page.context, delta
		if page.asOf != nil && *page.asOf != page.context.GetTick() || !delta && (page.unchanged != nil && *page.unchanged != 0 || len(page.removed) != 0) {
			return EntityRows[T]{}, raw, contract("invalid entity delta")
		}
		counts := page.completeness
		if len(page.rows) > page.limit || counts == nil || counts.Page == nil || counts.Page.Complete == nil || counts.Returned != nil && counts.GetReturned() != uint64(len(page.rows)) {
			return EntityRows[T]{}, raw, contract("incomplete entity page")
		}
		for i, row := range page.rows {
			id := page.ids[i]
			if validID(id) != nil {
				return EntityRows[T]{}, raw, contract("invalid entity identity")
			}
			if _, dup := out.Rows[id]; dup {
				return EntityRows[T]{}, raw, contract("duplicate entity")
			}
			out.Rows[id] = row
		}
		for _, id := range page.removed {
			if validID(id) != nil {
				return EntityRows[T]{}, raw, contract("invalid removed entity identity")
			}
			if !removed[id] {
				removed[id] = true
				out.Removed = append(out.Removed, id)
			}
		}
		// Unchanged is counted once: every page of one read reports the
		// same whole-list count.
		if page.unchanged != nil {
			out.Unchanged = uint64(*page.unchanged)
		}
		if counts.Page.GetComplete() {
			break
		}
		cursor = counts.Page.GetNextCursor()
		if cursor == "" {
			return EntityRows[T]{}, raw, contract("entity page incomplete without a cursor")
		}
	}
	for _, id := range out.Removed {
		if _, listed := out.Rows[id]; listed {
			return EntityRows[T]{}, last, contract("removed entity also listed")
		}
	}
	return out, last, nil
}

// MergeEntities lays a delta over held rows: a listed entity replaces the
// held one, a removed id leaves, and every other held row stays. A full
// read (Delta false) replaces the held rows outright.
func MergeEntities[T proto.Message](held map[string]T, delta EntityRows[T]) map[string]T {
	if !delta.Delta {
		return delta.Rows
	}
	out := make(map[string]T, len(held)+len(delta.Rows))
	for id, row := range held {
		out[id] = row
	}
	for id, row := range delta.Rows {
		out[id] = row
	}
	for _, id := range delta.Removed {
		delete(out, id)
	}
	return out
}

// EntityDrift counts the entities on which a merged delta and a full read
// of the same tick disagree: a row in one and not the other, or a row
// whose facts differ.
func EntityDrift[T proto.Message](merged, full map[string]T) int {
	drift := 0
	for id, row := range full {
		if have, ok := merged[id]; !ok || !proto.Equal(have, row) {
			drift++
		}
	}
	for id := range merged {
		if _, ok := full[id]; !ok {
			drift++
		}
	}
	return drift
}
