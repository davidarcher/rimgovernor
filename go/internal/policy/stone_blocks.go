package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// stoneChunkBlocks maps each Core stone chunk definition to the block
// definition its generated stonecutter recipe (Make_StoneBlocks<Stone>)
// produces. Slag and other non-stone chunks have no block recipe and are
// not listed.
var stoneChunkBlocks = map[Resource]Resource{
	"ChunkGranite":   "BlocksGranite",
	"ChunkLimestone": "BlocksLimestone",
	"ChunkMarble":    "BlocksMarble",
	"ChunkSandstone": "BlocksSandstone",
	"ChunkSlate":     "BlocksSlate",
}

// StoneBlockResource reports whether resource is a Core stone block
// definition, the product of a stonecutter bill StoneBlockTarget can raise.
func StoneBlockResource(resource Resource) bool {
	for _, blocks := range stoneChunkBlocks {
		if blocks == resource {
			return true
		}
	}
	return false
}

// StoneBlockTarget derives the one MaintainResource stone-block target a
// floor of StoneBlockTarget asks for: the block definition of the stone
// whose chunks the census counts most (ties break on the block name), so
// the stonecutter bill is always fed by chunks the map already holds. A
// map without stone chunks yields no target (ok false): the ladder can
// build a bench and set a bill, but it does not mine rock for chunks.
func StoneBlockTarget(floor int64, stock domain.Fact[[]Amount]) (resource Resource, target int64, ok bool, err error) {
	if floor < 0 || floor > 10000 {
		return "", 0, false, errors.New("invalid stone block target")
	}
	rows, known := stock.Value()
	if floor == 0 || !known {
		return "", 0, false, nil
	}
	if len(rows) > 4096 {
		return "", 0, false, errors.New("resource stock census exceeds bound")
	}
	chunks := map[Resource]int64{}
	for _, row := range rows {
		if row.Count < 0 {
			return "", 0, false, errors.New("invalid resource stock")
		}
		if _, stone := stoneChunkBlocks[row.Resource]; stone {
			chunks[row.Resource] += row.Count
		}
	}
	names := make([]Resource, 0, len(chunks))
	for name, count := range chunks {
		if count > 0 {
			names = append(names, name)
		}
	}
	if len(names) == 0 {
		return "", 0, false, nil
	}
	sort.Slice(names, func(i, j int) bool {
		if chunks[names[i]] != chunks[names[j]] {
			return chunks[names[i]] > chunks[names[j]]
		}
		return stoneChunkBlocks[names[i]] < stoneChunkBlocks[names[j]]
	})
	return stoneChunkBlocks[names[0]], floor, true, nil
}

// ResourceGoalConfigured reports whether MaintainResource has any target to
// keep: an operator resource floor or a stone-block floor.
func (p RoutinePolicy) ResourceGoalConfigured() bool {
	return len(p.ResourceTargets) > 0 || len(p.GearSpareTargets) > 0 || p.StoneBlockTarget > 0
}

// TracksResource reports whether resource is one MaintainResource keeps a
// floor for, so a workshop ladder record for it still drives research.
func (p RoutinePolicy) TracksResource(resource Resource) bool {
	return p.ResourceTargets[resource] > 0 || p.GearSpareTargets[resource] > 0 || p.StoneBlockTarget > 0 && StoneBlockResource(resource) || resource == "MedicineHerbal" && p.MedicalReserve.TargetPerColonist > 0
}

// EffectiveResourceTargets is the MaintainResource target map one review or
// planner step selects from: the operator's ResourceTargets, the
// stone-block target StoneBlockTarget derives from the same stock census
// (an operator floor for the same block definition wins) and the derived
// needs other goals' evidence asks for (ResourceGoalTargets, which never
// lowers a floor already present). With no stone chunks observed, or the
// census unknown, the stone floor contributes nothing.
func (p RoutinePolicy) EffectiveResourceTargets(stock domain.Fact[[]Amount], needs map[Resource]int64) (map[Resource]int64, error) {
	needs = ResourceGoalTargets(needs, p.GearSpareTargets)
	resource, target, ok, err := StoneBlockTarget(p.StoneBlockTarget, stock)
	if err != nil {
		return nil, err
	}
	if !ok {
		return ResourceGoalTargets(p.ResourceTargets, needs), nil
	}
	targets := make(map[Resource]int64, len(p.ResourceTargets)+1)
	for name, want := range p.ResourceTargets {
		targets[name] = want
	}
	if _, configured := targets[resource]; !configured {
		targets[resource] = target
	}
	return ResourceGoalTargets(targets, needs), nil
}
