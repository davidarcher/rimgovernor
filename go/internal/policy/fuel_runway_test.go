package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var (
	woodBurn  = domain.Known(FuelBurn{PerDay: 22, UnitsPerItem: 1, Flickable: true})
	turretUse = domain.Known(FuelBurn{PerDay: 1, UnitsPerItem: .75, UseDriven: true, Difficulty: true})
)

func woodGenerator(fuel float64) FuelConsumer {
	return FuelConsumer{ID: "gen", Fuel: domain.Known(fuel), Target: domain.Known(75.0), OutOfFuel: domain.Known(fuel <= 0), SwitchedOn: domain.Known(true), Powered: domain.Known(true), Burn: woodBurn, Fuels: []Resource{"WoodLog"}}
}

func fuelInputs(stock int64, consumers ...FuelConsumer) FuelInputs {
	return FuelInputs{Consumers: domain.Known(consumers), Stock: StockReader{Resources: domain.Known([]Amount{{Resource: "WoodLog", Count: stock}, {Resource: "Steel", Count: 0}})}}
}

func TestFuelRunwayProjectsGeneratorShortfall(t *testing.T) {
	t.Parallel()
	// 22 a day over 5 days is 110, less the 30 in the tank: 80 logs wanted,
	// 20 stocked, so 60 logs (2.7 days of burn) are missing.
	plan := PlanFuelRunway(fuelInputs(20, woodGenerator(30)))
	if plan.Needs["WoodLog"] != 80 {
		t.Fatalf("needs %v", plan.Needs)
	}
	p, ok := plan.Projection.Value()
	if !ok || len(p.Resources) != 1 || p.Resources[0].Short != 60 || p.ShortfallDays < 2.7 || p.ShortfallDays > 2.8 {
		t.Fatalf("%+v %v", p, ok)
	}
	// Enough stock behind the tank: no demand, no shortfall.
	plan = PlanFuelRunway(fuelInputs(80, woodGenerator(30)))
	if p, ok := plan.Projection.Value(); len(plan.Needs) != 0 || !ok || p.ShortfallDays != 0 {
		t.Fatalf("%+v %+v", plan, p)
	}
	// Two generators sum their burn.
	if plan = PlanFuelRunway(fuelInputs(0, woodGenerator(30), woodGenerator(110))); plan.Needs["WoodLog"] != 80 {
		t.Fatalf("needs %v", plan.Needs)
	}
}

func TestFuelRunwayGatesBurnOnSwitchAndPower(t *testing.T) {
	t.Parallel()
	off := woodGenerator(0)
	off.SwitchedOn, off.OutOfFuel = domain.Known(false), domain.Known(false)
	if plan := PlanFuelRunway(fuelInputs(0, off)); len(plan.Needs) != 0 {
		t.Fatalf("a switched-off generator burns nothing: %v", plan.Needs)
	}
	unknown := woodGenerator(30)
	unknown.SwitchedOn = domain.Unknown[bool]()
	plan := PlanFuelRunway(fuelInputs(0, unknown))
	if _, ok := plan.Projection.Value(); ok || len(plan.Needs) != 0 {
		t.Fatalf("an unknown switch is an unknown projection, not a default: %+v", plan)
	}
}

func TestFuelRunwayUnknownInputsStayUnknown(t *testing.T) {
	t.Parallel()
	blind := woodGenerator(30)
	blind.Fuel = domain.Unknown[float64]()
	if _, ok := PlanFuelRunway(fuelInputs(10, blind)).Projection.Value(); ok {
		t.Fatal("unknown fuel level projected")
	}
	noBurn := woodGenerator(30)
	noBurn.Burn = domain.Unknown[FuelBurn]()
	if _, ok := PlanFuelRunway(fuelInputs(10, noBurn)).Projection.Value(); ok {
		t.Fatal("unknown burn projected")
	}
	if _, ok := PlanFuelRunway(FuelInputs{Consumers: domain.Known([]FuelConsumer{woodGenerator(30)})}).Projection.Value(); ok {
		t.Fatal("unknown stock projected")
	}
	if _, ok := PlanFuelRunway(FuelInputs{}).Projection.Value(); ok {
		t.Fatal("unread census projected")
	}
	// No refuelable building at all is a known, empty runway.
	if p, ok := PlanFuelRunway(fuelInputs(0)).Projection.Value(); !ok || p.ShortfallDays != 0 {
		t.Fatalf("%+v %v", p, ok)
	}
}

func TestFuelRunwayEmptyBarrelIsZeroRunwayAndUseDrivenHasNone(t *testing.T) {
	t.Parallel()
	empty := FuelConsumer{ID: "t1", Fuel: domain.Known(0.0), Target: domain.Known(60.0), OutOfFuel: domain.Known(true), Burn: turretUse, Fuels: []Resource{"Steel"}}
	loaded := FuelConsumer{ID: "t2", Fuel: domain.Known(40.0), Target: domain.Known(60.0), OutOfFuel: domain.Known(false), Burn: turretUse, Fuels: []Resource{"Steel"}}
	plan := PlanFuelRunway(fuelInputs(0, empty, loaded))
	// 60 fuel units at .75 a steel is 80 steel; the loaded barrel's burn is
	// per shot and unknown, so it asks for nothing and is a gap.
	if plan.Needs["Steel"] != 80 {
		t.Fatalf("needs %v", plan.Needs)
	}
	p, ok := plan.Projection.Value()
	if !ok || p.ShortfallDays != ProjectionHorizonDays || len(p.Gaps) != 1 || p.Gaps[0] != "t2" {
		t.Fatalf("%+v %v", p, ok)
	}
	// An empty barrel without a known target cannot be sized.
	empty.Target = domain.Unknown[float64]()
	plan = PlanFuelRunway(fuelInputs(0, empty))
	if _, ok := plan.Projection.Value(); ok || len(plan.Needs) != 0 {
		t.Fatalf("%+v", plan)
	}
	// Stocked steel covers the refill.
	empty.Target = domain.Known(60.0)
	in := fuelInputs(0, empty)
	in.Stock.Resources = domain.Known([]Amount{{Resource: "Steel", Count: 80}})
	if plan = PlanFuelRunway(in); len(plan.Needs) != 0 {
		t.Fatalf("%v", plan.Needs)
	}
}

func TestForwardProjectionCarriesFuelDomain(t *testing.T) {
	t.Parallel()
	got := ProjectForward(ForwardInputs{Fuel: fuelInputs(20, woodGenerator(30))})
	if p, ok := got.Fuel.Value(); !ok || p.ShortfallDays <= 0 {
		t.Fatalf("%+v %v", p, ok)
	}
	if shortfall, why := shadowShortfall(got, ShadowFuel); why != "" || shortfall <= 0 {
		t.Fatal(shortfall, why)
	}
	if _, why := shadowShortfall(ProjectForward(ForwardInputs{}), ShadowFuel); why == "" {
		t.Fatal("an unread census must stay unranked")
	}
}
