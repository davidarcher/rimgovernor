package buildingruntime

// Food matrix (epic #2140, child 3): the REAL reviewFoodPlan driven day by day
// through the supplysim world. Each simulated day the adapter builds the
// observation.ColonyProjection fields reviewFoodPlan reads (Workers,
// CombinedFoodSupply, FoodSupply, Acquisition, FoodChannels, FoodFields,
// Facts.Calendar) from the world, calls reviewFoodPlan and applies its Open and
// Close rows as source commands. No projection field needed a fallback to
// policy.SupplyFoodPlan plus the channel builders. Only what the real planner can see
// reaches it:
//
//   - Trade has no plan row, so a trade source is never opened.
//   - AnimalProduct rows are never acted on: animals keep producing.
//   - A planned Open row reopens daily (a no-op for an open source). The
//     adapter feeds the planner the delivery ledger the source would have
//     counted (cumulative nutrition per counter group), so a row is Open once
//     it has delivered and its credit follows what it delivers.
//   - A hidden source (a hunt the native gates refuse: no butcher bill, no
//     ranged hunter, Hunting priority 0) never reaches Acquisition.
//
// The assertions are direction and ordering, never exact ticks. Failures that
// today's planner has are recorded in testdata/food-matrix-baseline.json
// (scenario@horizon -> failed assertions). The test fails on a failure that is
// not baselined and on a baselined failure that now passes, so the set only
// shrinks: delete the entry (or run -update-food-baseline, which refuses to
// grow the file) when a change fixes one.

import (
	"encoding/json"
	"flag"
	"fmt"
	"maps"
	"math"
	"os"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/supplysim"
)

var updateFoodBaseline = flag.Bool("update-food-baseline", false, "rewrite testdata/food-matrix-baseline.json for the horizons run; it may only remove entries once a horizon is recorded")

const (
	foodBaselinePath = "testdata/food-matrix-baseline.json"
	// foodNutritionPerColonist is the planning draw per colonist per day.
	foodNutritionPerColonist = 1.6
	// foodViabilityMargin is the capacity margin a colony needs before the
	// planner is required not to starve it: the oracle must survive demand x 1.2.
	foodViabilityMargin = 1.2
	// foodRunwayTolerance is the days a larger mix's minimum runway may fall
	// short of its smaller predecessor's before the ordering counts as broken.
	foodRunwayTolerance = 0.5
	foodShockDay        = 12
	foodShortHorizon    = 30
	foodLongHorizon     = 90
)

// srcSpec is one world source with what the adapter needs to present it to
// the planner.
type srcSpec struct {
	kind   policy.CandidateKind
	src    supplysim.Source
	nutr   float64 // nutrition per source unit
	cells  float64 // crop field cells
	grow   int     // crop grow days
	window supplysim.Window
	hidden bool    // kept out of the projection by native gates
	reach  float64 // the longest weapon reach among the hunters who qualify
}

type foodScenario struct {
	name      string
	prev      string // the smaller mix this one extends ("" for none)
	twin      string // the unshocked twin of a shock scenario
	colonists int
	perDay    float64 // total nutrition demand per day
	stock     float64
	silver    float64
	specs     []srcSpec
	shocks    []supplysim.Shock
	// kitchen gives the colony a usable cook bench and a cook. The world has no
	// cooking: the recipe converts nutrition one for one, so a bench must not
	// make the planner worse.
	kitchen bool
	// shockedIDs are the sources a shock touches.
	shockedIDs []string
	// overestimates are the shocked sources whose planned rate the shock makes
	// false (they deliver far less than the plan expects): the delivery credit
	// must take their factor to ~0.
	overestimates []string
}

func (sc foodScenario) world(demandScale float64) supplysim.World {
	w := supplysim.World{Seed: 1, Workers: sc.colonists, Stock: map[supplysim.Good]float64{supplysim.Nutrition: sc.stock},
		Consumers: []supplysim.Consumer{{Name: "colony", Good: supplysim.Nutrition, PerDay: sc.perDay * demandScale}},
		Shocks:    sc.shocks}
	if sc.silver > 0 {
		w.Stock[supplysim.Silver] = sc.silver
	}
	for _, s := range sc.specs {
		src := s.src
		// Products are produced by animals the colony already keeps; every other source starts closed.
		src.Open = s.kind == policy.CandidateAnimalProduct
		w.Sources = append(w.Sources, src)
	}
	return w
}

func (sc foodScenario) spec(id string) *srcSpec {
	for i := range sc.specs {
		if sc.specs[i].src.ID == id {
			return &sc.specs[i]
		}
	}
	return nil
}

