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
			p, err := SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, nil, nil)
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
	p, err := SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, cells, nil)
	want := []domain.Cell{{X: 11, Z: 2}, {X: 10, Z: 2}, {X: 9, Z: 2}, {X: 8, Z: 2}, {X: 7, Z: 2}, {X: 6, Z: 2}, {X: 5, Z: 2}, {X: 4, Z: 2}}
	if err != nil || p.Method != PowerConnect || !reflect.DeepEqual(p.Cells, want) {
		t.Fatal(p, err)
	}
	v.Conduits = append(v.Conduits, p.Cells...)
	next, err := SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, cells, nil)
	if err != nil || next.Method != PowerConnect || next.Key == p.Key || !reflect.DeepEqual(next.Cells, []domain.Cell{{X: 3, Z: 2}, {X: 2, Z: 2}, {X: 1, Z: 2}}) {
		t.Fatal(next, err)
	}
	blocked, err := SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, cells, []domain.Cell{{X: 6, Z: 2}})
	if err != nil || blocked.Method != PowerRouteBlocked {
		t.Fatal(blocked, err)
	}
	cells[5].SupportsLight = domain.Unknown[bool]()
	blocked, err = SelectPowerMethod(domain.Known(v), Bounds{Width: 20, Height: 20}, cells, nil)
	if err != nil || blocked.Method != PowerRouteBlocked {
		t.Fatal(blocked, err)
	}
}

func TestPowerMethodIdentityIgnoresOutputAndInputRejectsAmbiguity(t *testing.T) {
	v := PowerTopology{Buildings: []PowerSite{powerSite("lamp", 2, -200, 0, "a"), powerSite("generator", 5, 100, 100, "a")}, Blackout: domain.Known(false)}
	first, err := SelectPowerMethod(domain.Known(v), Bounds{20, 20}, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	v.Buildings[1].OutputW = domain.Known(0.0)
	next, err := SelectPowerMethod(domain.Known(v), Bounds{20, 20}, nil, nil)
	if err != nil || first.Key != next.Key {
		t.Fatal(first, next, err)
	}
	v.Buildings = append(v.Buildings, powerSite("other", 6, 50, 0, "a"))
	next, err = SelectPowerMethod(domain.Known(v), Bounds{20, 20}, nil, nil)
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
			if _, err := SelectPowerMethod(domain.Known(bad), Bounds{20, 20}, nil, nil); err == nil {
				t.Fatal("ambiguous input accepted")
			}
		})
	}
}
