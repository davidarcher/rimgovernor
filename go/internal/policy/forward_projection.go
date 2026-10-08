package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ProjectionHorizonDays is the first-pass look-ahead (#1912, decided on #1856).
const ProjectionHorizonDays = 5.0

// ForwardInputs are the facts one projection reads. Shadow only: nothing in
// admission consumes the result.
type ForwardInputs struct {
	Food         FoodSupply
	Power        []PowerNetworkFact
	Sleeping     SleepingRange
	Conditions   domain.Fact[[]DisasterCondition]
	Turrets      domain.Fact[[]DefenseTurretFacts]
	Policy       RoundsPolicy
	Construction ConstructionInputs
	Fuel         FuelInputs
	// Runways are the resource runways the drug, material and medicine
	// domains read, split by the catalog's Items.
	Runways    []ResourceRunway
	Items      ItemFacts
	AnimalFeed AnimalFeedInputs
}

// SleepingRange is the coldest and hottest observed sleeping-room temperature.
type SleepingRange struct{ Min, Max domain.Fact[float64] }

// ForwardProjection rolls each resource forward over HorizonDays. A resource
// whose inputs are not all observed is an unknown fact, never a default.
type ForwardProjection struct {
	HorizonDays  float64
	Food         domain.Fact[FoodProjection]
	Power        domain.Fact[PowerProjection]
	Temperature  domain.Fact[TemperatureProjection]
	Defense      domain.Fact[DefenseProjection]
	Construction domain.Fact[ConstructionProjection]
	Fuel         domain.Fact[FuelProjection]
	Drugs        domain.Fact[StockProjection]
	Resources    domain.Fact[StockProjection]
	Medicine     domain.Fact[StockProjection]
	AnimalFeed   domain.Fact[AnimalFeedProjection]
}

type FoodProjection struct {
	RunwayDays    float64
	ShortfallDays float64
}

// PowerNetProjection is one net's stored energy over its current net drain.
type PowerNetProjection struct {
	ID            string
	ReserveDays   float64
	ShortfallDays float64
}

type PowerProjection struct {
	Nets          []PowerNetProjection
	ShortfallDays float64 // the largest per-net shortfall
}

// TemperatureProjection holds the observed sleeping-room extremes: the native
// read carries no rate of change, so a breach observed now is projected to
// last the whole horizon. ExtremeConditionDays is the observed remaining
// ColdSnap/HeatWave duration inside the horizon, for context.
type TemperatureProjection struct {
	MinC, MaxC           float64
	BreachDays           float64
	ExtremeConditionDays float64
}

// DefenseProjection reports only what is observed. Ammunition or fuel burn and
// raid arrival have no native rate, so they are listed in Gaps, not guessed.
type DefenseProjection struct {
	Turrets       int
	DPS           float64 // summed over turrets that are powered and not out of fuel
	DisabledCount int
	Gaps          []string
}

// ProjectForward projects food, power, temperature and defense over
// ProjectionHorizonDays.
func ProjectForward(in ForwardInputs) ForwardProjection {
	return ForwardProjection{
		HorizonDays:  ProjectionHorizonDays,
		Food:         projectFood(in.Food),
		Power:        projectPower(in.Power),
		Temperature:  projectTemperature(in),
		Defense:      projectDefense(in.Turrets),
		Construction: projectConstruction(in.Construction),
		Fuel:         PlanFuelRunway(in.Fuel).Projection,
		Drugs:        PlanDrugRunway(in.Runways, in.Items),
		Resources:    PlanMaterialRunway(in.Runways, in.Items),
		Medicine:     PlanMedicineRunway(in.Runways, in.Items),
		AnimalFeed:   PlanAnimalFeedRunway(in.AnimalFeed).Projection,
	}
}

// RunwayShortfall is the days a runway falls short of target: the one formula
// the food projection, the power projection and NutritionDemand share.
func RunwayShortfall(runway, target float64) float64 { return math.Max(0, target-runway) }

func projectFood(supply FoodSupply) domain.Fact[FoodProjection] {
	forecast, err := ForecastFood(supply, nil)
	if err != nil {
		return domain.Unknown[FoodProjection]()
	}
	days, known := forecast.RunwayDays.Value()
	if !known || !finite(days) {
		return domain.Unknown[FoodProjection]()
	}
	return domain.Known(FoodProjection{RunwayDays: days, ShortfallDays: RunwayShortfall(days, ProjectionHorizonDays)})
}

func projectPower(nets []PowerNetworkFact) domain.Fact[PowerProjection] {
	if len(nets) == 0 {
		return domain.Unknown[PowerProjection]()
	}
	out := PowerProjection{}
	for _, n := range nets {
		days, known := n.ReserveDays().Value()
		if !known || math.IsNaN(days) || days < 0 {
			return domain.Unknown[PowerProjection]()
		}
		row := PowerNetProjection{ID: n.ID, ReserveDays: days, ShortfallDays: RunwayShortfall(days, ProjectionHorizonDays)}
		out.ShortfallDays = math.Max(out.ShortfallDays, row.ShortfallDays)
		out.Nets = append(out.Nets, row)
	}
	sort.Slice(out.Nets, func(i, j int) bool { return out.Nets[i].ID < out.Nets[j].ID })
	return domain.Known(out)
}

func projectTemperature(in ForwardInputs) domain.Fact[TemperatureProjection] {
	lo, lk := in.Sleeping.Min.Value()
	hi, hk := in.Sleeping.Max.Value()
	if !lk || !hk || !finite(lo) || !finite(hi) || lo > hi {
		return domain.Unknown[TemperatureProjection]()
	}
	ticks, tk := ConditionRemainingTicks(in.Conditions, ConditionColdSnap, ConditionHeatWave).Value()
	if !tk {
		return domain.Unknown[TemperatureProjection]()
	}
	out := TemperatureProjection{MinC: lo, MaxC: hi, ExtremeConditionDays: math.Min(ProjectionHorizonDays, float64(ticks)/domain.TicksPerDay)}
	if lo < in.Policy.ColdEnter || hi > in.Policy.HotEnter {
		out.BreachDays = ProjectionHorizonDays
	}
	return domain.Known(out)
}

func projectDefense(turrets domain.Fact[[]DefenseTurretFacts]) domain.Fact[DefenseProjection] {
	rows, known := turrets.Value()
	if !known {
		return domain.Unknown[DefenseProjection]()
	}
	out := DefenseProjection{Turrets: len(rows), Gaps: []string{"turret ammunition or fuel burn rate", "raid arrival"}}
	for _, t := range rows {
		powered, pk := t.Powered.Value()
		empty, ek := t.OutOfFuel.Value()
		dps, dk := t.DPS.Value()
		if !pk || !ek || !dk || !finite(dps) || dps < 0 {
			return domain.Unknown[DefenseProjection]()
		}
		if powered && !empty {
			out.DPS += dps
		} else {
			out.DisabledCount++
		}
	}
	return domain.Known(out)
}