// foodDay is what the planner decided on one day.
type foodDay struct {
	known       bool
	gap         float64
	runway      float64
	opened      []string // sources newly opened
	closed      []string // sources closed for surplus
	closedAtGap bool
}

// foodAdapter is a supplysim.Planner over the real reviewFoodPlan.
type foodAdapter struct {
	sc    foodScenario
	days  []foodDay
	plans []policy.FoodPlan
	// credit is the planner's delivery credit; delivered is the cumulative
	// nutrition each ledger counter has seen (the sim's native ledger).
	credit    policy.DeliveryCredit
	delivered map[observation.DeliveryKey]float64
	// groups names the world source behind each ledger counter group; minFactor
	// is the lowest factor the credit reported for a source (0 once the plan
	// carries no rate for a fishing region after the shock).
	groups    map[string]string
	minFactor map[string]float64
}

func (a *foodAdapter) Plan(v supplysim.WorldView) []supplysim.Command {
	p, ids := a.projection(v)
	plan, known := reviewFoodPlan(p, policy.DefaultRoundsPolicy(), &a.credit, nil, foodTrade{}).Value()
	for _, c := range a.credit.Drain() {
		if id, ok := a.groups[c.Source]; ok {
			if f, seen := a.minFactor[id]; !seen || c.Factor < f {
				a.minFactor[id] = c.Factor
			}
		}
	}
	a.plans = append(a.plans, plan)
	d := foodDay{known: known, gap: plan.GapPerDay, runway: v.Runway[supplysim.Nutrition]}
	if !known {
		a.days = append(a.days, d)
		return nil
	}
	open := map[string]bool{}
	for _, s := range v.Sources {
		open[s.ID] = s.Open
	}
	var cmds []supplysim.Command
	for _, e := range plan.Portfolio {
		id, ok := ids[string(e.Channel.Kind)+"/"+e.Channel.ID]
		if !ok {
			continue // a supporting row
		}
		// A fishing region below its population floor carries no rate: the plan
		// no longer counts on it, which the credit never needs to judge (expected 0).
		if rate, known := e.Channel.Nutrition().PerDay.Value(); e.Channel.Kind == policy.CandidateFishing && known && rate == 0 && v.Day >= foodShockDay {
			a.minFactor[id] = 0
		}
		switch e.Decision {
		case policy.FoodPlanOpen:
			cmds = append(cmds, supplysim.Command{Kind: supplysim.Open, Source: id})
			if !open[id] {
				d.opened = append(d.opened, id)
			}
		case policy.FoodPlanClose:
			if e.Channel.Kind == policy.CandidateAnimalProduct {
				continue // animals keep producing; the planner cannot stop them
			}
			cmds = append(cmds, supplysim.Command{Kind: supplysim.Close, Source: id})
			d.closed = append(d.closed, id)
			if plan.GapPerDay > 0 || v.Runway[supplysim.Nutrition] < policy.DefaultRoundsPolicy().FoodMinDays {
				d.closedAtGap = true
			}
		}
	}
	a.days = append(a.days, d)
	return cmds
}

