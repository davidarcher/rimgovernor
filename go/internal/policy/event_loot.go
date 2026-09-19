package policy

import (
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sort"
)

type LootItem struct {
	Supply                StartingSupply
	Forbidden, SafeToHaul bool
	SafetyKnown           bool
}

// EventLootHistory is the latest complete safety census's work, not a
// first-seen cohort. The controller owns both forbidding and allowing items.
type EventLootHistory struct{ Pending []StartingSupply }

func (h EventLootHistory) Validate() error {
	if len(h.Pending) > 4096 {
		return errors.New("event loot exceeds bound")
	}
	for i, row := range h.Pending {
		if err := row.Validate(); err != nil {
			return err
		}
		if i > 0 && h.Pending[i-1].Thing >= row.Thing {
			return errors.New("duplicate or unsorted event loot")
		}
	}
	return nil
}

func ReviewEventLoot(observed domain.Fact[[]LootItem], previous EventLootHistory) (EventLootHistory, domain.Fact[bool], error) {
	if err := previous.Validate(); err != nil {
		return EventLootHistory{}, domain.Unknown[bool](), err
	}
	rows, known := observed.Value()
	if !known {
		return previous, domain.Unknown[bool](), nil
	}
	if len(rows) > 4096 {
		return previous, domain.Unknown[bool](), errors.New("event loot census exceeds bound")
	}
	next := EventLootHistory{}
	seen := map[string]bool{}
	for _, row := range rows {
		if err := row.Supply.Validate(); err != nil {
			return previous, domain.Unknown[bool](), err
		}
		if seen[row.Supply.Thing] {
			return previous, domain.Unknown[bool](), errors.New("duplicate event loot item")
		}
		seen[row.Supply.Thing] = true
		if row.SafetyKnown && row.Forbidden == row.SafeToHaul {
			supply := row.Supply
			supply.Forbid = !row.SafeToHaul
			next.Pending = append(next.Pending, supply)
		}
	}
	sort.Slice(next.Pending, func(i, j int) bool { return next.Pending[i].Thing < next.Pending[j].Thing })
	return next, domain.Known(len(next.Pending) > 0), nil
}

func supplySafetyPriority(f RoutineFacts) int {
	if rows, known := f.EventLoot.Value(); known {
		for _, row := range rows {
			if row.SafetyKnown && !row.Forbidden && !row.SafeToHaul {
				return 0
			}
		}
		return 2
	}
	return 4
}
