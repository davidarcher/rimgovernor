package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const shrinesTool = "rimgovernor/observations_get_ancient_shrines"
const (
	shrineLimit        = 64
	shrineCasketLimit  = 32
	shrineGuardLimit   = 256
	shrineBreachLimit  = 64
	shrineRoomMaxWidth = 256
)

// ReadAncientShrines is read-only and requires no authority. An unsupported
// native stub returns ErrUnavailable, never a successful empty census.
func (client *Client) ReadAncientShrines(ctx context.Context, identity *c.Identity) (*o.AncientShrinesReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &o.AncientShrinesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	reply := &o.AncientShrinesReply{}
	raw, err := client.protoRead(ctx, shrinesTool, request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err := buildingUnknown(reply); err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.AncientShrinesReply_Observed:
		err = ValidateAncientShrines(v.Observed, identity)
	case *o.AncientShrinesReply_Unavailable:
		err = unavailable(v.Unavailable, raw)
	case *o.AncientShrinesReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("shrine outcome missing")
	}
	return reply, raw, err
}

// ValidateAncientShrines rejects partial censuses and omitted safety facts.
// Guards may be empty on a sealed shrine only because guards_known is false;
// an opened shrine with no guards is a complete, empty list.
func ValidateAncientShrines(v *o.AncientShrinesSnapshot, identity *c.Identity) error {
	if v == nil || buildingUnknown(v) != nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return contract("invalid shrine context")
	}
	n := uint64(len(v.Shrines))
	p := v.Completeness
	if n > shrineLimit || p == nil || p.Page == nil || !p.Page.GetComplete() || p.Page.GetNextCursor() != "" || p.Matched == nil || p.Returned == nil || p.Filtered == nil || p.Unreadable == nil || p.GetMatched() != n || p.GetReturned() != n || p.GetFiltered() != 0 || p.GetUnreadable() != 0 {
		return contract("incomplete shrine census")
	}
	seen := map[string]bool{}
	for _, row := range v.Shrines {
		if row == nil || validID(row.GetShrineId()) != nil || seen[row.GetShrineId()] || row.Sealed == nil || row.InHome == nil || row.GuardsKnown == nil || row.GetSealed() && row.GetGuardsKnown() {
			return contract("invalid shrine")
		}
		seen[row.GetShrineId()] = true
		if err := validRectangle(row.Room, shrineRoomMaxWidth); err != nil {
			return contract("invalid shrine room")
		}
		if len(row.Caskets) > shrineCasketLimit || len(row.Guards) > shrineGuardLimit || len(row.BreachWalls) > shrineBreachLimit {
			return contract("shrine rows exceed the bound")
		}
		if !row.GetGuardsKnown() && len(row.Guards) != 0 {
			return contract("guards reported while unknown")
		}
		entities := map[string]bool{}
		for _, casket := range row.Caskets {
			if casket == nil || validID(casket.GetEntityId()) != nil || entities[casket.GetEntityId()] || validCell(casket.Cell) != nil || casket.HitPoints == nil || casket.MaxHitPoints == nil || casket.GetMaxHitPoints() == 0 || casket.GetHitPoints() > casket.GetMaxHitPoints() || casket.HasContents == nil || casket.PlayerClaimed == nil {
				return contract("invalid shrine casket")
			}
			entities[casket.GetEntityId()] = true
		}
		for _, guard := range row.Guards {
			if guard == nil || validID(guard.GetEntityId()) != nil || entities[guard.GetEntityId()] || guard.Downed == nil || guard.Dead == nil || guard.Kind < o.ShrineGuardKind_SHRINE_GUARD_KIND_MECHANOID || guard.Kind > o.ShrineGuardKind_SHRINE_GUARD_KIND_OTHER {
				return contract("invalid shrine guard")
			}
			entities[guard.GetEntityId()] = true
		}
		for _, wall := range row.BreachWalls {
			if wall == nil || validID(wall.GetEntityId()) != nil || entities[wall.GetEntityId()] || validCell(wall.Cell) != nil || validCell(wall.Outside) != nil || !adjacentCells(wall.Cell, wall.Outside) {
				return contract("invalid shrine breach wall")
			}
			entities[wall.GetEntityId()] = true
		}
	}
	return nil
}

func validRectangle(rect *o.Rectangle, maxWidth int64) error {
	if rect == nil || validCell(rect.Minimum) != nil || validCell(rect.Maximum) != nil || rect.Maximum.GetX() < rect.Minimum.GetX() || rect.Maximum.GetZ() < rect.Minimum.GetZ() || int64(rect.Maximum.GetX())-int64(rect.Minimum.GetX()) >= maxWidth || int64(rect.Maximum.GetZ())-int64(rect.Minimum.GetZ()) >= maxWidth {
		return contract("invalid rectangle")
	}
	return nil
}

func adjacentCells(a, b *c.Cell) bool {
	dx, dz := int64(a.GetX())-int64(b.GetX()), int64(a.GetZ())-int64(b.GetZ())
	return dx*dx+dz*dz == 1
}
