package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// EntityRows is one whole entity list read (zones, buildings or bill
// stacks) keyed by entity id, with the tick the reply described.
type EntityRows[T proto.Message] struct {
	Context *c.ObservationContext
	Rows    map[string]T
}

// AsOf is the tick the reply described.
func (e EntityRows[T]) AsOf() int64 { return e.Context.GetTick() }

const (
	zonesPage     = 16
	buildingsPage = 256
	billsPage     = 256
)

// ReadZones lists every zone on the map (bounds and settings, no cells,
// contents or filter) through observations_list_zones, every page.
func (client *Client) ReadZones(ctx context.Context, identity *c.Identity) (EntityRows[*o.ZoneState], Result, error) {
	return readEntities(ctx, client, identity, "rimgovernor/observations_list_zones",
		func(cursor string) proto.Message { return zonesListRequest(identity, cursor) },
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
				return entityPageReply[*o.ZoneState]{context: s.Context, completeness: s.Completeness, rows: s.Zones, ids: ids, limit: zonesPage}, nil
			}
			return entityPageReply[*o.ZoneState]{}, contract("missing zones outcome")
		})
}

// ReadBuildings lists every built, pending or blueprint player building
// (artificial) through observations_list_buildings, every page.
func (client *Client) ReadBuildings(ctx context.Context, identity *c.Identity) (EntityRows[*o.BuildingState], Result, error) {
	return readEntities(ctx, client, identity, "rimgovernor/observations_list_buildings",
		func(cursor string) proto.Message { return buildingsListRequest(identity, cursor) },
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
				return entityPageReply[*o.BuildingState]{context: s.Context, completeness: s.Completeness, rows: s.Buildings, ids: ids, limit: buildingsPage}, nil
			}
			return entityPageReply[*o.BuildingState]{}, contract("missing buildings outcome")
		})
}

// ReadBillStacks lists every player bench's bill stack through
// observations_read_bills, every page, keyed by bench id.
func (client *Client) ReadBillStacks(ctx context.Context, identity *c.Identity) (EntityRows[*o.BillStack], Result, error) {
	return readEntities(ctx, client, identity, "rimgovernor/observations_read_bills",
		func(cursor string) proto.Message { return billsListRequest(identity, cursor) },
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
				return entityPageReply[*o.BillStack]{context: s.Context, completeness: s.Completeness, rows: s.Benches, ids: ids, limit: billsPage}, nil
			}
			return entityPageReply[*o.BillStack]{}, contract("missing bills outcome")
		})
}

// The entity list requests, shared with the bundle's step families so a
// section the bundle carries is seeded under the key the read uses (#593).
func zonesListRequest(identity *c.Identity, cursor string) *o.ListZonesRequest {
	return &o.ListZonesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, IncludeCells: proto.Bool(false), IncludeContents: proto.Bool(false), IncludeFilter: proto.Bool(false), Page: entityPage(zonesPage, cursor)}
}

func buildingsListRequest(identity *c.Identity, cursor string) *o.ListBuildingsRequest {
	return &o.ListBuildingsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, PlayerOnly: proto.Bool(true), Category: proto.String("artificial"), Page: entityPage(buildingsPage, cursor)}
}

func billsListRequest(identity *c.Identity, cursor string) *o.BillsRequest {
	return &o.BillsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Page: entityPage(billsPage, cursor)}
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
	limit        int
}

// readEntities pages one entity list read to the end and folds the pages
// into EntityRows. Every page must describe the same context, and ids are
// valid and unique across pages.
func readEntities[T proto.Message](ctx context.Context, client *Client, identity *c.Identity, method string, request func(cursor string) proto.Message, reply func() proto.Message, decode func(proto.Message) (entityPageReply[T], error)) (EntityRows[T], Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return EntityRows[T]{}, Result{}, err
	}
	out := EntityRows[T]{Rows: map[string]T{}}
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
			return EntityRows[T]{}, raw, unavailable(page.unavailable, raw)
		}
		if err = ValidateContext(page.context); err != nil {
			return EntityRows[T]{}, raw, err
		}
		if !sameIdentity(page.context.Identity, identity) {
			return EntityRows[T]{}, raw, contract("entity list identity mismatch")
		}
		if out.Context != nil && !proto.Equal(out.Context, page.context) {
			return EntityRows[T]{}, raw, contract("entity list pages differ in context")
		}
		out.Context = page.context
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
		if counts.Page.GetComplete() {
			return out, last, nil
		}
		cursor = counts.Page.GetNextCursor()
		if cursor == "" {
			return EntityRows[T]{}, raw, contract("entity page incomplete without a cursor")
		}
	}
}
