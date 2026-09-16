package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func powerSite(id string, x int32, base, output float64, network string) PowerSite {
	b := PowerSite{ID: id, Definition: "StandingLamp", Cell: domain.Cell{X: x, Z: 2}, PowerBuilding: PowerBuilding{BaseW: domain.Known(base), OutputW: domain.Known(output), Connected: domain.Known(network != ""), Powered: domain.Known(output != 0), Forbidden: domain.Known(false), SwitchedOn: domain.Known(true)}}
	if base > 0 {
		b.Definition = "WoodFiredGenerator"
	}
	if network != "" {
		b.Network = domain.Known(network)
	}
	b.Occupied = []domain.Cell{b.Cell}
	return b
}

func TestPowerMethodNetworkCapacityAndPlayerIntent(t *testing.T) {
	for _, name := range []string{"generate", "recover", "refuel", "capacity", "foreign", "switch", "forbidden", "producer-switch", "flare", "unknown-environment", "unknown-output", "no-consumers"} {
		t.Run(name, func(t *testing.T) {
			v := PowerTopology{Buildings: []PowerSite{powerSite("lamp", 2, -200, 0, "a")}, Blackout: domain.Known(false)}
			want := PowerGenerate
			switch name {
			case "recover":
				v.Buildings[0].Powered = domain.Known(true)
				v.Buildings[0].OutputW = domain.Known(-200.0)
				v.Buildings = append(v.Buildings, powerSite("generator", 5, 1000, 1000, "a"))
				want = PowerNoMethod
			case "refuel":
				v.Buildings = append(v.Buildings, powerSite("generator", 5, 1000, 0, "a"))
				want = PowerWaitOutput
			case "capacity":
				v.Buildings = append(v.Buildings, powerSite("generator", 5, 100, 100, "a"))
			case "foreign":
				v.Buildings = append(v.Buildings, powerSite("generator", 5, 1000, 1000, "b"))
				want = PowerRouteBlocked
			case "switch":
				v.Buildings[0].SwitchedOn = domain.Known(false)
				want = PowerWaitPlayer
			case "forbidden":
				v.Buildings[0].Forbidden = domain.Known(true)
				want = PowerWaitPlayer
			case "producer-switch":
				v.Buildings = append(v.Buildings, powerSite("generator", 5, 1000, 0, "a"))
				v.Buildings[1].SwitchedOn = domain.Known(false)
				want = PowerWaitPlayer
			case "flare":
				v.Blackout = domain.Known(true)
				want = PowerWaitBlackout
			case "unknown-environment":
				v.Blackout = domain.Unknown[bool]()
				want = PowerUnknown
			case "unknown-output":
				v.Buildings[0].OutputW = domain.Unknown[float64]()
				want = PowerUnknown
			case "no-consumers":
				v.Buildings = nil
				want = PowerNoMethod
			}
			p, err := SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, nil, nil, DefaultPowerPlanning())
			if err != nil || p.Method != want {
				t.Fatal(p, err, want)
			}
			if want == PowerGenerate && (p.Key == "" || p.Target != "lamp" || p.Center != v.Buildings[0].Cell) {
				t.Fatal(p)
			}
		})
	}
}

func TestPowerRouteExtendsFromProducerAndSkipsNativeFootprints(t *testing.T) {
	v := PowerTopology{Buildings: []PowerSite{powerSite("lamp", 1, -200, 0, ""), powerSite("generator", 14, 1000, 1000, "b")}, Blackout: domain.Known(false)}
	v.Buildings[1].Occupied = append(v.Buildings[1].Occupied, domain.Cell{X: 13, Z: 2})
	v.Conduits = []domain.Cell{{X: 12, Z: 2}}
	var cells []SiteCell
	for x := int32(1); x <= 14; x++ {
		cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: 2}, SupportsLight: domain.Known(true), Occupied: domain.Known(true)})
	}
	p, err := SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, cells, nil, DefaultPowerPlanning())
	want := []domain.Cell{{X: 11, Z: 2}, {X: 10, Z: 2}, {X: 9, Z: 2}, {X: 8, Z: 2}, {X: 7, Z: 2}, {X: 6, Z: 2}, {X: 5, Z: 2}, {X: 4, Z: 2}}
	if err != nil || p.Method != PowerConnect || !reflect.DeepEqual(p.Cells, want) {
		t.Fatal(p, err)
	}
	v.Conduits = append(v.Conduits, p.Cells...)
	next, err := SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, cells, nil, DefaultPowerPlanning())
	if err != nil || next.Method != PowerConnect || next.Key == p.Key || !reflect.DeepEqual(next.Cells, []domain.Cell{{X: 3, Z: 2}, {X: 2, Z: 2}, {X: 1, Z: 2}}) {
		t.Fatal(next, err)
	}
	blocked, err := SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, cells, []domain.Cell{{X: 6, Z: 2}}, DefaultPowerPlanning())
	if err != nil || blocked.Method != PowerRouteBlocked {
		t.Fatal(blocked, err)
	}
	cells[5].SupportsLight = domain.Unknown[bool]()
	blocked, err = SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, cells, nil, DefaultPowerPlanning())
	if err != nil || blocked.Method != PowerRouteBlocked {
		t.Fatal(blocked, err)
	}
}

