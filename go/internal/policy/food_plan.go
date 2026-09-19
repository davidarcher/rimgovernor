package policy

import (
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type FoodChannelKind string

const (
	FoodForage        FoodChannelKind = "Forage"
	FoodHunt          FoodChannelKind = "Hunt"
	FoodCrop          FoodChannelKind = "Crop"
	FoodAnimalProduct FoodChannelKind = "AnimalProduct"
	FoodFishing       FoodChannelKind = "Fishing"
	FoodTrade         FoodChannelKind = "Trade"
	FoodCorpse        FoodChannelKind = "Corpse"
	FoodReserve       FoodChannelKind = "Reserve"
	FoodCook          FoodChannelKind = "Cook"
)

type FoodRiskKind string

const (
	FoodBlight  FoodRiskKind = "Blight"
	FoodFallout FoodRiskKind = "Fallout"
	FoodFrost   FoodRiskKind = "Frost"
	FoodPower   FoodRiskKind = "Power"
	FoodRevenge FoodRiskKind = "Revenge"
)

type FoodRisk struct {
	Kind   FoodRiskKind
	Weight float64 // [0,1]; summed risks discount nutrition, clamped at 100%.
}

type FoodPlanTerm struct {
	Name  string
	Value float64
}

type FoodChannel struct {
	// Contributions must be independent: a Cook row describes incremental
	// nutrition gained by conversion, not the raw input counted by another row.
	Kind                                  FoodChannelKind
	ID                                    string
	NutritionPerDay, WorkPerDay, LeadDays domain.Fact[float64]
	Risk                                  []FoodRisk
	Open                                  domain.Fact[bool]
	Terms                                 []FoodPlanTerm
}

type FoodPlanRequest struct {
	Demand                           FoodForecast
	ReserveDays, MinDays, TargetDays float64
	Channels                         domain.Fact[[]FoodChannel]
	Labor                            domain.Fact[float64]
}

type FoodPlanDecision string

const (
	FoodPlanOpen  FoodPlanDecision = "Open"
	FoodPlanHold  FoodPlanDecision = "Hold"
	FoodPlanClose FoodPlanDecision = "Close"
)

type FoodPlanEntry struct {
	Channel FoodChannel
	// Hold preserves current state: keep an open channel or defer a closed one.
	Decision        FoodPlanDecision
	Reason          string
	Terms           []FoodPlanTerm
	DeliveredPerDay float64 // Risk-adjusted contribution admitted to this plan.
}

type FoodPlan struct {
	Portfolio, Unknown                       []FoodPlanEntry
	DeliveredPerDay, DemandPerDay, GapPerDay float64
}

var ErrFoodPlanFacts = errors.New("food plan inputs unavailable or invalid")

// PlanFood budgets projected rates, never stored nutrition or completed work.
// Usable runway is max(0, forecast runway - reserve). Below the minimum it
// admits only channels arriving before that runway expires. Otherwise the lead
// horizon is at least TargetDays, allowing slower capacity to be established.
// Target coverage is 1 + max(0, TargetDays-usable runway)/TargetDays: replenish
// the missing buffer over one target window while also feeding consumers.
// GapPerDay is this covered demand minus admitted, risk-adjusted delivery.
// Open channels retain their observed contribution even over the labor budget;
// the excess is Hold with a labor term. Closed channels over budget add nothing.
func PlanFood(r FoodPlanRequest) (FoodPlan, error) {
	fail := func() (FoodPlan, error) { return FoodPlan{}, ErrFoodPlanFacts }
	rows, known := r.Channels.Value()
	labor, lk := r.Labor.Value()
	runway, rk := r.Demand.RunwayDays.Value()
	if !known || !lk || !rk || !foodNumber(labor) || !foodNumber(runway) ||
		!foodNumber(r.ReserveDays) || !foodNumber(r.MinDays) || !foodNumber(r.TargetDays) || r.TargetDays <= r.MinDays ||
		len(rows) > 4096 || len(r.Demand.Consumers) == 0 || len(r.Demand.Consumers) > 256 {
		return fail()
	}
	p := FoodPlan{}
	consumers := map[PawnID]bool{}
	for _, c := range r.Demand.Consumers {
		if !foodID(string(c.ID)) || consumers[c.ID] || !foodNumber(c.NutritionPerDay) || !foodNumber(c.RunwayDays) || !foodNumber(c.UsableNutrition) || !foodNumber(c.AllocatedNutrition) {
			return fail()
		}
		consumers[c.ID] = true
		p.DemandPerDay += c.NutritionPerDay
	}
	if !foodNumber(p.DemandPerDay) || p.DemandPerDay == 0 || !foodNumber(r.Demand.UsableNutrition) || !foodNumber(r.Demand.AtRiskNutrition) || !foodNumber(r.Demand.InventoryNutrition) {
		return fail()
	}
	runway = math.Max(0, runway-r.ReserveDays)
	cover := 1 + math.Max(0, r.TargetDays-runway)/r.TargetDays
	target := p.DemandPerDay * cover
	if !foodNumber(target) {
		return fail()
	}
	horizon := runway
	if runway >= r.MinDays {
		horizon = math.Max(horizon, r.TargetDays)
	}
	type candidate struct {
		entry                 FoodPlanEntry
		nutrition, work, lead float64
		open                  bool
	}
	var candidates []candidate
	type key struct {
		kind FoodChannelKind
		id   string
	}
	seen := map[key]bool{}
	for _, c := range rows {
		k := key{c.Kind, c.ID}
		if !validFoodChannelKind(c.Kind) || !foodID(c.ID) || seen[k] || len(c.Risk) > 5 || len(c.Terms) > 64 {
			return fail()
		}
		seen[k] = true
		var missing []string
		for _, f := range []struct {
			name string
			fact domain.Fact[float64]
		}{{"nutrition_per_day", c.NutritionPerDay}, {"work_per_day", c.WorkPerDay}, {"lead_days", c.LeadDays}} {
			v, ok := f.fact.Value()
			if ok && !foodNumber(v) {
				return fail()
			}
			if !ok {
				missing = append(missing, f.name)
			}
		}
		open, ok := c.Open.Value()
		if !ok {
			missing = append(missing, "open")
		}
		risk := 0.0
		risks := map[FoodRiskKind]bool{}
		for _, v := range c.Risk {
			if !validFoodRisk(v.Kind) || risks[v.Kind] || !foodNumber(v.Weight) || v.Weight > 1 {
				return fail()
			}
			risks[v.Kind] = true
			risk += v.Weight
		}
		for _, v := range c.Terms {
			if !foodID(v.Name) || math.IsNaN(v.Value) || math.IsInf(v.Value, 0) {
				return fail()
			}
		}
		c.Risk = append([]FoodRisk(nil), c.Risk...)
		c.Terms = append([]FoodPlanTerm(nil), c.Terms...)
		e := FoodPlanEntry{Channel: c, Decision: FoodPlanHold, Terms: append([]FoodPlanTerm(nil), c.Terms...)}
		if len(missing) > 0 {
			e.Reason = "unknown: " + strings.Join(missing, ", ")
			p.Unknown = append(p.Unknown, e)
			continue
		}
		n, _ := c.NutritionPerDay.Value()
		w, _ := c.WorkPerDay.Value()
		lead, _ := c.LeadDays.Value()
		n *= math.Max(0, 1-risk)
		e.Terms = append(e.Terms, FoodPlanTerm{"risk_discount", math.Min(1, risk)}, FoodPlanTerm{"nutrition_per_day", n}, FoodPlanTerm{"work_per_day", w}, FoodPlanTerm{"lead_days", lead}, FoodPlanTerm{"target_cover", cover})
		candidates = append(candidates, candidate{e, n, w, lead, open})
	}
	lessID := func(a, b FoodChannel) bool {
		if a.Kind != b.Kind {
			return a.Kind < b.Kind
		}
		return a.ID < b.ID
	}
	// Cost per risk-adjusted nutrition; zero delivery always ranks last.
	cost := func(c candidate) float64 {
		if c.nutrition == 0 {
			return math.Inf(1)
		}
		return c.work / c.nutrition
	}
	sort.Slice(candidates, func(i, j int) bool {
		a, b := candidates[i], candidates[j]
		if a.lead != b.lead {
			return a.lead < b.lead
		}
		if cost(a) != cost(b) {
			return cost(a) < cost(b)
		}
		return lessID(a.entry.Channel, b.entry.Channel)
	})
	sort.Slice(p.Unknown, func(i, j int) bool { return lessID(p.Unknown[i].Channel, p.Unknown[j].Channel) })
	used := 0.0
	var openOrder []int
	for i := range candidates {
		c := &candidates[i]
		if c.open {
			c.entry.Reason = "already delivering"
			c.entry.DeliveredPerDay = c.nutrition
			p.DeliveredPerDay += c.nutrition
			used += c.work
			openOrder = append(openOrder, i)
		}
	}
	if !foodNumber(used) || !foodNumber(p.DeliveredPerDay) {
		return fail()
	}
	sort.SliceStable(openOrder, func(i, j int) bool { return cost(candidates[openOrder[i]]) > cost(candidates[openOrder[j]]) })
	for _, i := range openOrder {
		c := &candidates[i]
		if p.DeliveredPerDay-target > c.nutrition {
			c.entry.Decision, c.entry.Reason = FoodPlanClose, "surplus"
			c.entry.DeliveredPerDay = 0
			p.DeliveredPerDay -= c.nutrition
			used -= c.work
		}
	}
	for i := range candidates {
		c := &candidates[i]
		if c.open {
			if c.entry.Decision != FoodPlanClose && used > labor {
				c.entry.Terms = append(c.entry.Terms, FoodPlanTerm{"labor_excess", used - labor})
			}
		} else {
			switch {
			case c.nutrition == 0:
				c.entry.Reason = "no risk-adjusted nutrition"
			case c.lead > horizon:
				c.entry.Reason = "lead exceeds runway"
			case p.DeliveredPerDay >= target:
				c.entry.Reason = "target covered"
			case c.work > math.Max(0, labor-used):
				c.entry.Reason = "labor budget"
				c.entry.Terms = append(c.entry.Terms, FoodPlanTerm{"labor_excess", c.work - math.Max(0, labor-used)})
			default:
				c.entry.Decision, c.entry.Reason = FoodPlanOpen, "close nutrition gap"
				c.entry.DeliveredPerDay = c.nutrition
				p.DeliveredPerDay += c.nutrition
				used += c.work
			}
		}
		p.Portfolio = append(p.Portfolio, c.entry)
	}
	p.GapPerDay = target - p.DeliveredPerDay
	if !foodNumber(p.DeliveredPerDay) || math.IsInf(p.GapPerDay, 0) || math.IsNaN(p.GapPerDay) {
		return fail()
	}
	return p, nil
}

func validFoodChannelKind(k FoodChannelKind) bool {
	switch k {
	case FoodForage, FoodHunt, FoodCrop, FoodAnimalProduct, FoodFishing, FoodTrade, FoodCorpse, FoodReserve, FoodCook:
		return true
	}
	return false
}

func validFoodRisk(k FoodRiskKind) bool {
	switch k {
	case FoodBlight, FoodFallout, FoodFrost, FoodPower, FoodRevenge:
		return true
	}
	return false
}

func (p FoodPlan) Explain() string {
	var b strings.Builder
	fmt.Fprintf(&b, "delivered=%.4f demand=%.4f gap=%.4f", p.DeliveredPerDay, p.DemandPerDay, p.GapPerDay)
	for _, rows := range [][]FoodPlanEntry{p.Portfolio, p.Unknown} {
		for _, e := range rows {
			fmt.Fprintf(&b, "\n %s/%s %s: %s delivered=%.4f", e.Channel.Kind, e.Channel.ID, e.Decision, e.Reason, e.DeliveredPerDay)
			for _, t := range e.Terms {
				fmt.Fprintf(&b, " %s=%.4f", t.Name, t.Value)
			}
		}
	}
	return b.String()
}

// Seed acquisition estimates budget one collection/hunt cycle per day. They
// describe finite observed sources, not guaranteed renewable production; refresh
// the census each review. Work constants are policy estimates in pawn ticks,
// not native measurements. Designation alone does not prove delivery (Open).
const (
	FoodForageCycleDays = 1.0
	FoodHuntCycleDays   = 1.0
	FoodForageWorkTicks = 2500.0
	FoodHuntWorkTicks   = 7500.0
)

func ForageChannels(sources []AcquisitionSource) []FoodChannel {
	return acquisitionFoodChannels(sources, false)
}
func HuntChannels(sources []AcquisitionSource) []FoodChannel {
	return acquisitionFoodChannels(sources, true)
}

func acquisitionFoodChannels(sources []AcquisitionSource, hunt bool) []FoodChannel {
	var out []FoodChannel
	for _, s := range sources {
		if !s.Food || s.Tree || s.Hunt != hunt {
			continue
		}
		kind, cycle, work := FoodForage, FoodForageCycleDays, FoodForageWorkTicks
		if hunt {
			kind, cycle, work = FoodHunt, FoodHuntCycleDays, FoodHuntWorkTicks
		}
		c := FoodChannel{Kind: kind, ID: s.ID, NutritionPerDay: domain.Known(s.NutritionYield / cycle), WorkPerDay: domain.Known(work / cycle), LeadDays: domain.Known(0.0), Open: domain.Known(false), Terms: []FoodPlanTerm{{"estimated_cycle_days", cycle}, {"estimated_work_ticks", work}}}
		if hunt {
			c.Risk = []FoodRisk{{FoodRevenge, 0}}
		}
		out = append(out, c)
	}
	return out
}

// FoodField supplies the observations a FieldPlan does not own. ID identifies
// the field across reviews; remaining growth and work must not be guessed from
// the crop name. A newly planned field uses the full GrowDays as its remaining
// growth and Open=false. Unknown inputs remain unknown in the resulting row.
type FoodField struct {
	ID                            string
	Plan                          FieldPlan
	RemainingGrowDays, WorkPerDay domain.Fact[float64]
	Open                          domain.Fact[bool]
}

func CropChannels(fields []FoodField) []FoodChannel {
	var out []FoodChannel
	for _, f := range fields {
		edible, ek := f.Plan.Crop.Edible.Value()
		if ek && !edible {
			continue
		}
		yield, yk := f.Plan.Crop.HarvestNutrition.Value()
		days, dk := f.Plan.Crop.GrowDays.Value()
		nutrition := domain.Unknown[float64]()
		// Preserve malformed known numbers for PlanFood's error boundary.
		if yk && !foodNumber(yield) || dk && !fieldPositive(days) || f.Plan.Sites.Cells < 0 {
			nutrition = domain.Known(math.NaN())
		} else if ek && yk && dk {
			nutrition = domain.Known(yield * float64(f.Plan.Sites.Cells) / days)
		}
		out = append(out, FoodChannel{Kind: FoodCrop, ID: f.ID, NutritionPerDay: nutrition, WorkPerDay: f.WorkPerDay, LeadDays: f.RemainingGrowDays, Open: f.Open})
	}
	return out
}