// projection builds the colony reading for the day and the map from the
// planner's "Kind/ID" row to the world source it stands for.
func (a *foodAdapter) projection(v supplysim.WorldView) (observation.ColonyProjection, map[string]string) {
	sc := a.sc
	views := map[string]supplysim.SourceView{}
	for _, s := range v.Sources {
		views[s.ID] = s
	}
	var pawns []policy.PawnID
	var consumers []policy.FoodConsumer
	for i := 0; i < sc.colonists; i++ {
		id := policy.PawnID(fmt.Sprintf("colonist-%d", i))
		pawns = append(pawns, id)
		consumers = append(consumers, policy.FoodConsumer{ID: id, NutritionPerDay: domain.Known(v.Demand[supplysim.Nutrition] / float64(sc.colonists))})
	}
	supply := policy.FoodSupply{Complete: domain.Known(true), Consumers: consumers}
	if stock := v.Stock[supplysim.Nutrition]; stock > 0 {
		supply.Stocks = []policy.FoodStock{{ID: "larder", Nutrition: domain.Known(stock), Holder: domain.Known(policy.PawnID("")),
			Perishable: domain.Known(false), Eaters: pawns}}
	}
	p := observation.ColonyProjection{Workers: domain.Known(sc.colonists), CombinedFoodSupply: domain.Known(supply), FoodSupply: domain.Known(supply)}
	p.Identity.Tick = v.Tick
	ledger := observation.DeliveryLedger{LoadToken: "sim", Counts: map[observation.DeliveryKey]observation.DeliveryCount{}}
	if a.delivered == nil {
		a.delivered = map[observation.DeliveryKey]float64{}
		a.groups, a.minFactor = map[string]string{}, map[string]float64{}
	}
	// count adds the units a source delivered the previous day to its ledger counter.
	count := func(key observation.DeliveryKey, sv supplysim.SourceView, nutr float64) {
		a.delivered[key] += sv.Last * nutr
		if n := a.delivered[key]; n > 0 {
			ledger.Counts[key] = observation.DeliveryCount{Nutrition: n, LastTick: int64(v.Tick)}
		}
	}
	if sc.kitchen {
		meal := policy.ProductionRecipe{Name: "CookMealSimple", Role: domain.RoleOrdinaryMeal, Available: domain.Known(true),
			NutrientEfficiency: domain.Known(1.0), WorkPerNutrition: domain.Known(100.0),
			IngredientClasses: domain.Known([]policy.FoodIngredientSlot{{Alternatives: []policy.FoodIngredientClass{policy.IngredientVegetable}}})}
		p.ProductionBenches = domain.Known([]policy.ProductionBench{{ID: "stove", Usable: domain.Known(true), Recipes: []policy.ProductionRecipe{meal}}})
	}
	ids := map[string]string{}
	var acquisition []policy.AcquisitionSource
	var fields []policy.FoodField
	var water observation.FishableWater
	water.FishingResearched = domain.Known(true)
	var gatherable []observation.GatherableAnimal
	var kept []policy.UpkeepAnimal
	var window *supplysim.Window
	for _, s := range sc.specs {
		sv, id := views[s.src.ID], s.src.ID
		if sv.Removed || s.hidden {
			continue
		}
		switch s.kind {
		case policy.CandidateForage, policy.CandidateHunt:
			if sv.Rate <= 0 {
				continue
			}
			hunt := s.kind == policy.CandidateHunt
			acquisition = append(acquisition, policy.AcquisitionSource{ID: id, Definition: id, Food: true, Hunt: hunt, NutritionYield: sv.Rate * s.nutr, WeaponRange: s.reach, Designated: sv.Open})
			if !hunt {
				count(observation.DeliveryKey{Kind: observation.DeliveryForage, SourceID: id, Def: id}, sv, s.nutr)
				a.groups["forage:"+id] = id
			}
			ids[string(s.kind)+"/"+id] = id
		case policy.CandidateFishing:
			regionID := policy.FishingRegionID(domain.Cell{X: int32(len(water.Regions))})
			// A fishing zone is designated while the source is open; whether it
			// delivers (above its population floor) is for the ledger to say.
			root := domain.Cell{X: int32(len(water.Regions))}
			count(observation.DeliveryKey{Kind: observation.DeliveryFish, SourceID: fmt.Sprintf("%d,%d", root.X, root.Z), Def: "fish"}, sv, s.nutr)
			a.groups[fmt.Sprintf("fish:%d,%d", root.X, root.Z)] = id
			water.Regions = append(water.Regions, observation.FishableRegion{Root: domain.Cell{X: int32(len(water.Regions))},
				Population: domain.Known(sv.Stock), MaxPopulation: domain.Known(sv.Max), Reachable: domain.Known(true), Frozen: domain.Known(false),
				Delivering: domain.Known(sv.Open), NutritionPerFish: domain.Known(s.nutr), FishPerBatch: domain.Known(1.0),
				WorkTicksPerBatch: domain.Known(supplysim.FishLaborPerFisher / supplysim.FishPerFisherDay), PawnFishWorkCapacity: domain.Known(sv.Rate * s.nutr),
				DistanceSquared: domain.Known(float64(len(water.Regions)))})
			ids[string(s.kind)+"/"+regionID] = id
		case policy.CandidateAnimalProduct:
			gatherable = append(gatherable, observation.GatherableAnimal{PawnID: id, Race: id, Active: domain.Known(true), HandlerReachable: domain.Known(true),
				NutritionPerDay: domain.Known(sv.Rate * s.nutr), WorkPerDay: domain.Known(s.src.Labor), LeadDays: domain.Known(0.0)})
			// The sim's animals eat nothing the colony counts.
			kept = append(kept, policy.UpkeepAnimal{ID: policy.PawnID(id), Herd: policy.HerdFacts{FeedPerDay: domain.Known(0.0)}})
			count(observation.DeliveryKey{Kind: observation.DeliveryAnimalProduct, SourceID: id, Def: id}, sv, s.nutr)
			a.groups["animal_product:"+id] = id
			ids[string(s.kind)+"/"+id] = id
		case policy.CandidateCrop:
			w := s.window
			if w.Period > 0 {
				window = &w
			}
			lead := float64(sv.GrowLeft)
			if !sv.Open || sv.GrowLeft == s.grow {
				lead += float64(daysUntilGrowing(w, v.Day))
			}
			fields = append(fields, policy.FoodField{ID: id, RemainingGrowDays: domain.Known(lead), WorkPerDay: domain.Known(s.cells * supplysim.CropHarvestWork / float64(s.grow)),
				Plan: policy.FieldPlan{Crop: policy.CropChoice{Name: id, Edible: domain.Known(true), GrowDays: domain.Known(float64(s.grow)), HarvestNutrition: domain.Known(s.nutr)},
					Sites: policy.FarmSitePlan{Cells: int(s.cells)}}})
			count(observation.DeliveryKey{Kind: observation.DeliveryCrop, SourceID: id, Def: id}, sv, s.nutr)
			a.groups["crop:"+id] = id
			ids[string(s.kind)+"/"+id] = id
		}
	}
	p.DeliveryLedger = domain.Known(ledger)
	p.Facts.AnimalUpkeep.Animals = domain.Known(kept)
	p.Acquisition = domain.Known(acquisition)
	p.FoodFields = domain.Known(fields)
	p.FoodChannels = domain.Known(observation.FoodChannels{FishableWater: domain.Known(water), Gatherable: gatherable})
	if window != nil {
		p.Facts.Calendar = domain.Known(calendarOn(*window, v.Day))
	}
	return p, ids
}

