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

// ReadZones lists every zone on the map (bounds and settings, no cells,
// contents or filter) through observations_list_zones in one complete reply.
func (client *Client) ReadZones(ctx context.Context, identity *c.Identity) (EntityRows[*o.ZoneState], Result, error) {
	return readEntities(ctx, client, identity, "rimgovernor/observations_list_zones",
		func() proto.Message { return zonesListRequest(identity) },
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
				return entityPageReply[*o.ZoneState]{context: s.Context, rows: s.Zones, ids: ids}, nil
			}
			return entityPageReply[*o.ZoneState]{}, contract("missing zones outcome")
		})
}

// ReadBuildings lists every built, pending or blueprint player building
// (artificial) through observations_list_buildings in one complete reply.
func (client *Client) ReadBuildings(ctx context.Context, identity *c.Identity) (EntityRows[*o.BuildingState], Result, error) {
	return readEntities(ctx, client, identity, "rimgovernor/observations_list_buildings",
		func() proto.Message { return buildingsListRequest(identity) },
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
				return entityPageReply[*o.BuildingState]{context: s.Context, rows: s.Buildings, ids: ids}, nil
			}
			return entityPageReply[*o.BuildingState]{}, contract("missing buildings outcome")
		})
}

// ReadBillStacks lists every player bench's bill stack through
// observations_read_bills, keyed by bench id.
func (client *Client) ReadBillStacks(ctx context.Context, identity *c.Identity) (EntityRows[*o.BillStack], Result, error) {
	return readEntities(ctx, client, identity, "rimgovernor/observations_read_bills",
		func() proto.Message { return billsListRequest(identity) },
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
				return entityPageReply[*o.BillStack]{context: s.Context, rows: s.Benches, ids: ids}, nil
			}
			return entityPageReply[*o.BillStack]{}, contract("missing bills outcome")
		})
}

// The entity list requests, shared with the bundle's step families so a
// section the bundle carries is seeded under the key the read uses (#593).
func zonesListRequest(identity *c.Identity) *o.ListZonesRequest {
	return &o.ListZonesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, IncludeContents: proto.Bool(false), IncludeFilter: proto.Bool(false)}
}

func buildingsListRequest(identity *c.Identity) *o.ListBuildingsRequest {
	return &o.ListBuildingsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, PlayerOnly: proto.Bool(true), Category: proto.String("artificial")}
}

func billsListRequest(identity *c.Identity) *o.BillsRequest {
	return &o.BillsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
}

// entityPageReply is one list reply as the family's decoder hands it to
// readEntities: the outcome, or the rows with their ids in row order.
type entityPageReply[T proto.Message] struct {
	failure     *c.Failure
	unavailable *c.Unavailable
	context     *c.ObservationContext
	rows        []T
	ids         []string
}

// readEntities reads one complete entity list and folds it into EntityRows.
// ids are valid and unique.
func readEntities[T proto.Message](ctx context.Context, client *Client, identity *c.Identity, method string, request func() proto.Message, reply func() proto.Message, decode func(proto.Message) (entityPageReply[T], error)) (EntityRows[T], Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return EntityRows[T]{}, Result{}, err
	}
	out := EntityRows[T]{Rows: map[string]T{}}
	message := reply()
	raw, err := client.protoRead(ctx, method, request(), message)
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
	out.Context = page.context
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
	return out, raw, nil
}
