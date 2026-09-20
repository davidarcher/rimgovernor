package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
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
		if len(row.Caskets) > shrineCasketLimit || len(row.Guards) > shrineGuardLimit || len(row.BreachWalls) > shrineBreachLimit || len(row.Occupants) > shrineGuardLimit {
			return contract("shrine rows exceed the bound")
		}
		if !row.GetGuardsKnown() && len(row.Guards) != 0 {
			return contract("guards reported while unknown")
		}
		entities := map[string]bool{}
		for _, casket := range row.Caskets {
			if casket == nil || validID(casket.GetEntityId()) != nil || entities[casket.GetEntityId()] || validCell(casket.Cell) != nil || validCell(casket.InteractionCell) != nil || casket.HitPoints == nil || casket.MaxHitPoints == nil || casket.GetMaxHitPoints() == 0 || casket.GetHitPoints() > casket.GetMaxHitPoints() || casket.HasContents == nil || casket.PlayerClaimed == nil {
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
		// A hostile occupant is also listed as a guard, so occupants keep
		// their own identity set.
		occupants := map[string]bool{}
		for _, occupant := range row.Occupants {
			if occupant == nil || validID(occupant.GetEntityId()) != nil || occupants[occupant.GetEntityId()] || occupant.Hostile == nil || occupant.Downed == nil || occupant.Dead == nil || occupant.Prisoner == nil || occupant.Faction == nil {
				return contract("invalid shrine occupant")
			}
			occupants[occupant.GetEntityId()] = true
		}
		if !row.GetGuardsKnown() && len(row.Occupants) != 0 {
			return contract("occupants reported while unknown")
		}
		if h := row.Heat; h != nil {
			if !row.GetGuardsKnown() || h.TemperatureCelsius == nil || h.OutdoorTemperatureCelsius == nil || math.IsNaN(h.GetTemperatureCelsius()) || math.IsInf(h.GetTemperatureCelsius(), 0) || math.IsNaN(h.GetOutdoorTemperatureCelsius()) || math.IsInf(h.GetOutdoorTemperatureCelsius(), 0) || h.CellCount == nil || h.GetCellCount() == 0 || h.GetCellCount() > 256 || h.BoundaryCells == nil || h.GetBoundaryCells() == 0 || h.GetBoundaryCells() > 1024 || h.Enclosed == nil || h.ColonistsInside == nil || len(h.DoorSites) > 1 || len(h.HeaterSites) > 256 || len(h.Heaters) > 256 || len(h.FiringCells) > 64 || len(h.RetreatCells) != len(h.FiringCells) || h.GetEnclosed() && len(h.DoorSites) > 0 {
				return contract("invalid shrine heat")
			}
			for _, cells := range [][]*c.Cell{h.DoorSites, h.HeaterSites, h.FiringCells, h.RetreatCells} {
				seenCells := map[[2]int32]bool{}
				for _, cell := range cells {
					key := [2]int32{cell.GetX(), cell.GetZ()}
					if validCell(cell) != nil || seenCells[key] {
						return contract("invalid shrine heat cell")
					}
					seenCells[key] = true
				}
			}
			for i, firing := range h.FiringCells {
				if !adjacentCells(firing, h.RetreatCells[i]) {
					return contract("invalid shrine retreat")
				}
			}
			heaters := map[string]bool{}
			for _, heater := range h.Heaters {
				if heater == nil || validID(heater.GetId()) != nil || heaters[heater.GetId()] || heater.GetDefName() != "Heater" || validCell(heater.Position) != nil {
					return contract("invalid shrine heater")
				}
				heaters[heater.GetId()] = true
			}
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