// daysUntilGrowing is the wait for the next growing day (0 inside the window).
func daysUntilGrowing(w supplysim.Window, day int) int {
	for d := 0; d < policy.YearDays; d++ {
		if w.Active(day + d) {
			return d
		}
	}
	return policy.YearDays
}

// calendarOn is the native growing calendar a window implies on a day.
func calendarOn(w supplysim.Window, day int) policy.Calendar {
	c := policy.Calendar{DayOfYear: int64(day % policy.YearDays), GrowingDays: float64(w.To - w.From), NonGrowingDays: float64(w.Period - (w.To - w.From))}
	c.GrowingDaysUntil = float64(daysUntilGrowing(w, day))
	if c.GrowingDaysUntil == 0 {
		c.Sowing = true
		c.GrowingDaysRemaining = float64(w.To - day%w.Period)
	}
	if c.GrowingDaysUntil > 0 {
		c.NonGrowingDays = c.GrowingDaysUntil
	}
	return c
}

// oracle opens every source every day: the best any planner could do with the
// labor on offer, used to decide whether the colony is viable at all.
var oracle = supplysim.PlannerFunc(func(v supplysim.WorldView) []supplysim.Command {
	var cmds []supplysim.Command
	for _, s := range v.Sources {
		cmds = append(cmds, supplysim.Command{Kind: supplysim.Open, Source: s.ID})
	}
	return cmds
})

type foodResult struct {
	rep       supplysim.Report
	days      []foodDay
	viable    bool
	minFactor map[string]float64
}

func runFood(sc foodScenario, horizon int) foodResult {
	a := &foodAdapter{sc: sc}
	r := foodResult{rep: supplysim.Run(sc.world(1), a, horizon), days: a.days, minFactor: a.minFactor}
	r.viable = !supplysim.Run(sc.world(foodViabilityMargin), oracle, horizon).Starved(supplysim.Nutrition)
	return r
}

// Assertion names, the baseline's vocabulary.
const (
	failStarves      = "starves-viable-colony"
	failRunwayOrder  = "more-channels-shorter-runway"
	failShockBetter  = "shock-improves-runway"
	failNoAlt        = "no-alternative-opened-after-shock"
	failSurplusClose = "surplus-close-while-short"
	failCreditStuck  = "over-estimating-channel-stays-credited"
)

// foodCreditFloor is the factor an over-estimating channel must reach.
const foodCreditFloor = 0.1

