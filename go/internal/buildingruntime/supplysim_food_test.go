package buildingruntime

// Food matrix (epic #2140, child 3): the REAL reviewFoodPlan driven day by day
// through the supplysim world. Each simulated day the adapter builds the
// observation.ColonyProjection fields reviewFoodPlan reads (Workers,
// CombinedFoodSupply, FoodSupply, Acquisition, FoodChannels, FoodFields,
// Facts.Calendar) from the world, calls reviewFoodPlan and applies its Open and
// Close rows as source commands. No projection field needed a fallback to
// policy.PlanFood plus the channel builders. Only what the real planner can see
// reaches it:
//
//   - Trade has no plan row, so a trade source is never opened.
//   - AnimalProduct rows are always Open, so products deliver from day 0 and
//     their rows are never acted on.
//   - Crop rows are never Open and Hunt/Forage rows carry no state, so those
//     rows reopen daily (a no-op for an open source).
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
	kind   policy.FoodChannelKind
	src    supplysim.Source
	nutr   float64 // nutrition per source unit
	cells  float64 // crop field cells
	grow   int     // crop grow days
	window supplysim.Window
	hidden bool // kept out of the projection by native gates
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
	// shockedIDs are the sources a shock touches.
	shockedIDs []string
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
		// Products are always Open in the planner; every other source starts closed.
		src.Open = s.kind == policy.FoodAnimalProduct
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
	sc foodScenario
	// supply plans through policy.PlanSupply instead of PlanFood.
	supply bool
	days   []foodDay
	plans  []policy.FoodPlan
}

func (a *foodAdapter) Plan(v supplysim.WorldView) []supplysim.Command {
	p, ids := a.projection(v)
	review := reviewFoodPlan
	if a.supply {
		review = reviewFoodPlanBySupply
	}
	plan, known := review(p, policy.DefaultRoundsPolicy()).Value()
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
		switch e.Decision {
		case policy.FoodPlanOpen:
			cmds = append(cmds, supplysim.Command{Kind: supplysim.Open, Source: id})
			if !open[id] {
				d.opened = append(d.opened, id)
			}
		case policy.FoodPlanClose:
			if e.Channel.Kind == policy.FoodAnimalProduct {
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
	ids := map[string]string{}
	var acquisition []policy.AcquisitionSource
	var fields []policy.FoodField
	var water observation.FishableWater
	water.FishingResearched = domain.Known(true)
	var gatherable []observation.GatherableAnimal
	var window *supplysim.Window
	for _, s := range sc.specs {
		sv, id := views[s.src.ID], s.src.ID
		if sv.Removed || s.hidden {
			continue
		}
		switch s.kind {
		case policy.FoodForage, policy.FoodHunt:
			if sv.Rate <= 0 {
				continue
			}
			hunt := s.kind == policy.FoodHunt
			acquisition = append(acquisition, policy.AcquisitionSource{ID: id, Food: true, Hunt: hunt, NutritionYield: sv.Rate * s.nutr, Designated: sv.Open})
			ids[string(s.kind)+"/"+id] = id
		case policy.FoodFishing:
			regionID := policy.FishingRegionID(domain.Cell{X: int32(len(water.Regions))})
			// A fishing row is Open while the region delivers: open and above its floor.
			delivering := sv.Open && sv.Stock >= s.src.Floor*sv.Max
			water.Regions = append(water.Regions, observation.FishableRegion{Root: domain.Cell{X: int32(len(water.Regions))},
				Population: domain.Known(sv.Stock), MaxPopulation: domain.Known(sv.Max), Reachable: domain.Known(true), Frozen: domain.Known(false),
				Delivering: domain.Known(delivering), NutritionPerFish: domain.Known(s.nutr), FishPerBatch: domain.Known(1.0),
				WorkTicksPerBatch: domain.Known(supplysim.FishLaborPerFisher / supplysim.FishPerFisherDay), PawnFishWorkCapacity: domain.Known(sv.Rate * s.nutr),
				DistanceSquared: domain.Known(float64(len(water.Regions)))})
			ids[string(s.kind)+"/"+regionID] = id
		case policy.FoodAnimalProduct:
			gatherable = append(gatherable, observation.GatherableAnimal{PawnID: id, Race: id, Active: domain.Known(true), HandlerReachable: domain.Known(true),
				NutritionPerDay: domain.Known(sv.Rate * s.nutr), WorkPerDay: domain.Known(s.src.Labor), LeadDays: domain.Known(0.0)})
			ids[string(s.kind)+"/"+id] = id
		case policy.FoodCrop:
			w := s.window
			if w.Period > 0 {
				window = &w
			}
			lead := float64(sv.GrowLeft)
			if !sv.Open || sv.GrowLeft == s.grow {
				lead += float64(daysUntilGrowing(w, v.Day))
			}
			fields = append(fields, policy.FoodField{ID: id, RemainingGrowDays: domain.Known(lead), WorkPerDay: domain.Known(s.cells * supplysim.CropHarvestWork / float64(s.grow)), Open: domain.Known(false),
				Plan: policy.FieldPlan{Crop: policy.CropChoice{Name: id, Edible: domain.Known(true), GrowDays: domain.Known(float64(s.grow)), HarvestNutrition: domain.Known(s.nutr)},
					Sites: policy.FarmSitePlan{Cells: int(s.cells)}}})
			ids[string(s.kind)+"/"+id] = id
		}
	}
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
	rep    supplysim.Report
	days   []foodDay
	viable bool
}

func runFood(sc foodScenario, horizon int) foodResult { return runFoodWith(sc, horizon, false) }

func runFoodWith(sc foodScenario, horizon int, supply bool) foodResult {
	a := &foodAdapter{sc: sc, supply: supply}
	r := foodResult{rep: supplysim.Run(sc.world(1), a, horizon), days: a.days}
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
)

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
	for _, d := range r.days {
		if d.closedAtGap {
			fails = append(fails, failSurplusClose)
			break
		}
	}
	return fails
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
		if !touched[s.src.ID] && !opened[s.src.ID] && !s.hidden && s.kind != policy.FoodAnimalProduct {
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
func buildSpec(kind policy.FoodChannelKind, id string, need float64) srcSpec {
	switch kind {
	case policy.FoodForage:
		const nutr = 0.9
		units := need / nutr
		foragers := int(math.Ceil(units / 4))
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewForage(id, units*15, units, nutr, supplysim.Window{}, foragers)}
	case policy.FoodHunt:
		const nutr = 20.0
		kills := need / nutr
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewHerd(id, kills*30, nutr, int(math.Ceil(kills)), 0)}
	case policy.FoodFishing:
		const nutr = 0.5
		maxPop := need / (supplysim.FishingRegenFraction * nutr)
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewFishing(id, maxPop, maxPop, int(math.Ceil(need/(supplysim.FishPerFisherDay*nutr))), nutr)}
	case policy.FoodCrop:
		const nutr, grow = 3.0, 6
		cells := math.Ceil(need * grow / nutr)
		window := supplysim.Window{Period: policy.YearDays, From: 0, To: 45}
		return srcSpec{kind: kind, nutr: nutr, cells: cells, grow: grow, window: window, src: supplysim.NewCrop(id, cells, grow, nutr, window, cells)}
	case policy.FoodAnimalProduct:
		const nutr = 0.5
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewProducts(id, math.Ceil(need/nutr), 1, nutr)}
	case policy.FoodTrade:
		const nutr, period = 1.0, 15
		return srcSpec{kind: kind, nutr: nutr, src: supplysim.NewTrade(id, supplysim.Window{Period: period, From: 0, To: 2}, need*period/nutr, nutr, 1)}
	}
	panic("unknown channel kind " + string(kind))
}

