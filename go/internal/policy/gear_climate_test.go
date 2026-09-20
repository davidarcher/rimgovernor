package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func mildGearClimate() *GearClimate {
	return &GearClimate{Temperatures: []float64{20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20}, CurrentTwelfth: 7, TicksToNextTwelfth: gearTwelfthTicks}
}

func TestGearClimatePreparesAutumnAndHeatWave(t *testing.T) {
	for _, hot := range []bool{false, true} {
		c := mildGearClimate()
		coat, hat := loadoutOption("Parka", GearOuter), loadoutOption("Tuque", GearHeadgear)
		coat.Cold, hat.Cold = 20, 10
		if hot {
			c.Weather = &GearWeather{Definition: "HeatWave", RemainingTicks: 60000, TemperatureOffset: 17}
			coat.Definition, hat.Definition = "Duster", "CowboyHat"
			coat.Heat, hat.Heat = 12, 5
		} else {
			c.Temperatures[8], c.Temperatures[9] = 5, -10
		}
		p := GearLoadoutInput{Ambient: 22, ComfortableMin: 16, ComfortableMax: 26, Options: []GearOption{coat, hat}}
		old, err := PlanGearLoadout(p)
		if err != nil || len(old.Gaps) != 0 {
			t.Fatalf("ambient fallback: %+v %v", old, err)
		}
		pawn := GearPawn{Pawn: "p", Loadout: "loadout", Climate: c, LoadoutModel: domain.Known(p)}
		review, err := ReviewGear(domain.Known(GearObservation{Pawns: []GearPawn{pawn}}))
		if err != nil || review.Deficit != domain.Known(1.0) || len(review.Loadouts) != 1 || len(review.Loadouts[0].Gaps) != 2 {
			t.Fatalf("hot=%v: %+v %v", hot, review, err)
		}
		demand, known := review.Demand.Value()
		if !known || len(demand) != 2 {
			t.Fatalf("missing advance tailoring demand: %+v", demand)
		}
	}
}

func TestGearClimateRangeDurationAndWrap(t *testing.T) {
	c := mildGearClimate()
	c.CurrentTwelfth = 11
	c.Temperatures[0], c.Temperatures[1], c.Temperatures[2] = 10, -5, -40
	low, high := c.TemperatureRange(22)
	if low != -5 || high != 22 {
		t.Fatalf("wrap/lookahead: %v %v", low, high)
	}
	c.Weather = &GearWeather{Definition: "ColdSnap", RemainingTicks: c.TicksToNextTwelfth, TemperatureOffset: -20}
	low, _ = c.TemperatureRange(22)
	if low != -5 {
		t.Fatalf("expired weather affected future twelfth: %v", low)
	}
	c.Weather.RemainingTicks++
	low, _ = c.TemperatureRange(22)
	if low != -10 {
		t.Fatalf("overlapping weather omitted: %v", low)
	}
	c.Weather.RemainingTicks = 3*gearTwelfthTicks + 1
	low, _ = c.TemperatureRange(22)
	if low != -60 {
		t.Fatalf("long condition did not extend target: %v", low)
	}
	c.Weather.RemainingTicks = 0
	low, _ = c.TemperatureRange(22)
	if low != -5 {
		t.Fatalf("expired condition: %v", low)
	}
}

func TestGearClimateValidation(t *testing.T) {
	for _, mutate := range []func(*GearClimate){
		func(c *GearClimate) { c.Temperatures = c.Temperatures[:11] },
		func(c *GearClimate) { c.Temperatures[0] = math.NaN() },
		func(c *GearClimate) { c.CurrentTwelfth = 12 },
		func(c *GearClimate) { c.TicksToNextTwelfth = 0 },
		func(c *GearClimate) { c.Weather = &GearWeather{Definition: "Rain"} },
		func(c *GearClimate) { c.Weather = &GearWeather{Definition: "ColdSnap", RemainingTicks: -2} },
	} {
		c := mildGearClimate()
		mutate(c)
		if err := (GearLoadoutInput{Climate: c}).Validate(); err == nil {
			t.Fatal("invalid climate accepted")
		}
	}
}