// foodFailures evaluates every assertion that applies to sc.
func foodFailures(sc foodScenario, res map[string]foodResult) []string {
	r := res[sc.name]
	var fails []string
	if r.viable && r.rep.Starved(supplysim.Nutrition) {
		fails = append(fails, failStarves)
	}
	if prev, ok := res[sc.prev]; ok && r.rep.MinRunway[supplysim.Nutrition] < prev.rep.MinRunway[supplysim.Nutrition]-foodRunwayTolerance {
		fails = append(fails, failRunwayOrder)
	}
	if twin, ok := res[sc.twin]; ok && r.rep.MinRunway[supplysim.Nutrition] > twin.rep.MinRunway[supplysim.Nutrition]+foodRunwayTolerance {
		fails = append(fails, failShockBetter)
	}
	if len(sc.shockedIDs) > 0 && !openedAlternative(sc, r) {
		fails = append(fails, failNoAlt)
	}
	for _, id := range sc.overestimates {
		// A source the planner had not opened before the shock has no history to
		// penalise: it is not judged before its lead plus one window.
		if f, seen := r.minFactor[id]; openedBeforeShock(r, id) && (!seen || f > foodCreditFloor) {
			fails = append(fails, failCreditStuck)
			break
		}
	}
	for _, d := range r.days {
		if d.closedAtGap {
			fails = append(fails, failSurplusClose)
			break
		}
	}
	return fails
}

func openedBeforeShock(r foodResult, id string) bool {
	for _, d := range r.days[:min(foodShockDay, len(r.days))] {
		if slices.Contains(d.opened, id) {
			return true
		}
	}
	return false
}

// openedAlternative holds when a source the shock left alone was closed at the
// shock and the colony ran short afterwards, then the planner opened one such
// source within three days; it holds vacuously when no such source exists or
// the colony never ran short.
func openedAlternative(sc foodScenario, r foodResult) bool {
	touched := map[string]bool{}
	for _, id := range sc.shockedIDs {
		touched[id] = true
	}
	opened := map[string]bool{}
	for _, d := range r.days[:min(foodShockDay, len(r.days))] {
		for _, id := range d.opened {
			opened[id] = true
		}
	}
	var spare []string
	for _, s := range sc.specs {
		if !touched[s.src.ID] && !opened[s.src.ID] && !s.hidden && s.kind != policy.CandidateAnimalProduct {
			spare = append(spare, s.src.ID)
		}
	}
	end := min(foodShockDay+3, len(r.days))
	if len(spare) == 0 || r.rep.Days[end-1].Runway[supplysim.Nutrition] >= policy.DefaultRoundsPolicy().FoodMinDays {
		return true
	}
	for _, d := range r.days[foodShockDay:end] {
		for _, id := range d.opened {
			if slices.Contains(spare, id) {
				return true
			}
		}
	}
	return false
}

// ---- scenario construction ----

// foodShare is the share of base demand each generated channel is sized to
// deliver at full labor.
const foodShare = 0.7

// foodShockShare sizes the four channels of a shock colony so losing two of
// them leaves it near its demand.
const foodShockShare = 0.4

// buildSpec sizes one channel of the given kind to deliver need nutrition per
// day.
func buildSpec(kind policy.CandidateKind, id string, need float64) srcSpec {
	switch kind {
	case policy.CandidateForage:
		const nutr = 0.9
		units := need / nutr
		foragers := int(math.Ceil(units / 4))
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewForage(id, units*15, units, nutr, supplysim.Window{}, foragers)}
	case policy.CandidateHunt:
		const nutr = 20.0
		kills := need / nutr
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewHerd(id, kills*30, nutr, int(math.Ceil(kills)), 0)}
	case policy.CandidateFishing:
		const nutr = 0.5
		maxPop := need / (supplysim.FishingRegenFraction * nutr)
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewFishing(id, maxPop, maxPop, int(math.Ceil(need/(supplysim.FishPerFisherDay*nutr))), nutr)}
	case policy.CandidateCrop:
		const nutr, grow = 3.0, 6
		cells := math.Ceil(need * grow / nutr)
		window := supplysim.Window{Period: policy.YearDays, From: 0, To: 45}
		return srcSpec{kind: kind, nutr: nutr, cells: cells, grow: grow, window: window, src: supplysim.NewCrop(id, cells, grow, nutr, window, cells)}
	case policy.CandidateAnimalProduct:
		const nutr = 0.5
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewProducts(id, math.Ceil(need/nutr), 1, nutr)}
	case policy.CandidateTrade:
		const nutr, period = 1.0, 15
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewTrade(id, supplysim.Window{Period: period, From: 0, To: 2}, need*period/nutr, nutr, 1)}
	}
	panic("unknown channel kind " + string(kind))
}

func foodSrcID(kind policy.CandidateKind) string { return strings.ToLower(string(kind)) }

func mixScenario(name string, colonists int, level, share float64, kinds []policy.CandidateKind) foodScenario {
	perDay := foodNutritionPerColonist * float64(colonists)
	sc := foodScenario{name: name, colonists: colonists, perDay: perDay * level, stock: perDay * level * 3}
	for _, k := range kinds {
		spec := buildSpec(k, foodSrcID(k), perDay*share)
		if k == policy.CandidateTrade {
			sc.silver = spec.src.Restock * 8
		}
		sc.specs = append(sc.specs, spec)
	}
	return sc
}

