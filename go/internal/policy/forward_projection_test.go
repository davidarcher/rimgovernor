package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func k(v float64) domain.Fact[float64] { return domain.Known(v) }

func TestProjectForwardFood(t *testing.T) {
	// foodFixture: 3 nutrition/day, 9 nutrition rotting after 1 day: runway 1.
	got, ok := ProjectForward(ForwardInputs{Food: foodFixture()}).Food.Value()
	if !ok || got.RunwayDays != 1 || got.ShortfallDays != 4 {
		t.Fatal(got, ok)
	}
	s := foodFixture()
	s.Stocks = []FoodStock{durableFood("rice", 30, "", "a", "b")}
	got, ok = ProjectForward(ForwardInputs{Food: s}).Food.Value()
	if !ok || got.ShortfallDays != 0 {
		t.Fatal(got, ok)
	}
	s.Complete = domain.Unknown[bool]()
	if _, ok = ProjectForward(ForwardInputs{Food: s}).Food.Value(); ok {
		t.Fatal("incomplete census must be unknown")
	}
}

func TestProjectForwardPower(t *testing.T) {
	nets := []PowerNetworkFact{
		{ID: "b", GenerationW: k(0), ConsumptionW: k(100), StoredWD: k(250)},
		{ID: "a", GenerationW: k(200), ConsumptionW: k(100), StoredWD: k(0)},
	}
	got, ok := ProjectForward(ForwardInputs{Power: nets}).Power.Value()
	if !ok || len(got.Nets) != 2 || got.Nets[0].ID != "a" || got.Nets[0].ShortfallDays != 0 || got.Nets[1].ReserveDays != 2.5 || got.ShortfallDays != 2.5 {
		t.Fatal(got, ok)
	}
	nets[1].StoredWD = domain.Unknown[float64]()
	if _, ok = ProjectForward(ForwardInputs{Power: nets}).Power.Value(); ok {
		t.Fatal("unknown stored energy must be unknown")
	}
	if _, ok = ProjectForward(ForwardInputs{}).Power.Value(); ok {
		t.Fatal("no networks must be unknown")
	}
}

func TestProjectForwardTemperature(t *testing.T) {
	in := ForwardInputs{Policy: DefaultRoutinePolicy(), Sleeping: SleepingRange{Min: k(8), Max: k(20)}, Conditions: domain.Known([]DisasterCondition{})}
	got, ok := ProjectForward(in).Temperature.Value()
	if !ok || got.BreachDays != ProjectionHorizonDays {
		t.Fatal(got, ok)
	}
	ticks := int64(2 * domain.TicksPerDay)
	in.Sleeping.Min = k(18)
	in.Conditions = domain.Known([]DisasterCondition{{Definition: ConditionColdSnap, TicksLeft: &ticks}})
	got, ok = ProjectForward(in).Temperature.Value()
	if !ok || got.BreachDays != 0 || got.ExtremeConditionDays != 2 {
		t.Fatal(got, ok)
	}
	in.Sleeping.Max = domain.Unknown[float64]()
	if _, ok = ProjectForward(in).Temperature.Value(); ok {
		t.Fatal("unknown room temperature must be unknown")
	}
}

func TestProjectForwardDefense(t *testing.T) {
	turrets := []DefenseTurretFacts{
		{ID: "1", Powered: domain.Known(true), OutOfFuel: domain.Known(false), DPS: k(10)},
		{ID: "2", Powered: domain.Known(true), OutOfFuel: domain.Known(true), DPS: k(7)},
	}
	got, ok := ProjectForward(ForwardInputs{Turrets: domain.Known(turrets)}).Defense.Value()
	if !ok || got.DPS != 10 || got.DisabledCount != 1 || len(got.Gaps) == 0 {
		t.Fatal(got, ok)
	}
	turrets[0].DPS = domain.Unknown[float64]()
	if _, ok = ProjectForward(ForwardInputs{Turrets: domain.Known(turrets)}).Defense.Value(); ok {
		t.Fatal("unknown DPS must be unknown")
	}
}
