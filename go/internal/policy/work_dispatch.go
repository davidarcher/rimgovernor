package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The bench dispatcher is the decision half of the work ledger: how many
// benches should carry each stock-target order (the rate model, BenchesWanted),
// whether production keeps up with that prediction (a rolling one-day window of
// measured stock gain) and, when it does not, whether one more bench would help
// or what stops it. buildingruntime only feeds it observations and keeps the
// memory it returns.

const (
	// ThroughputWindowTicks is the calibration window: one game day.
	ThroughputWindowTicks = domain.TicksPerDay
	// ShortfallFraction is the share of predicted stock gain below which a
	// full window counts as far below the prediction.
	ShortfallFraction = 0.5
	// IngredientBoundStock is the ingredient stock at or below which a recipe
	// is ingredient-bound.
	IngredientBoundStock = 1
)

// UnmetReason says why a kind's throughput stays unmet.
type UnmetReason string

const (
	// UnmetNoBench: the rate model wants more benches than exist (or none of
	// the kind can take the recipe). A further bench would help.
	UnmetNoBench UnmetReason = "no_bench"
	// UnmetSlotsFull: every capable bench carries the 15-bill cap.
	UnmetSlotsFull UnmetReason = "bench_slots_full"
	// UnmetIngredients: measured production is short because the recipe's
	// ingredients are out; a further bench would not help.
	UnmetIngredients UnmetReason = "ingredient_bound"
	// UnmetHaul: the bill is active and its ingredients exist but the bench sat
	// idle all window; hauling, not bench count, limits it.
	UnmetHaul UnmetReason = "haul_bound"
	// UnmetBenchesExhausted: production stays short with a bill on every usable
	// bench; only a new bench can raise it.
	UnmetBenchesExhausted UnmetReason = "benches_exhausted"
)

// UnmetThroughput is the throughput one bench kind lacks, in product units per
// day, and why. The facilities ladder reads it as its demand signal.
type UnmetThroughput struct {
	BenchKind   string
	ShortPerDay float64
	Reason      UnmetReason
}

// Stocked is whether the order is a stock target the rate model can size: the
// declarer named the product and the target is positive.
func (s OrderSpec) Stocked() bool {
	return s.Product != "" && wireClass(s.Mode) == domain.StockTarget && s.Target > 0
}

