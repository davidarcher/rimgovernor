package policy

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainResource-* pure primitives:
// ingredient deficits, mining progress, resource method,
// production budgets. The dispatch vertical built on top of these
// (buildingruntime.RoutineResourcePlanner) is a single
// config-only policy.MaintainResource goal, mirroring EnsureResearch's
// posture, whose method is a generic bench/recipe StockTarget production
// bill exactly like GearProduce/MaintainMedicalReserves dispatch through.
// Native mining-source acquisition (SelectResourceSources below),
// material-storage zoning and the native SetProductionPolicy floors/
// commitments push (ProductionFloors below) remain entirely unwired to any
// native call — see SelectResourceTarget, SelectResourceMethod and
// ProductionFloors's own doc comments for what is and is not covered.

// ResourceRequirement is one native recipe-ingredient alternative's exact
// required quantity and the deficit against current stock.
type ResourceRequirement struct {
	Resource Resource
	Required int64
	Deficit  int64
}

// ResourceRecipeDeficits computes, per ingredient slot alternative, the exact
// native-required amount and the deficit against current stock. Unknown per-alternative native
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
// has advanced since the previously observed tick. A tick at or before the
// last observed progress can never itself establish an advance, which guards
// against replaying stale native reads as new progress.
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

// ProductionFloors is the reserve half of the production budget only: given
// RoutinePolicy's operator-declared per-resource reserve floors and
// stopped-spending set, it returns the exact floors map (zero reserves
// omitted) and the stopped definitions sorted for deterministic dispatch. A
// second source of floors -- outstanding, unconfirmed construction-bundle
// ingredient costs -- has no Go equivalent yet; that half depends on the still-unported
// multi-step staged-bundle admission model MaintainStoneShell already needs
// dedicated design work for, so it is not attempted here. This is a pure
// primitive: nothing yet calls it, pending the native SetProductionPolicy
// operation category, which this round's investigation found is not just
// missing Go wiring but has no native Execute/Preview handler at all
// (contracts/proto/operations.proto's SetProductionPolicy message and
// observations.proto's ReadProductionPolicy RPC are both fully unimplemented
// on the native side).
func ProductionFloors(reserves map[Resource]int64, stopped []Resource) (map[Resource]int64, []Resource, error) {
	if len(reserves) > 4096 || len(stopped) > 4096 {
		return nil, nil, errors.New("production policy input exceeds bound")
	}
	floors := map[Resource]int64{}
	for resource, reserve := range reserves {
		if !validResource(resource) || reserve < 0 || reserve > 10000 {
			return nil, nil, errors.New("invalid resource reserve")
		}
		if reserve != 0 {
			floors[resource] = reserve
		}
	}
	seen := map[Resource]bool{}
	stoppedOut := make([]Resource, 0, len(stopped))
	for _, resource := range stopped {
		if !validResource(resource) || seen[resource] {
			return nil, nil, errors.New("invalid or duplicate stopped resource")
		}
		seen[resource] = true
		stoppedOut = append(stoppedOut, resource)
	}
	sort.Slice(stoppedOut, func(i, j int) bool { return stoppedOut[i] < stoppedOut[j] })
	return floors, stoppedOut, nil
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
	// Cell and Token are populated for a "mine" source only (see
	// NativeResourceSourcesTool.Project / NativeMineAcquisition.Snapshot on
	// the native side): the exact position and CAS snapshot token required
	// to dispatch an AcquireResource operation against it. Harvest/hunt
	// sources still carry neither -- they are reached only through the
	// AcquisitionFacts census path's own token, not this one.
	Cell  domain.Cell
	Token string
}

// SelectResourceSources chooses, nearest first, the undesignated sources
// whose combined yield covers the outstanding deficit (target minus current
// stock minus already-pending acquisition). At most one
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

// ResourceStorage mirrors one native home/resource_sources "storage" payload
// (NativeResourceSourcesTool.Storage): the storage branch keys off
// exactly these fields (haulers/capacity/stackLimit/candidates) to decide
// whether hauling a selected mine source's yield needs a new covered
// stockpile zone. Candidates are native's own hauler-reachable, roofed,
// unreserved cell scan near an existing hauler -- not recomputed by
// policy.CoveredStorageSites, the generic site-search this narrower,
// deposit-adjacent placement does not need.
type ResourceStorage struct {
	Resource   Resource
	Capacity   int64
	Stored     int64
	StackLimit int64
	// Haulers is the count of eligible haulers native found (only its
	// emptiness is checked); the haulers'
	// own identities are not threaded further since nothing here dispatches
	// hauling jobs directly.
	Haulers    int64
	Candidates []domain.Cell
}

// ResourceStorageZone is the exact new allow-listed stockpile zone the
// material-storage fallback should build (the storage branch's create_zone
// operation).
type ResourceStorageZone struct {
	Cells []domain.Cell
}