func foodSrcID(kind policy.FoodChannelKind) string { return strings.ToLower(string(kind)) }

func mixScenario(name string, colonists int, level, share float64, kinds []policy.FoodChannelKind) foodScenario {
	perDay := foodNutritionPerColonist * float64(colonists)
	sc := foodScenario{name: name, colonists: colonists, perDay: perDay * level, stock: perDay * level * 3}
	for _, k := range kinds {
		spec := buildSpec(k, foodSrcID(k), perDay*share)
		if k == policy.FoodTrade {
			sc.silver = spec.src.Restock * 8
		}
		sc.specs = append(sc.specs, spec)
	}
	return sc
}

var foodChains = map[string][]policy.FoodChannelKind{
	"forage-first":  {policy.FoodForage, policy.FoodHunt, policy.FoodFishing, policy.FoodCrop},
	"farm-first":    {policy.FoodCrop, policy.FoodAnimalProduct, policy.FoodFishing, policy.FoodHunt},
	"fishing-first": {policy.FoodFishing, policy.FoodForage, policy.FoodAnimalProduct, policy.FoodCrop},
	"trade-first":   {policy.FoodTrade, policy.FoodHunt, policy.FoodForage, policy.FoodAnimalProduct},
}

var foodSizes = []int{3, 8, 16}

var foodDemandLevels = map[string]float64{"base": 1, "high": 1.5}

