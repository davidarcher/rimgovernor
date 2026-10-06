package policy

import (
	"errors"
	"fmt"
	"math"
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
	// DistanceSquared is the nearest source cell to the colony centre;
	// fishing regions of equal lead rank nearest-first on it.
	DistanceSquared domain.Fact[float64]
	// Prey is a squad hunt's animals (SquadHunts), sorted.
	Prey []string
}

type FoodPlanRequest struct {
	Demand                           FoodForecast
	ReserveDays, MinDays, TargetDays float64
	// EmergencyDays is the stage's starvation line (FootholdFoodDays).
	// Under it a new hunt or forage is not credited toward the target --
	// only delivered food counts -- and the lead-0 channels open one per
	// kind first, so hunting, fishing, foraging and harvest run in parallel
	// up to the labor budget. Zero disables the emergency.
	EmergencyDays float64
	Channels      domain.Fact[[]FoodChannel]
	Labor         domain.Fact[float64]
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
	// Forecast is the shared allocation behind this portfolio, including animals.
	Forecast                                 FoodForecast
	Portfolio, Unknown                       []FoodPlanEntry
	DeliveredPerDay, DemandPerDay, GapPerDay float64
}

var ErrFoodPlanFacts = errors.New("food plan inputs unavailable or invalid")

// SupplyFoodPlan is the food view of PlanSupply: the request's forecast becomes
// the Nutrition demand, its channels become candidates, and the supply plan
// reads back as a FoodPlan. PlanSupply owns the ranking, labor budget and
// credit; GapPerDay is the covered demand minus admitted, risk-adjusted delivery.
func SupplyFoodPlan(r FoodPlanRequest) (FoodPlan, error) {
	demand, err := NutritionDemand(NutritionDemandInput{Forecast: r.Demand, ReserveDays: r.ReserveDays, MinDays: r.MinDays, TargetDays: r.TargetDays, EmergencyDays: r.EmergencyDays})
	if err != nil {
		return FoodPlan{}, ErrFoodPlanFacts
	}
	req := SupplyPlanRequest{Demands: domain.Known([]SupplyDemand{demand}), Labor: r.Labor, Candidates: domain.Unknown[[]SupplyCandidate]()}
	if rows, known := r.Channels.Value(); known {
		cands := make([]SupplyCandidate, 0, len(rows))
		for _, c := range rows {
			cands = append(cands, SupplyCandidateOfFood(c))
		}
		req.Candidates = domain.Known(cands)
	}
	plan, err := PlanSupply(req)
	if err != nil {
		return FoodPlan{}, ErrFoodPlanFacts
	}
	out := FoodPlan{Forecast: r.Demand, GapPerDay: plan.Gap(NutritionKey), DeliveredPerDay: plan.Delivered(NutritionKey)}
	for _, c := range r.Demand.Consumers {
		out.DemandPerDay += c.NutritionPerDay
	}
	view := func(rows []SupplyEntry) (entries []FoodPlanEntry) {
		for _, e := range rows {
			channel, ok := FoodChannelOfSupply(e.Candidate)
			if !ok {
				panic("not a food channel")
			}
			entry := FoodPlanEntry{Channel: channel, Decision: FoodPlanDecision(e.Decision), Reason: e.Reason}
			for _, t := range e.Terms {
				entry.Terms = append(entry.Terms, FoodPlanTerm{Name: t.Name, Value: t.Value})
			}
			for _, c := range e.Credit {
				entry.DeliveredPerDay += c.Amount
			}
			entries = append(entries, entry)
		}
		return entries
	}
	out.Portfolio, out.Unknown = view(plan.Portfolio), view(plan.Unknown)
	return out, nil
}

func validFoodChannelKind(k FoodChannelKind) bool {
	switch k {
	case FoodForage, FoodHunt, FoodCrop, FoodAnimalProduct, FoodFishing, FoodTrade, FoodCorpse, FoodReserve, FoodCook:
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
			// Range reduces pursuit work; downed prey needs only collection.
			work /= 1 + s.WeaponRange/25
			if s.Downed {
				work = FoodForageWorkTicks
			}
		}
		c := FoodChannel{Kind: kind, ID: s.ID, NutritionPerDay: domain.Known(s.NutritionYield / cycle), WorkPerDay: domain.Known(work / cycle), LeadDays: domain.Known(0.0), Open: domain.Known(false), Terms: []FoodPlanTerm{{"estimated_cycle_days", cycle}, {"estimated_work_ticks", work}}}
		if hunt {
			c.Risk = []FoodRisk{{FoodRevenge, math.Min(1, s.HuntRevengeCost())}}
			c.Terms = append(c.Terms, FoodPlanTerm{"revenge_chance", s.RevengeChance}, FoodPlanTerm{"herd_size", float64(s.HerdSize)}, FoodPlanTerm{"revenge_cost", s.HuntRevengeCost()}, FoodPlanTerm{"weapon_range", s.WeaponRange})
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
		// Preserve malformed known numbers for PlanSupply's error boundary.
		if yk && !foodNumber(yield) || dk && !fieldPositive(days) || f.Plan.Sites.Cells < 0 {
			nutrition = domain.Known(math.NaN())
		} else if ek && yk && dk {
			nutrition = domain.Known(yield * float64(f.Plan.Sites.Cells) / days)
		}
		out = append(out, FoodChannel{Kind: FoodCrop, ID: f.ID, NutritionPerDay: nutrition, WorkPerDay: f.WorkPerDay, LeadDays: f.RemainingGrowDays, Open: f.Open})
	}
	return out
}
