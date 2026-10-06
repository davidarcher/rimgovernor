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
	// Open is true once the channel is observed delivering (the delivery
	// ledger); Designated is a committed channel not yet seen delivering; a
	// channel that is neither is closed (see State).
	Open, Designated domain.Fact[bool]
	// Source names the ledger counter group that counts the channel's
	// deliveries ("crop:<zone>", "fish:<x,z>", "forage:<def>",
	// "animal_product:<race>"); empty for a channel the ledger does not count.
	Source string
	Terms  []FoodPlanTerm
	// DistanceSquared is the nearest source cell to the colony centre;
	// fishing regions of equal lead rank nearest-first on it.
	DistanceSquared domain.Fact[float64]
	// Prey is a formation hunt's animals (HuntCandidates), sorted.
	Prey []string
	// Products are the goods a channel yields beside its nutrition (a hunted
	// deer's leather).
	Products []CandidateYield
	// StockCap bounds the nutrition the channel can hold at once (a perishable
	// harvest); Unknown is no known bound.
	StockCap domain.Fact[int64]
}

type FoodPlanRequest struct {
	Demand                           FoodForecast
	ReserveDays, MinDays, TargetDays float64
	// EmergencyDays is the stage's starvation line (FootholdFoodDays).
	// Under it a channel not yet delivering is not credited toward the target --
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

// Selected is whether the plan relies on the channel: it credits it, or opens it
// (an uncredited channel opened under an emergency until it delivers).
func (e FoodPlanEntry) Selected() bool {
	return e.DeliveredPerDay > 0 || e.Decision == FoodPlanOpen
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
				entry.Terms = append(entry.Terms, FoodPlanTerm(t))
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
	var out []FoodChannel
	for _, s := range sources {
		if s.Food && !s.Tree && !s.Hunt {
			c := FoodChannel{Kind: FoodForage, ID: s.ID, NutritionPerDay: domain.Known(s.NutritionYield / FoodForageCycleDays), WorkPerDay: domain.Known(FoodForageWorkTicks / FoodForageCycleDays), LeadDays: domain.Known(0.0), Open: domain.Known(false), Designated: domain.Known(s.Designated),
				Terms: []FoodPlanTerm{{"estimated_cycle_days", FoodForageCycleDays}, {"estimated_work_ticks", FoodForageWorkTicks}}}
			if s.Definition != "" {
				c.Source = "forage:" + s.Definition
			}
			out = append(out, c)
		}
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

// CookTicksPerDay is the work one cook gives a bench per day, the same budget
// convention as the plan's labor (eight hours a worker).
const CookTicksPerDay = 20000.0

// CropKitchen is the cooking a harvest can use: the benches and the number of
// cooks who can work them. Unknown benches or cooks leave a crop raw.
type CropKitchen struct {
	Benches domain.Fact[[]ProductionBench]
	Cooks   domain.Fact[float64]
}

// CropCooking is the best vegetable meal at the kitchen's usable benches.
// NutrientEfficiency is cooked nutrition per raw nutrition, WorkPerNutrition
// the bench work per cooked nutrition and CapacityTicks the cook work a day.
type CropCooking struct {
	Recipe                                              string
	NutrientEfficiency, WorkPerNutrition, CapacityTicks float64
}

// Cooking reads the best meal made of vegetables. It is Unknown while the
// benches, the cooks or any usable meal recipe's facts are unknown, and known
// "none" (ok false, Known) is never confused with it: a kitchen with no usable
// bench or no vegetable meal leaves the crop raw.
func (k CropKitchen) Cooking() domain.Fact[CropCooking] {
	benches, bk := k.Benches.Value()
	cooks, ck := k.Cooks.Value()
	if !bk || !ck || !foodNumber(cooks) {
		return domain.Unknown[CropCooking]()
	}
	usable, unknown := 0, false
	var best CropCooking
	for _, b := range benches {
		u, uk := b.Usable.Value()
		if !uk {
			unknown = true
			continue
		}
		if !u {
			continue
		}
		usable++
		for _, r := range b.Recipes {
			if r.Role != domain.RoleOrdinaryMeal || !positive(r.Available) {
				continue
			}
			eff, ek := r.NutrientEfficiency.Value()
			work, wk := r.WorkPerNutrition.Value()
			classes, ik := r.IngredientClasses.Value()
			if !ek || !wk || !ik || !fieldPositive(eff) || !foodNumber(work) {
				unknown = true
				continue
			}
			if !mealSlotsSupported(classes, map[FoodIngredientClass]bool{IngredientVegetable: true}) {
				continue
			}
			if best.Recipe == "" || eff > best.NutrientEfficiency || eff == best.NutrientEfficiency && (work < best.WorkPerNutrition || work == best.WorkPerNutrition && r.Name < best.Recipe) {
				best = CropCooking{Recipe: r.Name, NutrientEfficiency: eff, WorkPerNutrition: work}
			}
		}
	}
	if best.Recipe == "" && unknown {
		return domain.Unknown[CropCooking]()
	}
	best.CapacityTicks = math.Min(float64(usable), cooks) * CookTicksPerDay
	return domain.Known(best)
}

// CropChannels prices each field as the better of raw and cooked nutrition.
// Cooked nutrition is the raw harvest times the best meal's efficiency, adds
// the cook labor to WorkPerDay and needs the kitchen's capacity; it is chosen
// only when it beats raw (raw wins ties unless raw is known not preferred).
// A perishable harvest bounds the stock it holds to what survives its rot
// days (StockCap); an unknown recipe or rot fact leaves that facet Unknown,
// never zero.
func CropChannels(fields []FoodField, kitchen CropKitchen) []FoodChannel {
	cooking := kitchen.Cooking()
	var out []FoodChannel
	for _, f := range fields {
		edible, ek := f.Plan.Crop.Edible.Value()
		if ek && !edible {
			continue
		}
		yield, yk := f.Plan.Crop.HarvestNutrition.Value()
		days, dk := f.Plan.Crop.GrowDays.Value()
		nutrition := domain.Unknown[float64]()
		work := f.WorkPerDay
		var terms []FoodPlanTerm
		// Preserve malformed known numbers for PlanSupply's error boundary.
		if yk && !foodNumber(yield) || dk && !fieldPositive(days) || f.Plan.Sites.Cells < 0 {
			nutrition = domain.Known(math.NaN())
		} else if ek && yk && dk {
			raw := yield * float64(f.Plan.Sites.Cells) / days
			nutrition = domain.Known(raw)
			terms = append(terms, FoodPlanTerm{"raw_nutrition_per_day", raw})
			if cook, known := cooking.Value(); known && cook.Recipe != "" {
				cooked := raw * cook.NutrientEfficiency
				cookWork := cooked * cook.WorkPerNutrition
				preferred, pk := f.Plan.Crop.RawPreferred.Value()
				if cookWork <= cook.CapacityTicks && (cooked > raw || cooked == raw && pk && !preferred) {
					nutrition = domain.Known(cooked)
					terms = append(terms, FoodPlanTerm{"cooked_nutrition_per_day", cooked}, FoodPlanTerm{"cook_work_per_day", cookWork})
					if w, wk := f.WorkPerDay.Value(); wk {
						work = domain.Known(w + cookWork)
					}
				}
			}
		}
		c := FoodChannel{Kind: FoodCrop, ID: f.ID, NutritionPerDay: nutrition, WorkPerDay: work, LeadDays: f.RemainingGrowDays, Open: f.Open, StockCap: domain.Unknown[int64](), Designated: domain.Known(true), Source: "crop:" + f.ID, Terms: terms}
		if dk && fieldPositive(days) {
			c.Terms = append(c.Terms, FoodPlanTerm{"grow_days", days})
		}
		if n, nk := nutrition.Value(); nk && foodNumber(n) {
			rot, rk := f.Plan.Crop.RotDays.Value()
			perishable, pk := f.Plan.Crop.Perishable.Value()
			if rk && pk && perishable && fieldPositive(rot) {
				c.StockCap = domain.Known(int64(math.Ceil(n * rot)))
				c.Terms = append(c.Terms, FoodPlanTerm{"rot_days", rot})
			}
		}
		out = append(out, c)
	}
	return out
}