var foodChains = map[string][]policy.CandidateKind{
	"forage-first":  {policy.CandidateForage, policy.CandidateHunt, policy.CandidateFishing, policy.CandidateCrop},
	"farm-first":    {policy.CandidateCrop, policy.CandidateAnimalProduct, policy.CandidateFishing, policy.CandidateHunt},
	"fishing-first": {policy.CandidateFishing, policy.CandidateForage, policy.CandidateAnimalProduct, policy.CandidateCrop},
	"trade-first":   {policy.CandidateTrade, policy.CandidateHunt, policy.CandidateForage, policy.CandidateAnimalProduct},
}

var foodSizes = []int{3, 8, 16}

// bowReach is a short bow's range: the reach the gate offers a colony whose
// only hunters carry bows (hunting is vanilla ranged-only, arrows included).
const bowReach = 25.9

// huntScenarios are the colonies the hunt channel decides: wild animals the
// gates never offer (no butcher bill, no qualifying hunter), and a colony fed
// by hunting alone with bow hunters.
func huntScenarios(size int) []foodScenario {
	hidden := mixScenario(fmt.Sprintf("hunt/no-huntable-animals/n%d", size), size, 1, 1, []policy.CandidateKind{policy.CandidateForage, policy.CandidateCrop, policy.CandidateHunt})
	hidden.specs[2].hidden = true
	bows := mixScenario(fmt.Sprintf("hunt/only-bows/n%d", size), size, 1, foodShare, []policy.CandidateKind{policy.CandidateHunt})
	bows.specs[0].reach = bowReach
	return []foodScenario{hidden, bows}
}

var foodDemandLevels = map[string]float64{"base": 1, "high": 1.5}

// foodShocks are dated at foodShockDay on a four-channel colony; each lists
// the shocks and the sources they touch.
var foodShocks = map[string]struct {
	shocks  []supplysim.Shock
	touched []string
	// over names the touched sources the shock leaves delivering almost nothing
	// while the plan still expects their rate.
	over []string
}{
	"overfishing":    {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.DestroyStock, Source: "fishing", Factor: 0.95}}, []string{"fishing"}, []string{"fishing"}},
	"pond-collapse":  {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.DestroyStock, Source: "fishing", Factor: 0.99}}, []string{"fishing"}, []string{"fishing"}},
	"fallout-fields": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.DestroyStock, Source: "crop", Factor: 1}, {Day: foodShockDay, Kind: supplysim.PauseGrowth, Source: "crop", Days: 40}}, []string{"crop"}, []string{"crop"}},
	"eclipse": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.PauseGrowth, Source: "crop", Days: 4}, {Day: foodShockDay, Kind: supplysim.PauseGrowth, Source: "forage", Days: 4}},
		[]string{"crop", "forage"}, nil},
	"toxic-fallout": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.DestroyStock, Source: "crop", Factor: 0.8}, {Day: foodShockDay, Kind: supplysim.PauseGrowth, Source: "crop", Days: 15},
		{Day: foodShockDay, Kind: supplysim.ScaleCapacity, Source: "forage", Factor: 0.5}}, []string{"crop", "forage"}, []string{"crop"}},
	"volcanic-winter": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.PauseGrowth, Source: "crop", Days: 20}, {Day: foodShockDay, Kind: supplysim.ScaleCapacity, Source: "forage", Factor: 0.2},
		{Day: foodShockDay, Kind: supplysim.ScaleCapacity, Source: "hunt", Factor: 0.5}, {Day: foodShockDay, Kind: supplysim.ScaleCapacity, Source: "fishing", Factor: 0.5}},
		[]string{"crop", "forage", "hunt", "fishing"}, nil},
	"no-huntable-animals": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.RemoveSource, Source: "hunt"}}, []string{"hunt"}, nil},
	"no-farmland":         {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.RemoveSource, Source: "crop"}}, []string{"crop"}, nil},
	"no-soil": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.RemoveSource, Source: "crop"}, {Day: foodShockDay, Kind: supplysim.RemoveSource, Source: "forage"}},
		[]string{"crop", "forage"}, nil},
}

var foodShockMix = []policy.CandidateKind{policy.CandidateFishing, policy.CandidateCrop, policy.CandidateHunt, policy.CandidateForage}

