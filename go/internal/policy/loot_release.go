package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Pawn deaths drop forbidden items. Pawn.Kill forbids the corpse when
// it falls outside the Home area, and Pawn.DropAndForbidEverything drops the
// dead pawn's equipment and inventory with forbid: true; apparel stays on
// the corpse. So a dead colonist's corpse and gear arrive forbidden and are
// released by ManageSupplySafety's standing census (ReviewEventLoot), one
// stack at a time. Release is selective: FilterLootRelease keeps back what a
// native spawner forbids on purpose and whatever lies in danger.

// Hold reasons FilterLootRelease records.
const (
	LootHoldSpawnForbidden = "spawn_forbidden"
	LootHoldDanger         = "danger"
)

// FilterLootRelease narrows the census's release side (a safe, forbidden
// stack): a stack whose def a native spawner forbids on purpose is never
// released, and neither is one within ThreatReachCells of a danger seed (a
// live hostile, a hive: DangerSeeds). Unknown seeds hold nothing back: the
// native census's own route check still refuses an item a live hostile can
// see. Forbidding an unsafe stack and allowed stacks are untouched. The
// returned rows are the census the safety review acts on.
func FilterLootRelease(observed domain.Fact[[]LootItem], seeds domain.Fact[[]domain.Cell]) (domain.Fact[[]LootItem], []LootHold) {
	rows, known := observed.Value()
	if !known {
		return observed, nil
	}
	danger, _ := seeds.Value()
	var kept []LootItem
	var holds []LootHold
	for _, row := range rows {
		if !row.SafetyKnown || !row.SafeToHaul || !row.Forbidden {
			kept = append(kept, row)
			continue
		}
		reason := ""
		switch {
		case row.SpawnForbidden:
			reason = LootHoldSpawnForbidden
		case NearDanger(danger, row.Supply.Cell):
			reason = LootHoldDanger
		}
		if reason == "" {
			kept = append(kept, row)
			continue
		}
		holds = append(holds, LootHold{Thing: row.Supply.Thing, Definition: row.Supply.Definition, Cell: row.Supply.Cell, Reason: reason})
	}
	sort.Slice(holds, func(i, j int) bool { return holds[i].Thing < holds[j].Thing })
	return domain.Known(kept), holds
}
