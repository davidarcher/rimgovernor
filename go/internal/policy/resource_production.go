package policy

import (
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainResource-* opening slice: pure, native-shape-preserving primitives
// ported from controller/rimgovernor/production_policy.py's
// ingredient_deficits/observe_mining_progress. No domain/store/executor/
// bridge/buildingruntime wiring exists yet for this goal family (see
// docs/BACKLOG.md 05.5) — resource_method's own dynamic-target selection,
// acquisition/mining dispatch, material-storage zoning and bill placement
// are a same-sized effort to GearReplace/EnsureResearch and remain entirely
// unstarted. These two functions only make its deterministic cost/deficit
// and excavation-progress comparisons available and independently testable
// ahead of that wiring, the same posture 05.5's other slices opened from.

// ResourceRequirement is one native recipe-ingredient alternative's exact
// required quantity and the deficit against current stock.
type ResourceRequirement struct {
	Resource Resource
	Required int64
	Deficit  int64
}

// ResourceRecipeDeficits computes, per ingredient slot alternative, the exact
// native-required amount and the deficit against current stock. Mirrors
// production_policy.py's ingredient_deficits: unknown per-alternative native
// ingredient quantities (an unreadable recipe) can never be guessed at, so an
// Unknown ingredients fact is refused rather than treated as zero-cost. The
// [][]Amount shape matches policy.GearRecipe.Ingredients so both gear and
// resource production bills share one recipe representation.
func ResourceRecipeDeficits(ingredients domain.Fact[[][]Amount], stock map[Resource]int64) ([][]ResourceRequirement, error) {
	slots, known := ingredients.Value()
	if !known {
		return nil, errors.New("exact native ingredient quantities unavailable")
	}
	result := make([][]ResourceRequirement, 0, len(slots))
	for _, choices := range slots {
		row := make([]ResourceRequirement, 0, len(choices))
		for _, choice := range choices {
			deficit := choice.Count - stock[choice.Resource]
			if deficit < 0 {
				deficit = 0
			}
			row = append(row, ResourceRequirement{Resource: choice.Resource, Required: choice.Count, Deficit: deficit})
		}
		result = append(result, row)
	}
	return result, nil
}

// MiningProgress mirrors one designated mineable source's native hit points;
// a decrease since the prior observation is excavation progress.
type MiningProgress struct {
	ThingID   string
	HitPoints int64
}

// DrillingProgress mirrors one owned native drill's progress fraction; an
// increase since the prior observation is drilling progress.
type DrillingProgress struct {
	ThingID  string
	Progress float64
}

// ResourceExtractionAdvanced reports whether excavation (a designated mine's
// hit points decreasing) or drilling (an owned drill's progress increasing)
// has advanced since the previously observed tick, mirroring
// production_policy.py's observe_mining_progress. A tick at or before the
// last observed progress can never itself establish an advance, matching the
// Python guard against replaying stale native reads as new progress.
func ResourceExtractionAdvanced(tick, lastProgressTick domain.Tick, mining, priorMining []MiningProgress, drilling, priorDrilling []DrillingProgress) bool {
	if tick < lastProgressTick {
		return false
	}
	priorHitPoints := make(map[string]int64, len(priorMining))
	for _, p := range priorMining {
		priorHitPoints[p.ThingID] = p.HitPoints
	}
	for _, m := range mining {
		if prev, ok := priorHitPoints[m.ThingID]; ok && m.HitPoints < prev {
			return true
		}
	}
	priorDrillProgress := make(map[string]float64, len(priorDrilling))
	for _, p := range priorDrilling {
		priorDrillProgress[p.ThingID] = p.Progress
	}
	for _, d := range drilling {
		if prev, ok := priorDrillProgress[d.ThingID]; ok && d.Progress > prev {
			return true
		}
	}
	return false
}