// SelectResourceStorageZone decides whether hauling a selection of resource
// sources (from SelectResourceSources) needs a new covered stockpile zone,
// as follows: the check only ever applies when at least one selected source
// is a "mine" source (an ordinary harvest/hunt yield needs no dedicated
// storage zone). blocked is true when there is no route to funding storage at
// all this tick (no eligible hauler, or no free candidate cell covers the
// shortfall) -- a
// caller should treat this as a hard stop rather than silently proceeding as
// if storage were adequate. needed is true only when blocked is false and a
// new zone must be built; existing capacity already covering the deficit (or
// no mine source selected at all) reports needed=false, blocked=false so the
// caller's ordinary fallback speaks instead.
func SelectResourceStorageZone(selected []ResourceSource, pending int64, storage ResourceStorage) (zone ResourceStorageZone, needed, blocked bool, err error) {
	if len(selected) > 8 {
		return ResourceStorageZone{}, false, false, errors.New("resource selection exceeds bound")
	}
	if pending < 0 || storage.Capacity < 0 || storage.Stored < 0 || storage.StackLimit < 0 || storage.Haulers < 0 {
		return ResourceStorageZone{}, false, false, errors.New("invalid resource storage observation")
	}
	if len(storage.Candidates) > 4096 {
		return ResourceStorageZone{}, false, false, errors.New("resource storage candidate collection exceeds bound")
	}
	hasMine := false
	var yield int64
	for _, s := range selected {
		if s.Method == ResourceSourceMine {
			hasMine = true
		}
		yield += s.Yield
	}
	if !hasMine {
		return ResourceStorageZone{}, false, false, nil
	}
	if storage.Haulers == 0 {
		return ResourceStorageZone{}, false, true, nil
	}
	capacityNeeded := yield + pending
	if storage.Capacity >= capacityNeeded {
		return ResourceStorageZone{}, false, false, nil
	}
	if storage.StackLimit == 0 {
		return ResourceStorageZone{}, false, true, nil
	}
	shortfall := capacityNeeded - storage.Capacity
	cellsNeeded := (shortfall + storage.StackLimit - 1) / storage.StackLimit
	if cellsNeeded > int64(len(storage.Candidates)) {
		cellsNeeded = int64(len(storage.Candidates))
	}
	if cellsNeeded <= 0 {
		return ResourceStorageZone{}, false, true, nil
	}
	cells := append([]domain.Cell(nil), storage.Candidates[:cellsNeeded]...)
	return ResourceStorageZone{Cells: cells}, true, false, nil
}

// SelectResourceTarget performs MaintainResource's dynamic-target selection:
// given every operator-configured resource target (RoutinePolicy's future
// ResourceTargets, one native stock floor per definition) and a fresh native
// stock census, it picks the single resource whose stock is furthest below
// its own target (by proportion, so a small target is not starved behind a
// large one merely stuck a few units short), the same way one plan-wide
// review would attend to its worst-covered floor first. A configured
// resource absent from the census is treated as fully unstocked rather than
// refused — there is no decode of native policyResources yet to tell "unknown
// definition" apart from "currently zero", a disclosed narrowing matching
// the hardcoded-resource narrowing in
// buildingruntime.medicineResourceDefinition. ok is false when stock is not
// yet known, no resource is configured, or every configured resource already
// meets its target — there is nothing to dispatch a method for this tick.
func SelectResourceTarget(targets map[Resource]int64, stock domain.Fact[[]Amount]) (resource Resource, target int64, ok bool, err error) {
	if len(targets) == 0 {
		return "", 0, false, nil
	}
	if len(targets) > 4096 {
		return "", 0, false, errors.New("too many configured resource targets")
	}
	names := make([]Resource, 0, len(targets))
	for name, want := range targets {
		if !validResource(name) || want <= 0 || want > 10000 {
			return "", 0, false, errors.New("invalid resource target")
		}
		names = append(names, name)
	}
	sort.Slice(names, func(i, j int) bool { return names[i] < names[j] })
	rows, known := stock.Value()
	if !known {
		return "", 0, false, nil
	}
	if len(rows) > 4096 {
		return "", 0, false, errors.New("resource stock census exceeds bound")
	}
	have := map[Resource]int64{}
	for _, q := range rows {
		if !validResource(q.Resource) || q.Count < 0 {
			return "", 0, false, errors.New("invalid resource stock")
		}
		if _, exists := have[q.Resource]; exists {
			return "", 0, false, errors.New("duplicate resource stock")
		}
		have[q.Resource] = q.Count
	}
	bestRatio := -1.0
	for _, name := range names {
		want := targets[name]
		deficit := want - have[name]
		if deficit <= 0 {
			continue
		}
		ratio := float64(deficit) / float64(want)
		if ratio > bestRatio {
			bestRatio, resource, target = ratio, name, want
		}
	}
	return resource, target, resource != "", nil
}

// ResourceMethodKind names the shape of one proposed MaintainResource method.
type ResourceMethodKind string

const (
	ResourceMethodUnknown   ResourceMethodKind = "unknown"
	ResourceMethodRecovered ResourceMethodKind = "recovered"
	ResourceMethodProduce   ResourceMethodKind = "produce"
	ResourceMethodWait      ResourceMethodKind = "wait_for_existing_work"
	ResourceMethodBlocked   ResourceMethodKind = "no_eligible_method"
)

