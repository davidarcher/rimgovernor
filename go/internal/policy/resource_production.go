package policy

import (
	"errors"
	"sort"

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

// ResourceSourceMethod names how one native resource source is acquired.
// "mine" sources need excavation and are subject to the one-per-selection
// and safety rules below; any other value (harvest, haul, etc.) is treated
// as an ordinary, freely combinable source.
type ResourceSourceMethod string

const ResourceSourceMine ResourceSourceMethod = "mine"

// ResourceSource mirrors one native home/resource_sources row.
type ResourceSource struct {
	ThingID    string
	Yield      int64
	Distance   float64
	Method     ResourceSourceMethod
	Designated bool
	// Safety gates a "mine" source only: an older companion cannot certify
	// excavation geometry, so a mine source is usable only when native
	// reports "open_surface".
	Safety    string
	WorkTypes []WorkType
}

// SelectResourceSources chooses, nearest first, the undesignated sources
// whose combined yield covers the outstanding deficit (target minus current
// stock minus already-pending acquisition), mirroring
// production_policy.py's resource_method acquisition loop. At most one
// "mine" source is ever selected per call — one excavation identity per
// method preserves cancellation across a changing stock target without
// retaining an unbounded second source ledger — and it is the last source
// selected. A "mine" source lacking native "open_surface" safety
// confirmation can never be selected. The result is capped at 8 sources,
// matching the native selection this ports.
func SelectResourceSources(sources []ResourceSource, target, stock, pending int64) []ResourceSource {
	needed := target - stock - pending
	if needed <= 0 {
		return nil
	}
	usable := make([]ResourceSource, 0, len(sources))
	for _, s := range sources {
		if s.Method == ResourceSourceMine && s.Safety != "open_surface" {
			continue
		}
		usable = append(usable, s)
	}
	sort.SliceStable(usable, func(i, j int) bool {
		if usable[i].Distance != usable[j].Distance {
			return usable[i].Distance < usable[j].Distance
		}
		return usable[i].ThingID < usable[j].ThingID
	})
	var selected []ResourceSource
	for _, s := range usable {
		if needed <= 0 || len(selected) == 8 {
			break
		}
		if s.Designated || s.Yield <= 0 {
			continue
		}
		if s.Method == ResourceSourceMine && len(selected) > 0 {
			break
		}
		selected = append(selected, s)
		needed -= s.Yield
		if s.Method == ResourceSourceMine {
			break
		}
	}
	return selected
}
