package policy

import (
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SupplyDemand is one good the colony needs delivered. A flow demand (PerDay)
// is a rate to sustain, such as nutrition; a stock demand (Units) is a deficit
// to cover once, such as a resource floor. At most one is positive; a demand
// with neither is satisfied and matches nothing.
type SupplyDemand struct {
	Good     ResourceKey
	PerDay   float64
	Units    int64
	Priority int // 1..100; the higher serves first and wins urgent-work contests.
	// HorizonDays is the longest lead a candidate may have and still serve the
	// demand; 0 is a floor wanted now. It is also the window an upfront cost is
	// amortised over (at least one day).
	HorizonDays float64
	// Emergency opens the demand's candidates breadth first, one per kind
	// before a second of any, and credits a candidate only once it delivers.
	Emergency bool
	// Cover is reported as the target_cover term (nutrition's target coverage).
	Cover float64
}

// NutritionPriority ranks nutrition above every resource deficit.
const NutritionPriority = 100

// NutritionKey is the demand and yield key of nutrition.
var NutritionKey = ResourceKey{Def: CandidateNutrition}

// NutritionDemandInput is the food forecast's demand model.
type NutritionDemandInput struct {
	Forecast                         FoodForecast
	ReserveDays, MinDays, TargetDays float64
	// EmergencyDays is the stage's starvation line (FootholdFoodDays); zero
	// disables the emergency.
	EmergencyDays float64
}

// NutritionDemand is the nutrition flow demand. Usable runway is
// max(0, forecast runway - reserve). Below MinDays the horizon is that usable
// runway; otherwise it is at least TargetDays, so slower capacity can be
// established. Target coverage is 1 + max(0, TargetDays-usable)/TargetDays:
// replenish the missing buffer over one target window while also feeding
// consumers.
func NutritionDemand(in NutritionDemandInput) (SupplyDemand, error) {
	runway, known := in.Forecast.RunwayDays.Value()
	if !known || !foodNumber(runway) || !foodNumber(in.ReserveDays) || !foodNumber(in.MinDays) || !foodNumber(in.TargetDays) ||
		in.TargetDays <= in.MinDays || len(in.Forecast.Consumers) == 0 {
		return SupplyDemand{}, ErrSupplyPlanFacts
	}
	consumers := map[PawnID]bool{}
	perDay := 0.0
	for _, c := range in.Forecast.Consumers {
		if !foodID(string(c.ID)) || consumers[c.ID] || !foodNumber(c.NutritionPerDay) || !foodNumber(c.RunwayDays) || !foodNumber(c.UsableNutrition) || !foodNumber(c.AllocatedNutrition) {
			return SupplyDemand{}, ErrSupplyPlanFacts
		}
		consumers[c.ID] = true
		perDay += c.NutritionPerDay
	}
	f := in.Forecast
	if !foodNumber(perDay) || perDay == 0 || !foodNumber(f.UsableNutrition) || !foodNumber(f.AtRiskNutrition) || !foodNumber(f.InventoryNutrition) {
		return SupplyDemand{}, ErrSupplyPlanFacts
	}
	usable := math.Max(0, runway-in.ReserveDays)
	cover := 1 + math.Max(0, in.TargetDays-usable)/in.TargetDays
	target := perDay * cover
	if !foodNumber(target) {
		return SupplyDemand{}, ErrSupplyPlanFacts
	}
	horizon := usable
	if usable >= in.MinDays {
		horizon = math.Max(horizon, in.TargetDays)
	}
	return SupplyDemand{Good: NutritionKey, PerDay: target, Priority: NutritionPriority, HorizonDays: horizon,
		Emergency: in.EmergencyDays > 0 && runway < in.EmergencyDays, Cover: cover}, nil
}

// SupplyDemandOfResource is a resource deficit as a stock demand wanted now.
func SupplyDemandOfResource(d ResourceDemand) SupplyDemand {
	return SupplyDemand{Good: d.Key, Units: d.Count, Priority: d.Priority}
}

// SupplyPlanRequest is the input of PlanSupply. Labor is the shared budget in
// work ticks per day (workers * 20000). UrgentPriority is competing urgent
// work: it holds a candidate whose best matched demand ranks below it.
type SupplyPlanRequest struct {
	Demands        domain.Fact[[]SupplyDemand]
	Candidates     domain.Fact[[]SupplyCandidate]
	Labor          domain.Fact[float64]
	UrgentPriority int
}

type SupplyDecision string

const (
	SupplyOpen  SupplyDecision = "Open"
	SupplyHold  SupplyDecision = "Hold"
	SupplyClose SupplyDecision = "Close"
)

// SupplyCredit is the contribution admitted toward one demand: risk-adjusted
// units per day for a flow, units for a stock demand.
type SupplyCredit struct {
	Good   ResourceKey
	Amount float64
}

// SupplyEntry is the decision for one candidate. Score, Wanted, Trips and
// Value price the candidate against the full demands, independent of the
// other candidates: covered value per labor-equivalent cost.
type SupplyEntry struct {
	Candidate SupplyCandidate
	// Hold preserves current state: keep a delivering candidate or defer a closed one.
	Decision SupplyDecision
	Reason   string
	Terms    []CandidateTerm
	Credit   []SupplyCredit
	Score    float64
	Wanted   int64
	Trips    int64
	Value    float64
}

// SupplyDemandResult reports what the plan admits against one demand.
type SupplyDemandResult struct {
	Demand    SupplyDemand
	Delivered float64
	Gap       float64
}

// SupplyPlan lists the portfolio in rank order and, apart, the candidates
// with unknown facts.
type SupplyPlan struct {
	Demands            []SupplyDemandResult
	Portfolio, Unknown []SupplyEntry
}

var ErrSupplyPlanFacts = errors.New("supply plan inputs unavailable or invalid")

// Delivered is what the plan admits toward a good, summed over its demands.
func (p SupplyPlan) Delivered(good ResourceKey) float64 {
	total := 0.0
	for _, d := range p.Demands {
		if d.Demand.Good == good {
			total += d.Delivered
		}
	}
	return total
}

// Gap is what a good's demands still lack after admission.
func (p SupplyPlan) Gap(good ResourceKey) float64 {
	total := 0.0
	for _, d := range p.Demands {
		if d.Demand.Good == good {
			total += d.Gap
		}
	}
	return total
}

func (p SupplyPlan) Explain() string {
	var b strings.Builder
	for _, d := range p.Demands {
		fmt.Fprintf(&b, "%s delivered=%.4f gap=%.4f\n", d.Demand.Good.Def, d.Delivered, d.Gap)
	}
	for _, rows := range [][]SupplyEntry{p.Portfolio, p.Unknown} {
		for _, e := range rows {
			fmt.Fprintf(&b, " %s/%s %s: %s", e.Candidate.Kind, e.Candidate.ID, e.Decision, e.Reason)
			for _, c := range e.Credit {
				fmt.Fprintf(&b, " credit[%s]=%.4f", c.Good.Def, c.Amount)
			}
			for _, t := range e.Terms {
				fmt.Fprintf(&b, " %s=%.4f", t.Name, t.Value)
			}
			b.WriteByte('\n')
		}
	}
	return strings.TrimRight(b.String(), "\n")
}

// supplyCand is a validated candidate with the numbers the ranker reads.
type supplyCand struct {
	c                               SupplyCandidate
	yields                          []CandidateYield // canonical order
	open                            bool
	lead, work, upfront, risk, dist float64
	distSq                          domain.Fact[float64]
	full                            supplyShare // priced against the full demands
	ok                              []bool      // per demand: lead within horizon
	flow                            bool        // matches a flow demand
	priority                        int
	laborNew                        float64
	cost                            float64 // labor-equivalent per covered value; +Inf covers nothing
	credited                        []float64
	entry                           SupplyEntry
}

// supplyShare is what a candidate covers of the demands.
type supplyShare struct {
	credit      []float64 // per demand, risk-adjusted
	matched     []bool    // per demand: a yield matches an active demand
	wanted      int64
	trips       int64
	value       float64
	priority    int
	unitMatched bool // a stock demand with units left matched a yield holding some
}

func validCandidateKind(k CandidateKind) bool {
	switch k {
	case CandidateForage, CandidateHunt, CandidateSlaughter, CandidateCrop, CandidateAnimalProduct, CandidateFishing, CandidateTrade,
		CandidateCorpse, CandidateReserve, CandidateCook, CandidateLoot, CandidateSalvage, CandidateMining, CandidateProduce,
		CandidateDeepDrill, CandidateChop, CandidateHarvest, CandidateTame, CandidateAnimalBuy:
		return true
	}
	return false
}

func validCandidateRisk(k CandidateRiskKind) bool {
	switch k {
	case CandidateBlight, CandidateFallout, CandidateFrost, CandidatePower, CandidateRevenge:
		return true
	}
	return false
}

func goodName(k ResourceKey) string { return strings.ToLower(string(k.Def)) }

func demandMatches(d SupplyDemand, y CandidateYield) bool {
	return d.Good.Def == y.Good.Def && (d.Good.Stuff == "" || d.Good.Stuff == y.Good.Stuff)
}

func (d SupplyDemand) active() bool { return d.PerDay > 0 || d.Units > 0 }
func (d SupplyDemand) flow() bool   { return d.PerDay > 0 }
func (d SupplyDemand) window() float64 {
	return math.Max(1, d.HorizonDays)
}

func supplyLess(a, b SupplyCandidate) bool {
	if a.Kind != b.Kind {
		return a.Kind < b.Kind
	}
	return a.ID < b.ID
}

// PlanSupply is the one ranker over a demand vector and unified candidates. It
// budgets projected rates and unit deficits, never stored goods or completed
// work.
//
// A candidate serves a demand when a yield matches it and the candidate's lead
// is within the demand's horizon. Its contribution to a flow is its
// risk-adjusted rate, `rate * max(0, 1 - sum(risk))`, bounded by its stock cap
// spread over the demand's window; to a stock demand a one-shot source
// contributes min(stock cap, storage headroom when hauled, remaining deficit).
// An upfront cost is charged as labor over the demand's window. Labor is
// charged once however many demands a candidate serves, and the budget is
// shared by all demands.
//
// Order: flow demands (nutrition) before stock demands. Within a flow demand lead is
// the primary key (a channel arriving before the runway expires beats a
// cheaper later one), then fishing distance, then labor per unit delivered.
// Within stock demands the key is the score
// Value / (1 + labor + distance*(1+2*trips) + trips), then lead, kind, id. An
// emergency demand re-ranks its candidates breadth first by kind.
//
// Delivering candidates keep their observed contribution even over the labor
// budget (the excess is a Hold with a labor_excess term), and a strict surplus
// closes the least efficient first. Closed candidates over budget add nothing.
func PlanSupply(r SupplyPlanRequest) (SupplyPlan, error) {
	fail := func() (SupplyPlan, error) { return SupplyPlan{}, ErrSupplyPlanFacts }
	rows, known := r.Candidates.Value()
	labor, lk := r.Labor.Value()
	demands, dk := r.Demands.Value()
	if !known || !lk || !foodNumber(labor) || r.UrgentPriority < 0 || r.UrgentPriority > 100 {
		return fail()
	}
	demands = append([]SupplyDemand(nil), demands...)
	sort.Slice(demands, func(i, j int) bool { return acquisitionKeyLess(demands[i].Good, demands[j].Good) })
	for i, d := range demands {
		if !validAcquisitionKey(d.Good) || !foodNumber(d.PerDay) || !validAcquisitionCount(d.Units) || d.PerDay > 0 && d.Units > 0 ||
			d.Priority < 1 || d.Priority > 100 || !foodNumber(d.HorizonDays) || !foodNumber(d.Cover) || i > 0 && demands[i-1].Good == d.Good {
			return fail()
		}
	}
	remaining := make([]int64, len(demands))
	delivered := make([]float64, len(demands))
	for i, d := range demands {
		remaining[i] = d.Units
	}

	var p SupplyPlan
	var cands []*supplyCand
	identities := map[[2]string]bool{}
	for _, row := range rows {
		c, missing, err := newSupplyCand(row, identities)
		if err != nil {
			return fail()
		}
		if len(missing) > 0 || !dk {
			c.entry.Reason = "unknown: " + strings.Join(missing, ", ")
			if !dk {
				c.entry.Reason = "unknown_demand_or_cost"
			}
			p.Unknown = append(p.Unknown, c.entry)
			continue
		}
		c.price(demands)
		cands = append(cands, c)
	}
	sort.Slice(p.Unknown, func(i, j int) bool { return supplyLess(p.Unknown[i].Candidate, p.Unknown[j].Candidate) })
	sort.Slice(cands, func(i, j int) bool { return cands[i].before(cands[j]) })
	cands = breadthFirst(cands, demands)

	used := 0.0
	var openOrder []*supplyCand
	for _, c := range cands {
		if c.open {
			c.entry.Reason = "already delivering"
			c.admit(c.share(demands, remaining), false, demands, remaining, delivered)
			used += c.work
			openOrder = append(openOrder, c)
		}
	}
	if !foodNumber(used) {
		return fail()
	}
	sort.SliceStable(openOrder, func(i, j int) bool { return openOrder[i].cost > openOrder[j].cost })
	for _, c := range openOrder {
		if c.surplus(demands, delivered) {
			for i, amount := range c.credited {
				delivered[i] -= amount
			}
			c.entry.Decision, c.entry.Reason, c.entry.Credit = SupplyClose, "surplus", nil
			used -= c.work
		}
	}
	for _, c := range cands {
		if c.open {
			if c.entry.Decision != SupplyClose && used > labor {
				c.entry.Terms = append(c.entry.Terms, CandidateTerm{"labor_excess", used - labor})
			}
		} else {
			used += c.consider(demands, remaining, delivered, used, labor, r.UrgentPriority)
		}
		p.Portfolio = append(p.Portfolio, c.entry)
	}
	for i, d := range demands {
		res := SupplyDemandResult{Demand: d, Delivered: delivered[i]}
		res.Gap = d.PerDay - delivered[i]
		if !d.flow() {
			res.Gap = float64(remaining[i])
		}
		if !foodNumber(res.Delivered) || math.IsInf(res.Gap, 0) || math.IsNaN(res.Gap) {
			return fail()
		}
		p.Demands = append(p.Demands, res)
	}
	return p, nil
}

// newSupplyCand validates a candidate and reads its facts; missing names the
// unknown ones.
func newSupplyCand(row SupplyCandidate, identities map[[2]string]bool) (*supplyCand, []string, error) {
	bad := errors.New("invalid supply candidate")
	id := [2]string{string(row.Kind), row.ID}
	if !validCandidateKind(row.Kind) || !foodID(row.ID) || identities[id] || len(row.Yields) == 0 ||
		!validAcquisitionCount(row.UnitsPerTrip) || row.NeedsHaul && row.UnitsPerTrip == 0 {
		return nil, nil, bad
	}
	identities[id] = true
	num := func(f domain.Fact[float64], max float64) (float64, bool, error) {
		v, ok := f.Value()
		if ok && (!foodNumber(v) || v > max) {
			return 0, false, bad
		}
		return v, ok, nil
	}
	lead, leadKnown, err1 := num(row.LeadDays, math.MaxFloat64)
	work, workKnown, err2 := num(row.LaborPerDay, math.MaxFloat64)
	upfront, upKnown, err3 := num(row.UpfrontCost.LaborTicks, 1e12)
	dist, pathKnown, err4 := num(row.PathDistance, 1e12)
	_, _, err5 := num(row.DistanceSquared, math.MaxFloat64)
	if err := errors.Join(err1, err2, err3, err4, err5); err != nil {
		return nil, nil, bad
	}
	for _, q := range row.UpfrontCost.Resources {
		if !validAcquisitionKey(q.Key) || !validAcquisitionCount(q.Count) {
			return nil, nil, bad
		}
	}
	own := row
	own.Yields = append([]CandidateYield(nil), row.Yields...)
	own.Risk = append([]CandidateRisk(nil), row.Risk...)
	own.Terms = append([]CandidateTerm(nil), row.Terms...)
	own.Prey = append([]string(nil), row.Prey...)
	own.UpfrontCost.Resources = append([]ResourceQuantity(nil), row.UpfrontCost.Resources...)
	yields := append([]CandidateYield(nil), row.Yields...)
	sort.Slice(yields, func(i, j int) bool { return acquisitionKeyLess(yields[i].Good, yields[j].Good) })
	var missing []string
	for i, y := range yields {
		_, rateKnown, err := num(y.PerDay, math.MaxFloat64)
		if err != nil {
			return nil, nil, bad
		}
		stock, capKnown := y.StockCap.Value()
		headroom, roomKnown := y.Headroom.Value()
		if !validAcquisitionKey(y.Good) || capKnown && !validAcquisitionCount(stock) || roomKnown && !validAcquisitionCount(headroom) ||
			!finiteAcquisitionCost(y.UnitValue) || i > 0 && yields[i-1].Good == y.Good {
			return nil, nil, bad
		}
		if !rateKnown && !capKnown {
			missing = append(missing, goodName(y.Good)+"_per_day")
		}
	}
	if !workKnown && !upKnown {
		missing = append(missing, "work_per_day")
	}
	if !leadKnown {
		missing = append(missing, "lead_days")
	}
	state, stateKnown := row.State.Value()
	if !stateKnown {
		missing = append(missing, "open")
	}
	// A one-shot source prices its distance; a steady flow is priced by labor.
	if !pathKnown && (row.NeedsHaul || !workKnown) {
		missing = append(missing, "path_distance")
	}
	risk := 0.0
	risks := map[CandidateRiskKind]bool{}
	for _, v := range row.Risk {
		if !validCandidateRisk(v.Kind) || risks[v.Kind] || !foodNumber(v.Weight) || v.Weight > 1 {
			return nil, nil, bad
		}
		risks[v.Kind] = true
		risk += v.Weight
	}
	for _, v := range row.Terms {
		if !foodID(v.Name) || math.IsNaN(v.Value) || math.IsInf(v.Value, 0) {
			return nil, nil, bad
		}
	}
	c := &supplyCand{c: own, yields: yields, open: state == CandidateDelivering, lead: lead, work: work, upfront: upfront, risk: risk, dist: dist, distSq: row.DistanceSquared}
	c.entry = SupplyEntry{Candidate: own, Decision: SupplyHold, Terms: append([]CandidateTerm(nil), row.Terms...)}
	return c, missing, nil
}

// rate is a yield's risk-adjusted steady contribution toward a flow demand.
func (c *supplyCand) rate(y CandidateYield, d SupplyDemand) float64 {
	rate, rateKnown := y.PerDay.Value()
	stock, capKnown := y.StockCap.Value()
	switch {
	case rateKnown && capKnown:
		rate = math.Min(rate, float64(stock)/d.window())
	case capKnown:
		rate = float64(stock) / d.window()
	}
	return rate * math.Max(0, 1-c.risk)
}

// units is what a yield can deliver toward a stock demand.
func (c *supplyCand) units(y CandidateYield, d SupplyDemand) int64 {
	if stock, capped := y.StockCap.Value(); capped {
		return stock
	}
	rate, _ := y.PerDay.Value()
	return int64(math.Floor(rate*math.Max(0, 1-c.risk)*math.Max(0, d.window()-c.lead) + 1e-9))
}

// share prices the candidate against demands with remaining units left on
// each stock demand. Lead is not applied: ok says where it is within horizon.
func (c *supplyCand) share(demands []SupplyDemand, remaining []int64) supplyShare {
	s := supplyShare{credit: make([]float64, len(demands)), matched: make([]bool, len(demands))}
	left := append([]int64(nil), remaining...)
	for _, y := range c.yields {
		used := int64(0)
		for i, d := range demands {
			if !d.active() || !demandMatches(d, y) {
				continue
			}
			s.matched[i] = true
			if d.flow() {
				amount := c.rate(y, d)
				s.credit[i] += amount
				s.value += amount * float64(d.Priority) * (1 + y.UnitValue)
				if amount > 0 {
					s.priority = max(s.priority, d.Priority)
				}
				continue
			}
			capacity := c.units(y, d)
			if capacity > 0 && left[i] > 0 {
				s.unitMatched = true
			}
			if c.c.NeedsHaul {
				room, _ := y.Headroom.Value()
				capacity = min(capacity, room)
			}
			count := min(capacity-used, left[i])
			if count <= 0 {
				continue
			}
			left[i] -= count
			used += count
			s.credit[i] += float64(count)
			s.wanted += count
			s.priority = max(s.priority, d.Priority)
			s.value += float64(count) * float64(d.Priority) * (1 + y.UnitValue)
		}
		if c.c.NeedsHaul && used > 0 {
			s.trips += 1 + (used-1)/c.c.UnitsPerTrip
		}
	}
	return s
}

// price fills the candidate's rank keys and explain terms from the full demands.
func (c *supplyCand) price(demands []SupplyDemand) {
	remaining := make([]int64, len(demands))
	for i, d := range demands {
		remaining[i] = d.Units
	}
	c.full = c.share(demands, remaining)
	c.ok = make([]bool, len(demands))
	window, covered := 1.0, 0.0
	for i, d := range demands {
		c.ok[i] = c.lead <= d.HorizonDays
		if !c.full.matched[i] {
			continue
		}
		c.priority = max(c.priority, d.Priority)
		window = math.Max(window, d.window())
		if d.flow() {
			c.flow = true
			covered += c.full.credit[i]
		}
	}
	c.laborNew = c.work + c.upfront/window
	haul := c.dist * (1 + 2*float64(c.full.trips))
	denominator := 1 + c.laborNew + haul + float64(c.full.trips)
	e := &c.entry
	e.Wanted, e.Trips, e.Value = c.full.wanted, c.full.trips, c.full.value
	e.Score = c.full.value / denominator
	c.cost = math.Inf(1)
	switch {
	case c.flow && covered > 0:
		c.cost = c.laborNew / covered
	case !c.flow && c.full.value > 0:
		c.cost = denominator / c.full.value
	}
	e.Terms = append(e.Terms, CandidateTerm{"risk_discount", math.Min(1, c.risk)})
	cover := 0.0
	for i, d := range demands {
		if !c.full.matched[i] {
			continue
		}
		suffix := "_units"
		if d.flow() {
			suffix = "_per_day"
			if cover == 0 {
				cover = d.Cover
			}
		}
		e.Terms = append(e.Terms, CandidateTerm{goodName(d.Good) + suffix, c.full.credit[i]})
	}
	e.Terms = append(e.Terms, CandidateTerm{"work_per_day", c.work}, CandidateTerm{"lead_days", c.lead})
	if cover > 0 {
		e.Terms = append(e.Terms, CandidateTerm{"target_cover", cover})
	}
}

func (c *supplyCand) before(o *supplyCand) bool {
	if c.flow != o.flow {
		return c.flow
	}
	if c.flow {
		if c.lead != o.lead {
			return c.lead < o.lead
		}
		if c.c.Kind == CandidateFishing && o.c.Kind == CandidateFishing {
			da, ak := c.distSq.Value()
			db, bk := o.distSq.Value()
			if ak != bk {
				return ak
			}
			if da != db {
				return da < db
			}
		}
		if c.cost != o.cost {
			return c.cost < o.cost
		}
	} else {
		if c.entry.Score != o.entry.Score {
			return c.entry.Score > o.entry.Score
		}
		if c.lead != o.lead {
			return c.lead < o.lead
		}
	}
	return supplyLess(c.c, o.c)
}

// breadthFirst reorders the candidates serving an emergency demand, within
// their own slots: the best of every kind, then the second, and so on.
func breadthFirst(cands []*supplyCand, demands []SupplyDemand) []*supplyCand {
	var slots []int
	for i, c := range cands {
		for k, d := range demands {
			if d.Emergency && d.flow() && c.full.matched[k] {
				slots = append(slots, i)
				break
			}
		}
	}
	rank := make([]int, len(slots))
	kinds := map[CandidateKind]int{}
	for n, i := range slots {
		rank[n] = kinds[cands[i].c.Kind]
		kinds[cands[i].c.Kind]++
	}
	order := make([]int, len(slots))
	for n := range order {
		order[n] = n
	}
	sort.SliceStable(order, func(a, b int) bool { return rank[order[a]] < rank[order[b]] })
	out := append([]*supplyCand(nil), cands...)
	for n, k := range order {
		out[slots[n]] = cands[slots[k]]
	}
	return out
}

// admit credits the share toward the demands. Under an emergency a candidate
// that is not yet delivering is not food until delivered, so it covers nothing.
func (c *supplyCand) admit(s supplyShare, opening bool, demands []SupplyDemand, remaining []int64, delivered []float64) {
	c.credited = make([]float64, len(demands))
	for i, d := range demands {
		amount := s.credit[i]
		if amount == 0 || opening && !c.ok[i] {
			continue
		}
		if opening && d.Emergency && d.flow() {
			c.entry.Terms = append(c.entry.Terms, CandidateTerm{"uncredited_until_delivered", amount})
			continue
		}
		c.credited[i] = amount
		delivered[i] += amount
		if !d.flow() {
			remaining[i] -= int64(amount)
		}
		c.entry.Credit = append(c.entry.Credit, SupplyCredit{Good: d.Good, Amount: amount})
	}
}

// surplus holds when every demand the candidate feeds is a flow that stays
// strictly above its target without it: closure needs a surplus greater than
// the contribution, which prevents churn at the boundary.
func (c *supplyCand) surplus(demands []SupplyDemand, delivered []float64) bool {
	feeds := false
	for i, amount := range c.credited {
		if amount == 0 {
			continue
		}
		feeds = true
		if !demands[i].flow() || delivered[i]-demands[i].PerDay <= amount {
			return false
		}
	}
	return feeds
}

// consider decides a closed candidate and returns the labor it takes.
func (c *supplyCand) consider(demands []SupplyDemand, remaining []int64, delivered []float64, used, labor float64, urgent int) float64 {
	e, f := &c.entry, c.full
	total, flow, anyOK := 0.0, false, false
	for i, d := range demands {
		total += f.credit[i]
		flow = flow || f.matched[i] && d.flow()
		anyOK = anyOK || f.matched[i] && c.ok[i]
	}
	switch {
	case total == 0 && flow:
		e.Reason = "no risk-adjusted " + goodName(c.flowGood(demands))
		return 0
	case total == 0 && f.unitMatched:
		e.Reason = "no_storage_headroom"
		return 0
	case total == 0:
		e.Reason = "no_demand"
		return 0
	case !anyOK:
		e.Reason = "lead exceeds runway"
		return 0
	case urgent > f.priority:
		e.Reason = "competing_urgent_work"
		return 0
	}
	share := c.share(demands, remaining)
	needs, good := false, ""
	for i, d := range demands {
		if f.matched[i] && c.ok[i] && share.credit[i] > 0 && (!d.flow() || delivered[i] < d.PerDay) {
			if !needs {
				good = goodName(d.Good)
			}
			needs = true
		}
	}
	if !needs {
		e.Reason = "target covered"
		return 0
	}
	if free := math.Max(0, labor-used); c.laborNew > free {
		e.Reason = "labor budget"
		e.Terms = append(e.Terms, CandidateTerm{"labor_excess", c.laborNew - free})
		return 0
	}
	e.Decision, e.Reason = SupplyOpen, "close "+good+" gap"
	c.admit(share, true, demands, remaining, delivered)
	return c.laborNew
}

func (c *supplyCand) flowGood(demands []SupplyDemand) ResourceKey {
	for i, d := range demands {
		if c.full.matched[i] && d.flow() {
			return d.Good
		}
	}
	return ResourceKey{}
}