// EligibleBenches are the usable benches of the order's kind that offer its
// recipe, ordered by id.
func EligibleBenches(order OrderSpec, benches []GearBench) []GearBench {
	var out []GearBench
	for _, b := range benches {
		if b.Def == order.BenchKind && benchOffers(b, order.Recipe) {
			out = append(out, b)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}

// OrderDispatch is the rate model's answer for one order.
type OrderDispatch struct {
	// Copies is how many benches should carry the order: the rate model's
	// benches wanted plus calibration widening, within one and the eligible
	// benches (one when the model is unknown).
	Copies   int
	Eligible int
	// Demand is units per day to clear the deficit; Capacity is units per day
	// of one bench with an average worker.
	Demand, Capacity domain.Fact[float64]
	Wanted           domain.Fact[int]
	// NoBenchShort is the units per day the eligible benches cannot make
	// when the model wants more benches than exist.
	NoBenchShort float64
}

// DispatchOrder sizes one order. Orders the model cannot size (not a stock
// target, stock, workers or recipe work unknown) want one bench. widen is the
// calibration's extra benches. The workers are the pawns that can work the
// recipe (DispatchWorkers).
func DispatchOrder(order OrderSpec, stock domain.Fact[map[Resource]int64], benches []GearBench, pawns []WorkPawn, widen int) OrderDispatch {
	eligible := EligibleBenches(order, benches)
	d := OrderDispatch{Copies: 1, Eligible: len(eligible), Demand: domain.Unknown[float64](), Capacity: domain.Unknown[float64](), Wanted: domain.Unknown[int]()}
	if !order.Stocked() || len(eligible) == 0 {
		return d
	}
	have, known := stock.Value()
	if !known {
		return d
	}
	d.Demand = DemandRate(domain.Known(float64(int64(order.Target)-have[order.Product])), order.Class)
	required, _ := recipeOf(eligible[0], order.Recipe).RequiredWork.Value()
	workers := DispatchWorkers(pawns, required)
	d.Capacity = meanCapacity(order, eligible, AverageWorker(workers))
	d.Wanted = BenchesWanted(d.Demand, d.Capacity)
	n, ok := d.Wanted.Value()
	if !ok {
		return d
	}
	if capacity, _ := d.Capacity.Value(); n > len(eligible) {
		demand, _ := d.Demand.Value()
		d.NoBenchShort = demand - float64(len(eligible))*capacity
	}
	if len(workers) > 0 && n > len(workers) {
		n = len(workers)
	}
	d.Copies = min(max(n+widen, 1), len(eligible))
	return d
}

// recipeOf is the bench's row of the recipe; the zero row (every fact unknown)
// when it has none.
func recipeOf(b GearBench, recipe string) GearRecipe {
	recipes, _ := b.Recipes.Value()
	for _, r := range recipes {
		if r.Definition == recipe {
			return r
		}
	}
	return GearRecipe{}
}

// meanCapacity is the mean units per day one eligible bench makes with the
// worker; unknown when any bench's is.
func meanCapacity(order OrderSpec, eligible []GearBench, worker BenchWorker) domain.Fact[float64] {
	var sum float64
	for _, b := range eligible {
		c, ok := BenchCapacity(BenchRecipe{WorkPerUnit: recipeOf(b, order.Recipe).WorkAmount, BenchWorkSpeed: b.WorkSpeed}, []BenchWorker{worker}).Value()
		if !ok {
			return domain.Unknown[float64]()
		}
		sum += c
	}
	return domain.Known(sum / float64(len(eligible)))
}

// AverageWorker is the mean of the workers. Any unknown input makes the mean
// unknown; no workers is a worker of zero hours.
func AverageWorker(workers []BenchWorker) BenchWorker {
	if len(workers) == 0 {
		return BenchWorker{WorkHoursPerDay: domain.Known(0.0), WorkSpeed: domain.Known(0.0)}
	}
	var hours, speed float64
	for _, w := range workers {
		h, hok := w.WorkHoursPerDay.Value()
		s, sok := w.WorkSpeed.Value()
		if !hok || !sok {
			return BenchWorker{WorkHoursPerDay: domain.Unknown[float64](), WorkSpeed: domain.Unknown[float64]()}
		}
		hours += h
		speed += s
	}
	n := float64(len(workers))
	return BenchWorker{WorkHoursPerDay: domain.Known(hours / n), WorkSpeed: domain.Known(speed / n)}
}

// DispatchWorkers are the pawns that can work a bench: available, with work
// hours and speed known, and (when the recipe names work types) with one of
// them enabled. Speed is the global work speed, one plus the pawn's summed
// WorkSpeedGlobal offset.
func DispatchWorkers(pawns []WorkPawn, work []WorkRequirement) []BenchWorker {
	var out []BenchWorker
	for _, p := range pawns {
		if avail, ok := p.Available.Value(); !ok || !avail {
			continue
		}
		if len(work) > 0 && !pawnWorks(p, work) {
			continue
		}
		profile := BuildProfile(p)
		w := BenchWorker{WorkHoursPerDay: domain.Unknown[float64](), WorkSpeed: domain.Known(max(1+profile.Effects.WorkSpeed, 0))}
		if h, ok := profile.WorkHours.Value(); ok {
			w.WorkHoursPerDay = domain.Known(float64(h))
		}
		out = append(out, w)
	}
	return out
}

func pawnWorks(p WorkPawn, work []WorkRequirement) bool {
	rows, _ := p.Work.Value()
	for _, row := range rows {
		if row.Priority <= 0 || row.Disabled {
			continue
		}
		for _, req := range work {
			if row.Work == req.Work {
				return true
			}
		}
	}
	return false
}

// BenchesBusy is whether any pawn's current job works one of the benches.
func BenchesBusy(pawns []WorkPawn, benches []string) bool {
	for _, p := range pawns {
		job, ok := p.Job.Value()
		if !ok {
			continue
		}
		target, ok := job.Target.Value()
		if !ok {
			continue
		}
		for _, b := range benches {
			if target.Thing == b {
				return true
			}
		}
	}
	return false
}

// ThroughputWindow is one order's rolling calibration window. It is memory
// only: a world change empties it.
type ThroughputWindow struct {
	Open       bool
	Start      int64
	StartStock int64
	// Deficit is the units short of target at Start; PerDay the predicted
	// units per day of the Carrying benches.
	Deficit  int64
	PerDay   float64
	Carrying int
	// Met is set once the stock reached target in the window: a stopped bill
	// is not a shortfall.
	Met bool
	// Samples counts Rounds sampled; Busy those a pawn worked a carrying
	// bench; Inactive is set when a carrying bill was not known active.
	Samples, Busy int
	Inactive      bool
}

// ThroughputObservation is one Round's reading of an order.
type ThroughputObservation struct {
	Tick   int64
	Stock  domain.Fact[int64]
	Target int64
	// Carrying is the benches with a standing bill of the order; Eligible
	// the benches that could carry one.
	Carrying, Eligible int
	// Capacity is units per day of one bench.
	Capacity domain.Fact[float64]
	// Active: every carrying bill is known active. Busy: a pawn works a
	// carrying bench this Round.
	Active, Busy bool
	// Ingredients: the recipe's ingredient stock is above IngredientBoundStock
	// (known true), at or below it (known false) or unread.
	Ingredients domain.Fact[bool]
}

// Calibration is a closed window's verdict.
type Calibration struct {
	// Closed: a full window ended this Round. Short: its stock gain was under
	// ShortfallFraction of the prediction.
	Closed, Short bool
	// Widen: one more bench should carry the order. Otherwise a Short window
	// has Reason.
	Widen  bool
	Reason UnmetReason
	// ShortPerDay is the predicted minus measured gain per day.
	ShortPerDay float64
}

// CalibrateThroughput advances the order's window with this Round's reading.
// The window restarts whenever the carrying count changes, and only a full
// window gives a verdict. A short window widens by one bench unless the
// shortfall is ingredient-bound (a known ingredient stock at or below
// IngredientBoundStock) or haul-bound (every carrying bill active, ingredients
// present, yet no pawn worked a carrying bench all window); a short window
// with a bill on every eligible bench is UnmetBenchesExhausted.
func CalibrateThroughput(w ThroughputWindow, o ThroughputObservation) (ThroughputWindow, Calibration) {
	stock, stockKnown := o.Stock.Value()
	capacity, capKnown := o.Capacity.Value()
	if !stockKnown || !capKnown || o.Carrying == 0 {
		return ThroughputWindow{}, Calibration{}
	}
	fresh := func() ThroughputWindow {
		deficit := max(o.Target-stock, 0)
		return ThroughputWindow{Open: true, Start: o.Tick, StartStock: stock, Deficit: deficit, PerDay: capacity * float64(o.Carrying), Carrying: o.Carrying, Met: stock >= o.Target, Samples: 0}
	}
	if !w.Open || w.Carrying != o.Carrying || o.Tick < w.Start {
		return fresh(), Calibration{}
	}
	w.Met = w.Met || stock >= o.Target
	w.Samples++
	if o.Busy {
		w.Busy++
	}
	if !o.Active {
		w.Inactive = true
	}
	if o.Tick-w.Start < ThroughputWindowTicks {
		return w, Calibration{}
	}
	days := float64(o.Tick-w.Start) / float64(domain.TicksPerDay)
	expected := min(w.PerDay*days, float64(w.Deficit))
	cal := Calibration{Closed: true}
	gain := float64(stock - w.StartStock)
	if !w.Met && expected > 0 && gain < ShortfallFraction*expected {
		cal.Short = true
		cal.ShortPerDay = (expected - gain) / days
		ingredients, ingredientsKnown := o.Ingredients.Value()
		switch {
		case ingredientsKnown && !ingredients:
			cal.Reason = UnmetIngredients
		case ingredientsKnown && !w.Inactive && w.Samples > 0 && w.Busy == 0:
			cal.Reason = UnmetHaul
		case o.Carrying < o.Eligible:
			cal.Widen = true
		default:
			cal.Reason = UnmetBenchesExhausted
		}
	}
	return fresh(), cal
}

// UnplacedUnmet is the throughput lost to orders no bench could take: with a
// capable bench (all at the slot cap) the reason is UnmetSlotsFull, otherwise
// UnmetNoBench. The shortfall is one bench's capacity, or the whole demand
// when no bench is capable; zero when the model cannot size it.
func UnplacedUnmet(unplaced []OrderSpec, benches []GearBench, dispatch map[string]OrderDispatch) []UnmetThroughput {
	var out []UnmetThroughput
	for _, order := range unplaced {
		d := dispatch[order.Key()]
		u := UnmetThroughput{BenchKind: order.BenchKind, Reason: UnmetSlotsFull}
		if len(EligibleBenches(order, benches)) == 0 {
			u.Reason = UnmetNoBench
			u.ShortPerDay, _ = d.Demand.Value()
		} else {
			u.ShortPerDay, _ = d.Capacity.Value()
		}
		out = append(out, u)
	}
	return out
}

// MergeUnmet is one row per bench kind, sorted by kind: the shortfalls summed
// and the reason of the largest contributor (ties by reason name). A row short
// by zero (the model could not size it) still carries its reason.
func MergeUnmet(items []UnmetThroughput) []UnmetThroughput {
	type acc struct {
		sum, top float64
		reason   UnmetReason
	}
	byKind := map[string]*acc{}
	for _, u := range items {
		a := byKind[u.BenchKind]
		if a == nil {
			a = &acc{top: -1}
			byKind[u.BenchKind] = a
		}
		a.sum += u.ShortPerDay
		if u.ShortPerDay > a.top || (u.ShortPerDay == a.top && u.Reason < a.reason) {
			a.top, a.reason = u.ShortPerDay, u.Reason
		}
	}
	kinds := make([]string, 0, len(byKind))
	for k := range byKind {
		kinds = append(kinds, k)
	}
	sort.Strings(kinds)
	out := make([]UnmetThroughput, 0, len(kinds))
	for _, k := range kinds {
		out = append(out, UnmetThroughput{BenchKind: k, ShortPerDay: byKind[k].sum, Reason: byKind[k].reason})
	}
	return out
}
