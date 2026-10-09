package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// packedSource lists stored packed items; a source without it builds new pieces instead.
type packedSource interface {
	ReadPackedItems(context.Context, *c.Identity, string) ([]bridge.PackedItem, bridge.Result, error)
}

var _ packedSource = (*bridge.Client)(nil)

// packedStock is one pass's view of the colony's packed stock: each packed
// definition is read at most once and shared by every owner of the pass.
// Stock comes first: an owner needing a piece asks Install and builds only
// when it answers false. Not safe for concurrent use.
type packedStock struct {
	source   packedSource // nil: no stock readable
	identity *c.Identity
	read     map[string][]bridge.PackedItem
	claimed  map[string]bool // packed things Install handed out this pass
}

func newPackedStock(native any, identity *c.Identity) *packedStock {
	source, _ := native.(packedSource)
	return &packedStock{source: source, identity: identity, read: map[string][]bridge.PackedItem{}, claimed: map[string]bool{}}
}

// Items is the packed items of one packed definition; ok is false when the
// source cannot read stock.
func (s *packedStock) Items(call context.Context, packedDef string) (items []bridge.PackedItem, ok bool, err error) {
	if s.source == nil {
		return nil, false, nil
	}
	if items, done := s.read[packedDef]; done {
		return items, true, nil
	}
	items, _, err = s.source.ReadPackedItems(call, s.identity, packedDef)
	if err != nil {
		return nil, true, err
	}
	s.read[packedDef] = items
	return items, true, nil
}

// Install is the move that installs a stored packed piece of def (packed as
// packedDef) at anchor/rot; false when none is stored.
func (s *packedStock) Install(call context.Context, packedDef, def string, anchor domain.Cell, rot domain.Rotation) (domain.MoveBuilding, bool, error) {
	items, _, err := s.Items(call, packedDef)
	if err != nil {
		return domain.MoveBuilding{}, false, err
	}
	for _, item := range items {
		if item.InnerDef != def || s.claimed[item.Inner] {
			continue
		}
		move, err := domain.NewMoveBuilding(item.Inner, def, anchor, rot)
		s.claimed[item.Inner] = err == nil
		return move, err == nil, err
	}
	return domain.MoveBuilding{}, false, nil
}