// seedScenarios are the recorded colonies, rebuilt from the plan each
// snapshot recorded: its demand, its runway and the rows it listed.
func seedScenarios(t testing.TB) []foodScenario {
	t.Helper()
	var out []foodScenario
	for _, name := range []string{"food-ledger-baseline-forage-hunt.json", "food-corpse-larder-release.json", "trade-food-bridge-one-day.json",
		"trade-food-crop-surplus-for-meat.json", "food-starving-tribal-no-hunt-row.json.gz"} {
		r, err := snapshot.Load("../snapshot/testdata/" + name)
		if err != nil {
			t.Fatal(err)
		}
		plan, known := r.Facts.FoodPlan.Value()
		days, _ := r.Facts.FoodDays.Value()
		if !known {
			t.Fatalf("%s: food plan unknown", name)
		}
		sc := foodScenario{name: "seed/" + strings.TrimSuffix(strings.TrimSuffix(name, ".gz"), ".json"), perDay: plan.DemandPerDay, stock: days * plan.DemandPerDay}
		sc.colonists = max(1, int(math.Round(plan.DemandPerDay/1.8)))
		for i, row := range plan.Portfolio {
			n, nk := row.Channel.Nutrition().PerDay.Value()
			w, _ := row.Channel.LaborPerDay.Value()
			lead, _ := row.Channel.LeadDays.Value()
			if !nk || n <= 0 {
				continue
			}
			id := fmt.Sprintf("%s-%d", foodSrcID(row.Channel.Kind), i)
			switch row.Channel.Kind {
			case policy.CandidateForage:
				sc.specs = append(sc.specs, srcSpec{kind: policy.CandidateForage, nutr: n, src: supplysim.Source{ID: id, Yields: []supplysim.Yield{{Good: supplysim.Nutrition, PerUnit: n}},
					Capacity: 1, Labor: w, Finite: true, Stock: 30, Max: 30, Regen: 1}})
			case policy.CandidateHunt:
				sc.specs = append(sc.specs, srcSpec{kind: policy.CandidateHunt, nutr: n, src: supplysim.Source{ID: id, Yields: []supplysim.Yield{{Good: supplysim.Nutrition, PerUnit: n}},
					Capacity: 1, Labor: w, Finite: true, Stock: 1, Max: 1}})
			case policy.CandidateCrop:
				grow := max(1, int(math.Round(lead)))
				window := supplysim.Window{}
				sc.specs = append(sc.specs, srcSpec{kind: policy.CandidateCrop, nutr: n * float64(grow) / 10, cells: 10, grow: grow, window: window,
					src: supplysim.NewCrop(id, 10, grow, n*float64(grow)/10, window, 10)})
				sc.specs[len(sc.specs)-1].src.Labor = w
			}
		}
		if strings.Contains(name, "starving") {
			// The wild deer and the berries the run saw (#2140 diagnosis) that
			// native gates never turned into rows.
			sc.specs = append(sc.specs,
				srcSpec{kind: policy.CandidateHunt, hidden: true, nutr: 20, src: supplysim.NewHerd("deer", 20, 20, 3, 0)},
				srcSpec{kind: policy.CandidateForage, hidden: true, nutr: 0.9, src: supplysim.NewForage("berries", 100, 6, 0.9, supplysim.Window{}, 4)})
		}
		out = append(out, sc)
	}
	return out
}

func foodScenarios(t testing.TB) []foodScenario {
	var out []foodScenario
	for _, size := range foodSizes {
		for _, level := range slices.Sorted(maps.Keys(foodDemandLevels)) {
			for _, chain := range slices.Sorted(maps.Keys(foodChains)) {
				kinds := foodChains[chain]
				prev := ""
				for k := 1; k <= len(kinds); k++ {
					name := fmt.Sprintf("mix/%s/%dch/n%d/%s", chain, k, size, level)
					sc := mixScenario(name, size, foodDemandLevels[level], foodShare, kinds[:k])
					sc.prev = prev
					out = append(out, sc)
					prev = name
				}
			}
		}
		out = append(out, huntScenarios(size)...)
		for _, shock := range slices.Sorted(maps.Keys(foodShocks)) {
			base := mixScenario(fmt.Sprintf("shock-twin/n%d", size), size, 1, foodShockShare, foodShockMix)
			sc := mixScenario(fmt.Sprintf("shock/%s/n%d", shock, size), size, 1, foodShockShare, foodShockMix)
			sc.shocks, sc.shockedIDs, sc.twin = foodShocks[shock].shocks, foodShocks[shock].touched, base.name
			sc.overestimates = foodShocks[shock].over
			if !slices.ContainsFunc(out, func(s foodScenario) bool { return s.name == base.name }) {
				out = append(out, base)
			}
			out = append(out, sc)
		}
	}
	// Crop only, with and without a cook bench (#2159).
	crop := []policy.CandidateKind{policy.CandidateCrop}
	bare := mixScenario("crop/only/no-cook-bench/n8", 8, 1, foodShare, crop)
	kitchen := mixScenario("crop/only/cook-bench/n8", 8, 1, foodShare, crop)
	kitchen.kitchen, kitchen.prev = true, bare.name
	out = append(out, bare, kitchen)
	return append(out, seedScenarios(t)...)
}

