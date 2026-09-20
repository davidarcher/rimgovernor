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
	// Count, PathLength and StorageHeadroom feed remote loot's reach and
	// demand filter (#522); unknown cost or storage holds a remote stack.
	Count           int64
	PathLength      domain.Fact[float64]
	StorageHeadroom domain.Fact[int64]
}

// EventLootHistory is the latest complete safety census's work, not a
// first-seen cohort. The controller owns both forbidding and allowing items.
// Held lists the safe forbidden stacks the reach stage or demand kept
// forbidden, with reasons (#522).
type EventLootHistory struct {
	Pending []StartingSupply
	Held    []LootHold `json:",omitempty"`
}

func (h EventLootHistory) Validate() error {
	if len(h.Pending) > 4096 || len(h.Held) > 4096 {
		return errors.New("event loot exceeds bound")
	}
	for i, row := range h.Held {
		if row.Thing == "" || row.Reason == "" || i > 0 && h.Held[i-1].Thing >= row.Thing {
			return errors.New("invalid event loot hold")
		}
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