// ResourceMethod is one proposed StockTarget production bill for the
// resource SelectResourceTarget chose this tick.
type ResourceMethod struct {
	Kind          ResourceMethodKind
	ID            domain.MethodID
	Bench, Recipe string
	Resource      Resource
	Target        int64
}

// ResourceMethodRequest names the one dynamically-selected resource and
// target SelectResourceMethod should fund a bill for, plus the same generic
// bench/recipe census GearProduce/MaintainMedicalReserves already read
// (policy.GearBench/GearRecipe via bridge.ReadGearBenches/ReadSupplyStock).
// Like MedicinePlanningRequest, no Rules or Holds are threaded through yet —
// the same disclosed no-cross-goal-ingredient-reservation gap GearProduce
// and MaintainMedicalReserves already carry applies here too.
type ResourceMethodRequest struct {
	Resource Resource
	Target   int64
	Seen     []domain.MethodID
	Benches  domain.Fact[[]GearBench]
	Stock    []Stock
}

func resourceMethodID(resource Resource, bench, recipe string) domain.MethodID {
	value := struct {
		Resource      Resource
		Bench, Recipe string
	}{resource, bench, recipe}
	sum := sha256.Sum256([]byte(fmt.Sprintf("%v", value)))
	return domain.MethodID(fmt.Sprintf("resource-produce-%x", sum[:16]))
}

// SelectResourceMethod proposes one repeat-count StockTarget bill that keeps
// at least Target units of Resource in stock, mirroring SelectMedicineMethod
// exactly but over whichever resource SelectResourceTarget dynamically chose
// rather than one hardcoded definition. It covers only the bench/recipe
// production path of the resource method — the native
// mine/harvest source-acquisition branch (SelectResourceSources above) and
// the extraction-development branch are not dispatched from here; a resource
// with no producing recipe and no covering bill is simply Blocked, matching
// this narrowing rather than falling back to those undispatched paths. It
// issues no game orders and does not reserve resources.
func SelectResourceMethod(r ResourceMethodRequest) (ResourceMethod, error) {
	if !validResource(r.Resource) || r.Target <= 0 || r.Target > 10000 {
		return ResourceMethod{Kind: ResourceMethodUnknown}, nil
	}
	if len(r.Seen) > 4096 {
		return ResourceMethod{}, errors.New("resource method history exceeds bound")
	}
	seen := map[domain.MethodID]bool{}
	for _, id := range r.Seen {
		if !foodID(string(id)) || seen[id] {
			return ResourceMethod{}, errors.New("invalid resource method history")
		}
		seen[id] = true
	}
	benches, known := r.Benches.Value()
	if !known {
		return ResourceMethod{Kind: ResourceMethodUnknown}, nil
	}
	gearRequest := GearPlanningRequest{Stock: r.Stock}
	if err := validateGearProduction(benches, gearRequest); err != nil {
		return ResourceMethod{}, err
	}
	benches = append([]GearBench(nil), benches...)
	sort.Slice(benches, func(i, j int) bool { return benches[i].ID < benches[j].ID })
	for _, b := range benches {
		bills, known := b.Bills.Value()
		if !known {
			return ResourceMethod{Kind: ResourceMethodUnknown}, nil
		}
		for _, bill := range bills {
			if !containsResource(bill.Products, r.Resource) {
				continue
			}
			active, known := bill.Active.Value()
			if !known {
				return ResourceMethod{Kind: ResourceMethodUnknown}, nil
			}
			if active {
				return ResourceMethod{Kind: ResourceMethodWait}, nil
			}
		}
	}
	for _, b := range benches {
		recipes, known := b.Recipes.Value()
		if !known {
			return ResourceMethod{Kind: ResourceMethodUnknown}, nil
		}
		recipes = append([]GearRecipe(nil), recipes...)
		sort.Slice(recipes, func(i, j int) bool { return recipes[i].Definition < recipes[j].Definition })
		for _, recipe := range recipes {
			if !containsResource(recipe.Products, r.Resource) {
				continue
			}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			if ak && !available || ok && !on {
				continue
			}
			if !ak || !ok {
				return ResourceMethod{Kind: ResourceMethodUnknown}, nil
			}
			slots, known := recipe.Ingredients.Value()
			if !known {
				return ResourceMethod{Kind: ResourceMethodUnknown}, nil
			}
			_, _, funded, unknown := gearIngredients(slots, "", gearRequest)
			if unknown {
				return ResourceMethod{Kind: ResourceMethodUnknown}, nil
			}
			if !funded {
				continue
			}
			id := resourceMethodID(r.Resource, b.ID, recipe.Definition)
			if seen[id] {
				return ResourceMethod{Kind: ResourceMethodWait, ID: id}, nil
			}
			return ResourceMethod{Kind: ResourceMethodProduce, ID: id, Bench: b.ID, Recipe: recipe.Definition, Resource: r.Resource, Target: r.Target}, nil
		}
	}
	return ResourceMethod{Kind: ResourceMethodBlocked}, nil
}
