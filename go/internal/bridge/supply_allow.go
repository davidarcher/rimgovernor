package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

type SupplyTarget struct {
	Supply domain.SupplyAllow
}
type SupplyRead struct {
	Context *c.ObservationContext
	Targets []SupplyTarget
}

func (client *Client) ReadAllowSupplies(ctx context.Context, identity *c.Identity, cell domain.Cell) (SupplyRead, Result, error) {
	return client.readSupplyAccess(ctx, identity, cell, false)
}
func (client *Client) ReadForbidSupplies(ctx context.Context, identity *c.Identity, cell domain.Cell) (SupplyRead, Result, error) {
	return client.readSupplyAccess(ctx, identity, cell, true)
}

// ReadFoodReserveSupplies locates durable shared food through the existing
// supply census; the food forecast intentionally carries no item positions.
func (client *Client) ReadFoodReserveSupplies(ctx context.Context, identity *c.Identity, forbid bool) (SupplyRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return SupplyRead{}, Result{}, err
	}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Filter: &o.StockFilter{DefNames: []string{"Pemmican", "MealSurvivalPack"}, Ownership: o.StockOwnership_STOCK_OWNERSHIP_OURS.Enum(), IncludeHeld: proto.Bool(false), ForbiddenOnly: proto.Bool(!forbid)}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return SupplyRead{}, raw, err
	}
	result, err := decodeSupplyAccessAt(reply, identity, nil, forbid)
	for _, target := range result.Targets {
		if def := target.Supply.Definition(); def != "Pemmican" && def != "MealSurvivalPack" {
			return SupplyRead{}, raw, contract("unexpected reserve definition")
		}
	}
	return result, raw, err
}
func (client *Client) readSupplyAccess(ctx context.Context, identity *c.Identity, cell domain.Cell, forbid bool) (SupplyRead, Result, error) {
	if ValidateIdentity(identity) != nil || cell.X < 0 || cell.Z < 0 {
		return SupplyRead{}, Result{}, contract("invalid supply scope")
	}
	point := &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Filter: &o.StockFilter{Category: o.StockCategory_STOCK_CATEGORY_HAULABLE.Enum(), Ownership: o.StockOwnership_STOCK_OWNERSHIP_OURS.Enum(), IncludeHeld: proto.Bool(false), ForbiddenOnly: proto.Bool(!forbid), Region: &o.Rectangle{Minimum: point, Maximum: proto.Clone(point).(*c.Cell)}}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return SupplyRead{}, raw, err
	}
	if reply.GetFailure() != nil {
		return SupplyRead{}, raw, failure(reply.GetFailure(), raw)
	}
	result, err := decodeSupplyAccess(reply, identity, cell, forbid)
	return result, raw, err
}
func decodeSupplyAccess(reply *o.ListSuppliesReply, identity *c.Identity, cell domain.Cell, forbid bool) (SupplyRead, error) {
	return decodeSupplyAccessAt(reply, identity, &cell, forbid)
}
func decodeSupplyAccessAt(reply *o.ListSuppliesReply, identity *c.Identity, at *domain.Cell, forbid bool) (SupplyRead, error) {
	if reply == nil || buildingUnknown(reply) != nil {
		return SupplyRead{}, contract("invalid supply reply")
	}
	v := reply.GetObserved()
	if v == nil || buildingContext(v.Context, identity, 0, false) != nil {
		return SupplyRead{}, contract("supply census unavailable")
	}
	if v.Completeness == nil || v.Completeness.GetFiltered() != 0 {
		return SupplyRead{}, contract("incomplete supply census")
	}
	out := SupplyRead{Context: proto.Clone(v.Context).(*c.ObservationContext), Targets: []SupplyTarget{}}
	seen := map[string]bool{}
	for _, stock := range v.Stocks {
		if stock == nil || stock.Units == nil || stock.Forbidden == nil || stock.GetUnits() < 0 || stock.GetForbidden() < 0 || stock.GetForbidden() > stock.GetUnits() || !forbid && stock.GetForbidden() != stock.GetUnits() {
			return SupplyRead{}, contract("invalid forbidden supply stock")
		}
		def := stock.GetDefinition().GetDefName()
		for _, item := range stock.Items {
			id, pos := item.GetItem().GetId(), item.GetCell()
			if !validRef(item.GetItem()) || seen[id] || pos == nil || pos.X == nil || pos.Z == nil || pos.GetX() < 0 || pos.GetZ() < 0 {
				return SupplyRead{}, contract("supply entity mismatch")
			}
			cell := domain.Cell{X: pos.GetX(), Z: pos.GetZ()}
			if at != nil && cell != *at {
				return SupplyRead{}, contract("supply cell mismatch")
			}
			seen[id] = true
			// Native issues distinguish ineligible items from an incomplete census.
			if item.Snapshot == nil {
				continue
			}
			if !refSnapshot(item.Snapshot, item.GetItem(), v.Context) {
				return SupplyRead{}, contract("supply CAS scope mismatch")
			}
			supply, err := domain.NewSupplyAllow(id, def, cell)
			if err != nil {
				return SupplyRead{}, err
			}
			if forbid {
				supply, err = domain.NewSupplyForbid(id, def, cell)
				if err != nil {
					return SupplyRead{}, err
				}
			}
			out.Targets = append(out.Targets, SupplyTarget{supply})
		}
	}
	return out, nil
}

// supplyAction is the DesignateIntent that allows or forbids one exact
// supply item. Native checks the item live when it applies; an item already
// in the wanted state applies again.
func supplyAction(action domain.Action) (*op.Action, error) {
	v, ok := action.SupplyAllow()
	if !ok {
		return nil, contract("not a supply action")
	}
	if validID(v.Thing()) != nil {
		return nil, contract("supply target invalid")
	}
	designation := op.ThingDesignation_THING_DESIGNATION_ALLOW
	if v.Forbidden() {
		designation = op.ThingDesignation_THING_DESIGNATION_FORBID
	}
	return &op.Action{Intent: &op.Action_Designate{Designate: &op.DesignateIntent{ThingId: proto.String(v.Thing()), Designation: designation.Enum()}}}, nil
}