// ---- baseline ----

func loadFoodBaseline(t testing.TB) map[string][]string {
	t.Helper()
	data, err := os.ReadFile(foodBaselinePath)
	if os.IsNotExist(err) {
		return map[string][]string{}
	}
	if err != nil {
		t.Fatal(err)
	}
	var base map[string][]string
	if err := json.Unmarshal(data, &base); err != nil {
		t.Fatal(err)
	}
	return base
}

func runFoodMatrix(t *testing.T, horizon int) {
	suffix := fmt.Sprintf("@%d", horizon)
	scenarios := foodScenarios(t)
	results := map[string]foodResult{}
	for _, sc := range scenarios {
		results[sc.name] = runFood(sc, horizon)
	}
	current := map[string][]string{}
	for _, sc := range scenarios {
		if fails := foodFailures(sc, results); len(fails) > 0 {
			current[sc.name+suffix] = fails
		}
	}
	base := loadFoodBaseline(t)
	recorded := false
	for k := range base {
		recorded = recorded || strings.HasSuffix(k, suffix)
	}
	if *updateFoodBaseline {
		for k, fails := range current {
			if recorded && !slices.Equal(base[k], fails) && !isSubset(fails, base[k]) {
				t.Fatalf("%s: new failures %v; the baseline only shrinks", k, fails)
			}
		}
		for k := range base {
			if strings.HasSuffix(k, suffix) {
				delete(base, k)
			}
		}
		maps.Copy(base, current)
		data, err := json.MarshalIndent(base, "", "  ")
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(foodBaselinePath, append(data, '\n'), 0o644); err != nil {
			t.Fatal(err)
		}
		return
	}
	names := map[string]bool{}
	for _, sc := range scenarios {
		names[sc.name+suffix] = true
	}
	for _, k := range slices.Sorted(maps.Keys(base)) {
		if strings.HasSuffix(k, suffix) && !names[k] {
			t.Errorf("baseline entry %s names no scenario; delete it", k)
		}
	}
	for _, k := range slices.Sorted(maps.Keys(current)) {
		for _, f := range current[k] {
			if !slices.Contains(base[k], f) {
				t.Errorf("%s fails %q and is not in the baseline", k, f)
			}
		}
	}
	for _, k := range slices.Sorted(maps.Keys(base)) {
		if !strings.HasSuffix(k, suffix) {
			continue
		}
		for _, f := range base[k] {
			if !slices.Contains(current[k], f) {
				t.Errorf("%s now passes %q: delete it from the baseline (go test ./internal/buildingruntime -run TestFoodMatrix -update-food-baseline)", k, f)
			}
		}
	}
}

func isSubset(a, b []string) bool {
	for _, x := range a {
		if !slices.Contains(b, x) {
			return false
		}
	}
	return true
}

func TestFoodMatrix(t *testing.T) { runFoodMatrix(t, foodShortHorizon) }

func TestFoodMatrixLongHorizon(t *testing.T) {
	slowtest.Skip(t, "90 simulated days per scenario")
	runFoodMatrix(t, foodLongHorizon)
}

// The recorded starving-tribal colony (#2141): today's planner never opens a
// Hunt, Forage or Fishing row there, so the colony starves although wild deer
// and berries exist, and the baseline records it.
func TestFoodMatrixStarvingTribalStarvesWithoutHunt(t *testing.T) {
	var sc foodScenario
	for _, s := range seedScenarios(t) {
		if strings.HasSuffix(s.name, "starving-tribal-no-hunt-row") {
			sc = s
		}
	}
	res := runFood(sc, foodShortHorizon)
	if !res.rep.Starved(supplysim.Nutrition) {
		t.Fatal("the starving colony did not starve")
	}
	for _, d := range res.days {
		for _, id := range d.opened {
			if spec := sc.spec(id); spec.kind == policy.CandidateHunt || spec.kind == policy.CandidateForage || spec.kind == policy.CandidateFishing {
				t.Fatalf("the planner opened %s with no row to open", id)
			}
		}
	}
	if !res.viable {
		t.Fatal("the starving colony is not viable even for an oracle, so the starvation proves nothing")
	}
}
