package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type EquipCandidate struct {
	Thing, Definition string
	Cell              domain.Cell
	Token             string
	// ByTrade is the definition's membership in the Weapons thing category,
	// Ranged/Melee its IsRangedWeapon/IsMeleeWeapon (#287): a wood log or a
	// beer is a melee weapon by the def flags but not a weapon by trade.
	ByTrade, Ranged, Melee bool
}
type EquipRead struct {
	Context *c.ObservationContext
	Targets []EquipCandidate
}

// ReadEquipWeapons observes loose weapons within one rectangle, forbidden or
// not: equipping a forbidden weapon is simply refused downstream by the
// native preview, not filtered out here.
func (client *Client) ReadEquipWeapons(ctx context.Context, identity *c.Identity, minimum, maximum domain.Cell) (EquipRead, Result, error) {
	if ValidateIdentity(identity) != nil || minimum.X < 0 || minimum.Z < 0 || maximum.X < minimum.X || maximum.Z < minimum.Z {
		return EquipRead{}, Result{}, contract("invalid equip scope")
	}
	region := &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(minimum.X), Z: proto.Int32(minimum.Z)}, Maximum: &c.Cell{X: proto.Int32(maximum.X), Z: proto.Int32(maximum.Z)}}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Filter: &o.StockFilter{Category: proto.String("weapons"), Ownership: proto.String("ours"), IncludeHeld: proto.Bool(false), ForbiddenOnly: proto.Bool(false), Region: region}, Page: &c.PageRequest{Limit: proto.Uint32(256)}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return EquipRead{}, raw, err
	}
	if reply.GetFailure() != nil {
		return EquipRead{}, raw, failure(reply.GetFailure(), raw)
	}
	result, err := decodeEquipWeapons(reply, identity, minimum, maximum)
	return result, raw, err
}
func decodeEquipWeapons(reply *o.ListSuppliesReply, identity *c.Identity, minimum, maximum domain.Cell) (EquipRead, error) {
	if reply == nil || buildingUnknown(reply) != nil {
		return EquipRead{}, contract("invalid equip reply")
	}
	v := reply.GetObserved()
	if v == nil || buildingContext(v.Context, identity, 0, false) != nil || len(v.Stocks) > 256 {
		return EquipRead{}, contract("equip census unavailable")
	}
	complete, err := emergencyCompleteness(v.Completeness, len(v.Stocks))
	if yes, known := complete.Value(); err != nil || !known || !yes {
		return EquipRead{}, contract("incomplete equip census")
	}
	out := EquipRead{Context: proto.Clone(v.Context).(*c.ObservationContext), Targets: []EquipCandidate{}}
	seen := map[string]bool{}
	for _, stock := range v.Stocks {
		if stock == nil || stock.Units == nil || stock.GetUnits() < 0 || len(stock.Items) > 256 {
			return EquipRead{}, contract("invalid equip stock")
		}
		if stock.WeaponByTrade == nil || stock.Ranged == nil || stock.Melee == nil || (stock.GetRanged() && stock.GetMelee()) {
			return EquipRead{}, contract("equip stock lacks weapon class")
		}
		complete, err = emergencyCompleteness(stock.ItemsCompleteness, len(stock.Items))
		if yes, known := complete.Value(); err != nil || !known || !yes {
			return EquipRead{}, contract("incomplete equip items")
		}
		for _, item := range stock.Items {
			if item == nil || validID(item.GetId()) != nil || seen[item.GetId()] || item.MapId == nil || item.GetMapId() != identity.GetMapId() || item.Position == nil || item.Position.X == nil || item.Position.Z == nil || item.Position.GetX() < minimum.X || item.Position.GetX() > maximum.X || item.Position.GetZ() < minimum.Z || item.Position.GetZ() > maximum.Z || item.GetDefName() != stock.GetDefinition().GetDefName() {
				return EquipRead{}, contract("equip entity mismatch")
			}
			seen[item.GetId()] = true
			if len(seen) > 256 {
				return EquipRead{}, contract("equip item census exceeds bound")
			}
			// Native issues distinguish ineligible items from an incomplete census.
			if item.Snapshot == nil {
				continue
			}
			if item.Snapshot.GetEntityId() != item.GetId() || !proto.Equal(item.Snapshot.Context, v.Context) || validID(item.Snapshot.GetToken()) != nil {
				return EquipRead{}, contract("equip CAS scope mismatch")
			}
			cell := domain.Cell{X: item.Position.GetX(), Z: item.Position.GetZ()}
			out.Targets = append(out.Targets, EquipCandidate{Thing: item.GetId(), Definition: item.GetDefName(), Cell: cell, Token: item.Snapshot.GetToken(), ByTrade: stock.GetWeaponByTrade(), Ranged: stock.GetRanged(), Melee: stock.GetMelee()})
			if len(out.Targets) > 256 {
				return EquipRead{}, contract("equip targets exceed bound")
			}
		}
	}
	return out, nil
}