// foodShocks are dated at foodShockDay on a four-channel colony; each lists
// the shocks and the sources they touch.
var foodShocks = map[string]struct {
	shocks  []supplysim.Shock
	touched []string
}{
	"overfishing": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.DestroyStock, Source: "fishing", Factor: 0.95}}, []string{"fishing"}},
	"eclipse": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.PauseGrowth, Source: "crop", Days: 4}, {Day: foodShockDay, Kind: supplysim.PauseGrowth, Source: "forage", Days: 4}},
		[]string{"crop", "forage"}},
	"toxic-fallout": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.DestroyStock, Source: "crop", Factor: 0.8}, {Day: foodShockDay, Kind: supplysim.PauseGrowth, Source: "crop", Days: 15},
		{Day: foodShockDay, Kind: supplysim.ScaleCapacity, Source: "forage", Factor: 0.5}}, []string{"crop", "forage"}},
	"volcanic-winter": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.PauseGrowth, Source: "crop", Days: 20}, {Day: foodShockDay, Kind: supplysim.ScaleCapacity, Source: "forage", Factor: 0.2},
		{Day: foodShockDay, Kind: supplysim.ScaleCapacity, Source: "hunt", Factor: 0.5}, {Day: foodShockDay, Kind: supplysim.ScaleCapacity, Source: "fishing", Factor: 0.5}},
		[]string{"crop", "forage", "hunt", "fishing"}},
	"no-huntable-animals": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.RemoveSource, Source: "hunt"}}, []string{"hunt"}},
	"no-farmland":         {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.RemoveSource, Source: "crop"}}, []string{"crop"}},
	"no-soil": {[]supplysim.Shock{{Day: foodShockDay, Kind: supplysim.RemoveSource, Source: "crop"}, {Day: foodShockDay, Kind: supplysim.RemoveSource, Source: "forage"}},
		[]string{"crop", "forage"}},
}

var foodShockMix = []policy.FoodChannelKind{policy.FoodFishing, policy.FoodCrop, policy.FoodHunt, policy.FoodForage}

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
			n, nk := row.Channel.NutritionPerDay.Value()
			w, _ := row.Channel.WorkPerDay.Value()
			lead, _ := row.Channel.LeadDays.Value()
			if !nk || n <= 0 {
				continue
			}
			id := fmt.Sprintf("%s-%d", foodSrcID(row.Channel.Kind), i)
			switch row.Channel.Kind {
			case policy.FoodForage:
				sc.specs = append(sc.specs, srcSpec{kind: policy.FoodForage, nutr: n, src: supplysim.Source{ID: id, Yields: []supplysim.Yield{{Good: supplysim.Nutrition, PerUnit: n}},
					Capacity: 1, Labor: w, Finite: true, Stock: 30, Max: 30, Regen: 1}})
			case policy.FoodHunt:
				sc.specs = append(sc.specs, srcSpec{kind: policy.FoodHunt, nutr: n, src: supplysim.Source{ID: id, Yields: []supplysim.Yield{{Good: supplysim.Nutrition, PerUnit: n}},
					Capacity: 1, Labor: w, Finite: true, Stock: 1, Max: 1}})
			case policy.FoodCrop:
				grow := max(1, int(math.Round(lead)))
				window := supplysim.Window{}
				sc.specs = append(sc.specs, srcSpec{kind: policy.FoodCrop, nutr: n * float64(grow) / 10, cells: 10, grow: grow, window: window,
					src: supplysim.NewCrop(id, 10, grow, n*float64(grow)/10, window, 10)})
				sc.specs[len(sc.specs)-1].src.Labor = w
			}
		}
		if strings.Contains(name, "starving") {
			// The wild deer and the berries the run saw (#2140 diagnosis) that
			// native gates never turned into rows.
			sc.specs = append(sc.specs,
				srcSpec{kind: policy.FoodHunt, hidden: true, nutr: 20, src: supplysim.NewHerd("deer", 20, 20, 3, 0)},
				srcSpec{kind: policy.FoodForage, hidden: true, nutr: 0.9, src: supplysim.NewForage("berries", 100, 6, 0.9, supplysim.Window{}, 4)})
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
		for _, shock := range slices.Sorted(maps.Keys(foodShocks)) {
			base := mixScenario(fmt.Sprintf("shock-twin/n%d", size), size, 1, foodShockShare, foodShockMix)
			sc := mixScenario(fmt.Sprintf("shock/%s/n%d", shock, size), size, 1, foodShockShare, foodShockMix)
			sc.shocks, sc.shockedIDs, sc.twin = foodShocks[shock].shocks, foodShocks[shock].touched, base.name
			if !slices.ContainsFunc(out, func(s foodScenario) bool { return s.name == base.name }) {
				out = append(out, base)
			}
			out = append(out, sc)
		}
	}
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
			if spec := sc.spec(id); spec.kind == policy.FoodHunt || spec.kind == policy.FoodForage || spec.kind == policy.FoodFishing {
				t.Fatalf("the planner opened %s with no row to open", id)
			}
		}
	}
	if !res.viable {
		t.Fatal("the starving colony is not viable even for an oracle, so the starvation proves nothing")
	}
}
