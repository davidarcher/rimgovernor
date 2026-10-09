package policy

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainResource-* pure primitives:
// ingredient deficits, mining progress, resource method,
// and stock selection. The dispatch vertical built on top of these
// (buildingruntime.RoundsResourcePlanner) is a single
// config-only policy.MaintainResource goal, mirroring EnsureResearch's
// posture, whose method is a generic bench/recipe StockTarget production
// bill exactly like GearProduce/MaintainMedicalReserves dispatch through.
// Native mining-source acquisition (SelectResourceSources below),
// and material-storage zoning remain unwired to any native call — see
// SelectResourceTarget and SelectResourceMethod's own doc comments.

// ResourceRequirement is one native recipe-ingredient alternative's exact
// required quantity and the deficit against current stock.
type ResourceRequirement struct {
	Resource Resource
	Required int64
	Deficit  int64
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

// ResourceSourceMethod names how one native resource source is acquired.
// "mine" sources need excavation and are subject to the one-per-selection
// and safety rules below; any other value (harvest, haul, etc.) is treated
// as an ordinary, freely combinable source.
type ResourceSourceMethod string

const ResourceSourceMine ResourceSourceMethod = "mine"

// ResourceSource mirrors one Observations.ListResourceSources row.
type ResourceSource struct {
	ThingID    string
	Yield      int64
	Distance   float64
	Method     ResourceSourceMethod
	Designated bool
	// DesignatedTick and Taken mirror AcquisitionSource's; mine
	// rows only.
	DesignatedTick domain.Tick
	Taken          bool
	// Safety gates a "mine" source only: an older companion cannot certify
	// excavation geometry, so a mine source is usable only when native
	// reports a MineSafe safety.
	Safety    string
	WorkTypes []WorkType
	// Cell and Token are populated for a "mine" source only (see
	// NativeResourceSourcesTool.Project / NativeMineAcquisition.Snapshot on
	// the native side): the exact position and CAS snapshot token required
	// to dispatch an acquisition Designate against it. Harvest/hunt
	// sources still carry neither -- they are reached only through the
	// AcquisitionFacts census path's own token, not this one.
	Cell      domain.Cell
	Token     string
	Reachable domain.Fact[bool]
	// Buried marks a supported "mine" deposit no colonist can reach yet:
	// it is never mined directly, only tunnelled to by a
	// corridor excavation (CorridorExcavationSites).
	Buried bool
	// Tier is the layout plan's MineTier for a mine source (0 without a
	// plan); lower tiers are selected first.
	Tier int
}

// BuriedResourceSource is the deposit MaintainResource tunnels to
// when no mine source can be selected directly: the nearest undesignated,
// MineSafe, buried mine row with yield left whose cell the resource reach
// would let a miner work once the corridor opens it. Nothing is
// returned while a known threat or urgent competing work would hold any
// mining.
func BuriedResourceSource(sources []ResourceSource, r RemoteWorkRequest) (ResourceSource, bool) {
	if remoteThreatHold(r.Reach) != "" || r.Competition.UrgentPriority > 0 {
		return ResourceSource{}, false
	}
	var best ResourceSource
	found := false
	for _, s := range sources {
		if s.Method != ResourceSourceMine || !s.Buried || s.Designated || s.Yield <= 0 || !MineSafe(s.Safety) {
			continue
		}
		// The tunnel supplies the route; the deposit itself must lie in reach.
		if !FilterResourceReach(r.Reach, ResourceReachCandidate{Cell: s.Cell, Eligible: domain.Known(true), RouteObservedPassable: domain.Known(true)}).Allowed {
			continue
		}
		if !found || s.Distance < best.Distance || s.Distance == best.Distance && s.ThingID < best.ThingID {
			best, found = s, true
		}
	}
	return best, found
}

// SelectReachableResourceSources narrows mining to the current resource reach.
// Existing designations reserve estimated yield but are never adopted or removed.
// Each new method still contains at most one rock; its final native yield may
// overshoot the remaining demand by one rock's output. Every mine source kept
// back is returned as a hold with its explicit reason (RemoteHoldReason): a
// known threat or urgent competing work holds every deposit, then the
// open-surface roof check, the observed route and the reach stage.
func SelectReachableResourceSources(sources []ResourceSource, target, stock int64, r RemoteWorkRequest) ([]ResourceSource, []RemoteWorkHold) {
	var pending int64
	var candidates []ResourceSource
	var holds []RemoteWorkHold
	for _, source := range sources {
		if source.Designated && source.Yield > 0 {
			pending += source.Yield
			continue
		}
		if source.Method == ResourceSourceMine {
			reason := remoteThreatHold(r.Reach)
			if reason == "" && r.Competition.UrgentPriority > 0 {
				reason = RemoteHoldUrgentWork
			}
			if reason == "" && source.Buried {
				// Native confirmed its support; it waits on a corridor.
				reason = RemoteHoldBuried
			}
			if reason == "" {
				decision := FilterResourceReach(r.Reach, ResourceReachCandidate{Cell: source.Cell,
					Eligible: domain.Known(MineSafe(source.Safety) && !source.Buried), RouteObservedPassable: source.Reachable})
				if !decision.Allowed {
					reason = RemoteHoldReason(RemoteMining, decision.Reason)
				}
			}
			if reason != "" {
				holds = append(holds, RemoteWorkHold{Kind: RemoteMining, Target: source.ThingID, Reason: reason})
				continue
			}
			if plan, ok := r.Plan.Value(); ok {
				source.Tier = plan.MineTier(source.Cell)
			}
		}
		candidates = append(candidates, source)
	}
	return SelectResourceSources(candidates, target, stock, pending), holds
}

// SelectResourceSources chooses, nearest first, the undesignated sources
// whose combined yield covers the outstanding deficit (target minus current
// stock minus already-pending acquisition). At most one
// "mine" source is ever selected per call — one excavation identity per
// method preserves cancellation across a changing stock target without
// retaining an unbounded second source ledger — and it is the last source
// selected. A "mine" source lacking native mine safety confirmation
// (MineSafe) can never be selected. The result is capped at 8 sources,
// matching the native selection this ports.
func SelectResourceSources(sources []ResourceSource, target, stock, pending int64) []ResourceSource {
	needed := target - stock - pending
	if needed <= 0 {
		return nil
	}
	usable := make([]ResourceSource, 0, len(sources))
	for _, s := range sources {
		if s.Method == ResourceSourceMine && (!MineSafe(s.Safety) || s.Buried) {
			continue
		}
		usable = append(usable, s)
	}
	sort.SliceStable(usable, func(i, j int) bool {
		if usable[i].Tier != usable[j].Tier {
			return usable[i].Tier < usable[j].Tier
		}
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

// ResourceStorage mirrors one Observations.ListResourceSources storage payload
// (NativeResourceSourcesTool.Storage): the storage branch keys off
// exactly these fields (haulers/capacity/stackLimit/candidates) to decide
// whether hauling a selected mine source's yield needs a new covered
// stockpile zone. Candidates are native's own hauler-reachable, roofed,
// unreserved cell scan near an existing hauler -- not recomputed by
// a generic covered-site search, which this narrower,
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
// storage check sizes (SelectStockpileCapacity; no zone is built).
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
	if pending < 0 || storage.Capacity < 0 || storage.Stored < 0 || storage.StackLimit < 0 || storage.Haulers < 0 {
		return ResourceStorageZone{}, false, false, errors.New("invalid resource storage observation")
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
	return SelectStockpileCapacity(yield+pending, storage)
}

// SelectStockpileCapacity uses native covered, reachable storage candidates.
func SelectStockpileCapacity(capacityNeeded int64, storage ResourceStorage) (zone ResourceStorageZone, needed, blocked bool, err error) {
	if capacityNeeded < 0 || storage.Capacity < 0 || storage.Stored < 0 || storage.StackLimit < 0 || storage.Haulers < 0 {
		return zone, false, false, errors.New("invalid stockpile capacity")
	}
	if storage.Haulers == 0 {
		return ResourceStorageZone{}, false, true, nil
	}
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

// ResourceTarget is one configured stock floor still unmet by the census:
// the resource and its absolute target.
type ResourceTarget struct {
	Resource Resource
	Target   int64
}

// SelectResourceTarget performs MaintainResource's dynamic-target selection:
// given every operator-configured resource target (RoundsPolicy's future
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
	ranked, err := RankResourceTargets(targets, stock)
	if err != nil || len(ranked) == 0 {
		return "", 0, false, err
	}
	return ranked[0].Resource, ranked[0].Target, true, nil
}

// RankResourceTargets is SelectResourceTarget's full order: every unmet
// configured floor, worst-covered first (proportional deficit descending,
// definition name ascending on a tie), so a caller whose first choice has no
// dispatchable method can go on to the next demanded resource in the same
// step instead of starving it behind one it cannot act on. Empty when
// stock is unknown, nothing is configured, or every floor is met.
func RankResourceTargets(targets map[Resource]int64, stock domain.Fact[[]Amount]) ([]ResourceTarget, error) {
	if len(targets) == 0 {
		return nil, nil
	}
	names := make([]Resource, 0, len(targets))
	for name, want := range targets {
		if !validResource(name) || want <= 0 {
			return nil, errors.New("invalid resource target")
		}
		names = append(names, name)
	}
	sort.Slice(names, func(i, j int) bool { return names[i] < names[j] })
	rows, known := stock.Value()
	if !known {
		return nil, nil
	}
	have := map[Resource]int64{}
	for _, q := range rows {
		if !validResource(q.Resource) || q.Count < 0 {
			return nil, errors.New("invalid resource stock")
		}
		if _, exists := have[q.Resource]; exists {
			return nil, errors.New("duplicate resource stock")
		}
		have[q.Resource] = q.Count
	}
	type ranked struct {
		ResourceTarget
		ratio float64
	}
	var unmet []ranked
	for _, name := range names {
		want := targets[name]
		deficit := want - have[name]
		if deficit <= 0 {
			continue
		}
		unmet = append(unmet, ranked{ResourceTarget{name, want}, float64(deficit) / float64(want)})
	}
	sort.SliceStable(unmet, func(i, j int) bool { return unmet[i].ratio > unmet[j].ratio })
	out := make([]ResourceTarget, 0, len(unmet))
	for _, row := range unmet {
		out = append(out, row.ResourceTarget)
	}
	return out, nil
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
	Replace       string
	Resource      Resource
	Target        int64
	// Ingredients are the recipe's ingredients for one craft, the slots with
	// a single alternative (a slot that may be filled several ways has no
	// known draw).
	Ingredients []Amount
}

// ResourceMethodRequest names the one dynamically-selected resource and
// target SelectResourceMethod should fund a bill for, plus the same generic
// bench/recipe census GearProduce/MaintainMedicalReserves already read
// (policy.GearBench/GearRecipe via bridge.ReadGearBenches/ReadSupplyStock).
// Like MedicinePlanningRequest, no Safeguards or Holds are threaded through yet —
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
	if !validResource(r.Resource) || r.Target <= 0 || r.Target > math.MaxInt32 {
		return ResourceMethod{Kind: ResourceMethodUnknown}, nil
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
	if err := validateGearProduction(benches); err != nil {
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
			id := resourceMethodID(r.Resource, b.ID, recipe.Definition)
			if seen[id] {
				return ResourceMethod{Kind: ResourceMethodWait, ID: id}, nil
			}
			// Reuse the native replacement operation for an inactive matching
			// bill; adding beside it is refused by the native duplicate guard.
			var replace string
			bills, _ := b.Bills.Value()
			for _, bill := range bills {
				if bill.Recipe != recipe.Definition {
					continue
				}
				active, known := bill.Active.Value()
				if !known || !foodID(bill.ID) {
					return ResourceMethod{Kind: ResourceMethodUnknown}, nil
				}
				if !active {
					replace = bill.ID
					break
				}
			}
			return ResourceMethod{Kind: ResourceMethodProduce, ID: id, Bench: b.ID, Recipe: recipe.Definition, Replace: replace, Resource: r.Resource, Target: r.Target, Ingredients: recipeDraws(recipe)}, nil
		}
	}
	return ResourceMethod{Kind: ResourceMethodBlocked}, nil
}

// Native mine-source safety verdicts that permit excavation: open ground,
// or a roofed deposit whose removal keeps the roof supported.
const (
	MineSafetyOpenSurface   = "open_surface"
	MineSafetySupportedRoof = "supported_roof"
)

// MineSafe reports whether a native safety verdict lets a "mine" source be
// counted and selected.
func MineSafe(safety string) bool {
	return safety == MineSafetyOpenSurface || safety == MineSafetySupportedRoof
}

// recipeDraws is what one craft of the recipe consumes from the catalog: each
// slot with a single alternative. Unknown ingredients declare nothing.
func recipeDraws(recipe GearRecipe) []Amount {
	slots, known := recipe.Ingredients.Value()
	if !known {
		return nil
	}
	var out []Amount
	for _, slot := range slots {
		if len(slot) == 1 && slot[0].Count > 0 {
			out = append(out, slot[0])
		}
	}
	return out
}