func TestPowerMethodIdentityIgnoresOutputAndInputRejectsAmbiguity(t *testing.T) {
	v := PowerTopology{Buildings: []PowerSite{powerSite("lamp", 2, -200, 0, "a"), powerSite("generator", 5, 100, 100, "a")}, Blackout: domain.Known(false)}
	first, err := SelectPowerMethod(domain.Known(v), Bounds{20, 20}, nil, nil, DefaultPowerPlanning())
	if err != nil {
		t.Fatal(err)
	}
	v.Buildings[1].OutputW = domain.Known(0.0)
	next, err := SelectPowerMethod(domain.Known(v), Bounds{20, 20}, nil, nil, DefaultPowerPlanning())
	if err != nil || first.Key != next.Key {
		t.Fatal(first, next, err)
	}
	v.Buildings = append(v.Buildings, powerSite("other", 6, 50, 0, "a"))
	next, err = SelectPowerMethod(domain.Known(v), Bounds{20, 20}, nil, nil, DefaultPowerPlanning())
	if err != nil || first.Key == next.Key {
		t.Fatal(first, next, err)
	}
	for _, phase := range []string{"duplicate", "footprint", "watts", "network", "conduits"} {
		t.Run(phase, func(t *testing.T) {
			bad := PowerTopology{Buildings: []PowerSite{powerSite("lamp", 2, -200, 0, "a")}, Blackout: domain.Known(false)}
			switch phase {
			case "duplicate":
				bad.Buildings = append(bad.Buildings, bad.Buildings[0])
			case "footprint":
				bad.Buildings[0].Occupied = []domain.Cell{{X: 3, Z: 2}}
			case "watts":
				bad.Buildings[0].OutputW = domain.Known(math.NaN())
			case "network":
				bad.Buildings[0].Connected = domain.Known(false)
			case "conduits":
				bad.Conduits = []domain.Cell{{X: 1, Z: 1}, {X: 1, Z: 1}}
			}
			if _, err := SelectPowerMethod(domain.Known(bad), Bounds{20, 20}, nil, nil, DefaultPowerPlanning()); err == nil {
				t.Fatal("ambiguous input accepted")
			}
		})
	}
}

func TestPowerMethodHoldsForFuelRepairAndSizesReserve(t *testing.T) {
	options := func(solar, wood bool, woodStock int64) []GeneratorOption {
		return DefaultGeneratorOptions(func(name string) domain.Fact[bool] {
			switch name {
			case "SolarGenerator":
				return domain.Known(solar)
			case "WoodFiredGenerator":
				return domain.Known(wood)
			}
			return domain.Known(false)
		}, func(r Resource) domain.Fact[int64] {
			if r == "WoodLog" {
				return domain.Known(woodStock)
			}
			return domain.Unknown[int64]()
		})
	}
	for _, name := range []string{"out-of-fuel", "broken", "reserve-short", "reserve-fine", "reserve-unknown", "solar-preferred", "wood-short", "no-generator", "legacy-default"} {
		t.Run(name, func(t *testing.T) {
			v := PowerTopology{Buildings: []PowerSite{powerSite("lamp", 2, -200, 0, "a")}, Blackout: domain.Known(false)}
			planning := PowerPlanning{ReserveMinDays: 1, Generators: options(false, true, 200)}
			want, wantDefinition := PowerGenerate, "WoodFiredGenerator"
			switch name {
			case "out-of-fuel":
				v.Buildings = append(v.Buildings, powerSite("generator", 5, 1000, 0, "a"))
				v.Buildings[1].OutOfFuel = domain.Known(true)
				want = PowerWaitFuel
			case "broken":
				v.Buildings = append(v.Buildings, powerSite("generator", 5, 1000, 0, "a"))
				v.Buildings[1].BrokenDown = domain.Known(true)
				v.Buildings[1].OutOfFuel = domain.Known(true)
				want = PowerWaitRepair
			case "reserve-short", "reserve-fine", "reserve-unknown":
				// Powered by a battery while the generator idles: capacity covers
				// demand on paper, but the net drains.
				v.Buildings[0].Powered = domain.Known(true)
				v.Buildings[0].OutputW = domain.Known(-200.0)
				v.Buildings = append(v.Buildings, powerSite("generator", 5, 1000, 0, "a"))
				v.Buildings[1].Powered = domain.Known(true)
				net := PowerNetworkFact{ID: "a", GenerationW: domain.Known(0.0), ConsumptionW: domain.Known(200.0), StoredWD: domain.Known(100.0), CapacityWD: domain.Known(600.0)}
				if name == "reserve-fine" {
					net.StoredWD = domain.Known(500.0)
					want = PowerWaitOutput
				}
				if name == "reserve-unknown" {
					net.StoredWD = domain.Unknown[float64]()
					want = PowerWaitOutput
				}
				v.Networks = []PowerNetworkFact{net}
			case "solar-preferred":
				planning.Generators = options(true, true, 200)
				wantDefinition = "SolarGenerator"
			case "wood-short":
				planning.Generators = options(false, true, 10)
			case "no-generator":
				planning.Generators = options(false, false, 200)
				want = PowerNoGenerator
			case "legacy-default":
				planning.Generators = nil
			}
			p, err := SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, nil, nil, planning)
			if err != nil || p.Method != want {
				t.Fatal(p, err, want)
			}
			if want == PowerGenerate && (p.Definition != wantDefinition || p.Key == "" || p.Target != "lamp") {
				t.Fatal(p)
			}
		})
	}
	if days, ok := (PowerNetworkFact{GenerationW: domain.Known(300.0), ConsumptionW: domain.Known(200.0), StoredWD: domain.Known(1.0)}).ReserveDays().Value(); !ok || !math.IsInf(days, 1) {
		t.Fatal(days, ok)
	}
	bad := PowerTopology{Buildings: []PowerSite{powerSite("lamp", 2, -200, 0, "a")}, Blackout: domain.Known(false), Networks: []PowerNetworkFact{{ID: "a"}, {ID: "a"}}}
	if _, err := SelectPowerMethod(domain.Known(bad), Bounds{20, 20}, nil, nil, DefaultPowerPlanning()); err == nil {
		t.Fatal("duplicate network accepted")
	}
}
