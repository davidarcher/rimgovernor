package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestPowerWeatherSafetyPreservesExposureAndUnknown(t *testing.T) {
	for _, tc := range []struct {
		name             string
		vulnerable, roof domain.Fact[bool]
		known, safe      bool
	}{
		{"roofed battery", domain.Known(true), domain.Known(true), true, true},
		{"empty exposed battery", domain.Known(true), domain.Known(false), true, false},
		{"solar panel", domain.Known(false), domain.Known(false), true, true},
		{"unknown roof", domain.Known(true), domain.Unknown[bool](), false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, known := PowerWeatherSafe([]PowerBuilding{{RainVulnerable: tc.vulnerable, Roofed: tc.roof, Stored: domain.Known(0.0)}}).Value()
			if got != tc.safe || known != tc.known {
				t.Fatal(got, known)
			}
		})
	}
}

func TestPowerSafetyPrecedesGenerationAndUsesNativeRoofs(t *testing.T) {
	battery := powerSite("battery", 8, 0, 0, "a")
	battery.Cell.Z = 8
	battery.Occupied = []domain.Cell{battery.Cell, {X: 8, Z: 9}}
	battery.RainVulnerable, battery.Roofed = domain.Known(true), domain.Known(false)
	topology := PowerTopology{Buildings: []PowerSite{battery}, Blackout: domain.Known(false)}
	var cells []SiteCell
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), SupportsLight: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)})
		}
	}
	p, err := SelectPowerMethod(domain.Known(topology), Bounds{20, 20}, cells, nil, DefaultPowerPlanning())
	if err != nil || p.Method != PowerShelter || p.Target != "battery" || p.Room != (Rectangle{6, 6, 5, 6}) {
		t.Fatal(p, err)
	}
	blocked := []domain.Cell{{X: 6, Z: 6}}
	p, err = SelectPowerMethod(domain.Known(topology), Bounds{20, 20}, cells, blocked, DefaultPowerPlanning())
	if err != nil || p.Method != PowerRouteBlocked {
		t.Fatal(p, err)
	}
	topology.Buildings[0].Roofed = domain.Known(true)
	p, err = SelectPowerMethod(domain.Known(topology), Bounds{20, 20}, cells, nil, DefaultPowerPlanning())
	if err != nil || p.Method != PowerNoMethod {
		t.Fatal(p, err)
	}
}

func TestPowerUpgradesOrdinaryConduitsEvenWithoutRain(t *testing.T) {
	cells := []domain.Cell{{X: 3, Z: 3}, {X: 2, Z: 3}}
	v := PowerTopology{Conduits: cells, UnsafeConduits: cells, Blackout: domain.Known(false)}
	p, err := SelectPowerMethod(domain.Known(v), Bounds{20, 20}, nil, nil, DefaultPowerPlanning())
	if err != nil || p.Method != PowerConnect || string(p.Method) != "HiddenConduit" || len(p.Cells) != 2 || p.Cells[0].X != 2 {
		t.Fatal(p, err)
	}
	v.UnsafeConduits = nil
	p, err = SelectPowerMethod(domain.Known(v), Bounds{20, 20}, nil, nil, DefaultPowerPlanning())
	if err != nil || p.Method != PowerNoMethod {
		t.Fatal(p, err)
	}
}

func TestShortCircuitTracksBurningDamageAndDoesNotReplayArchive(t *testing.T) {
	b := recoveryBuilding("battery")
	b.HitPoints = domain.Known(int64(40))
	b.Burning = domain.Known(true)
	noConditions := domain.Known([]DisasterCondition{})
	event := domain.Known(domain.Tick(10))
	h, err := ReviewDisaster(noConditions, domain.Known([]RecoveryBuilding{b}), disasterGates(), nil, 10, event)
	if err != nil || h == nil || h.Phase != DisasterRecovering || len(h.Damaged) != 1 {
		t.Fatal(h, err)
	}
	b.Burning = domain.Known(false)
	b.HitPoints = domain.Known(int64(100))
	h, err = ReviewDisaster(noConditions, domain.Known([]RecoveryBuilding{b}), disasterGates(), h, 11, event)
	if err != nil || h.Phase != DisasterRestored {
		t.Fatal(h, err)
	}
	b.HitPoints = domain.Known(int64(50))
	same, err := ReviewDisaster(noConditions, domain.Known([]RecoveryBuilding{b}), disasterGates(), h, 12, event)
	if err != nil || same.Phase != DisasterRestored || same.Observed != 11 {
		t.Fatal(same, err)
	}
	fresh, err := ReviewDisaster(noConditions, domain.Known([]RecoveryBuilding{b}), disasterGates(), same, 13, domain.Known(domain.Tick(13)))
	if err != nil || fresh.Phase != DisasterRecovering {
		t.Fatal(fresh, err)
	}
}
