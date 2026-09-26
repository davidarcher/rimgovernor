package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func inRect(r Rectangle, c domain.Cell) bool {
	return c.X >= r.X && c.Z >= r.Z && c.X < r.X+r.Width && c.Z < r.Z+r.Height
}

// TestTurbineWindCells pins the mirror to the 1.6 CalculateWindCells rects.
func TestTurbineWindCells(t *testing.T) {
	for _, tc := range []struct {
		rot         domain.Rotation
		zLo, zHi, n int32
	}{{domain.North, 4, 21, 112}, {domain.South, -1, 16, 112}} {
		cells := TurbineWindCells(domain.Cell{X: 10, Z: 10}, tc.rot)
		lo, hi := int32(99), int32(-99)
		for _, c := range cells {
			lo, hi = min(lo, c.Z), max(hi, c.Z)
			if c.X < 7 || c.X > 13 {
				t.Fatal("x", c)
			}
		}
		if int32(len(cells)) != tc.n || lo != tc.zLo || hi != tc.zHi {
			t.Fatal(tc.rot, len(cells), lo, hi)
		}
	}
	north := TurbineWindCells(domain.Cell{X: 10, Z: 10}, domain.North)
	for _, c := range north {
		if c.Z == 10 || c.Z == 11 {
			t.Fatal("footprint in its own zone", c)
		}
	}
}

func TestPlanUtilities(t *testing.T) {
	zones := coreTestZones()
	core := PlanCore(zones, 3)
	p := PlanUtilities(core, UtilityWants{TurbinePairs: 2, Solar: 2, Geysers: []Rectangle{{X: 20, Z: 100, Width: 2, Height: 2}}})
	if !p.Valid() {
		t.Fatal("invalid")
	}
	g := newCoreGrid(zones, nil)
	count := map[ReservationKind]int{}
	var turbines, lanes []LayoutReservation
	for _, r := range p.Reservations {
		count[r.Kind]++
		switch r.Kind {
		case ReserveTurbine:
			turbines = append(turbines, r)
		case ReserveTurbineLane:
			lanes = append(lanes, r)
		}
	}
	if count[ReserveTurbine] != 4 || count[ReserveTurbineLane] != 6 || count[ReserveSolar] != 2 || count[ReserveGeothermal] != 1 || count[ReserveExhaust] != 1 {
		t.Fatal(count)
	}
	// Battery room: 5 wide off the spine, door on the walkway, no lane.
	var battery *LayoutRoom
	for i := range p.Rooms {
		if p.Rooms[i].Role == ModuleBattery {
			battery = &p.Rooms[i]
		}
	}
	if battery == nil || battery.Interior.Width != 5 || battery.Door.X != battery.Interior.X+2 {
		t.Fatal("battery", battery)
	}
	if n := len(BatterySlots(*battery)); n != 8 {
		t.Fatal("slots", n)
	}
	checkCore(t, LayoutPlan{Spine: p.Spine, Rooms: p.Rooms[:len(core.Rooms)], Zones: zones}, 3)

	planned := func(c domain.Cell) bool {
		for _, r := range p.Rooms {
			if inRect(roomWalls(r), c) {
				return true
			}
		}
		for _, s := range p.Spine {
			if c.X >= s.From.X && c.X <= s.To.X && c.Z >= s.From.Z-1 && c.Z <= s.From.Z+1 {
				return true
			}
		}
		return false
	}
	for _, tb := range turbines {
		south := false
		for _, o := range turbines {
			if o.Pair == tb.Pair && o.Area.Z > tb.Area.Z {
				south = true
			}
		}
		center, rot := TurbinePlacement(tb.Area, south)
		inLane := 0
		for _, c := range TurbineWindCells(center, rot) {
			if g.rock[c] || !g.core[c] || planned(c) {
				t.Fatal("blocked wind cell", c)
			}
			for _, o := range turbines {
				if inRect(o.Area, c) {
					t.Fatal("turbine in a zone", o, c)
				}
			}
			for _, l := range lanes {
				if l.Pair == tb.Pair && inRect(l.Area, c) {
					inLane++
					break
				}
			}
			field := false
			for _, z := range p.Zones[len(zones):] {
				for _, run := range z.Runs {
					field = field || run.Z == c.Z && c.X >= run.X && c.X < run.X+run.Length
				}
			}
			if !field {
				t.Fatal("wind cell not a field", c)
			}
		}
		if inLane != 112 {
			t.Fatal("wind cells outside lanes", inLane)
		}
	}
	// Grow never builds over a reservation.
	grown := Grow(p, 20)
	for _, r := range grown.Rooms {
		for _, res := range p.Reservations {
			w := roomWalls(r)
			if w.X < res.Area.X+res.Area.Width && res.Area.X < w.X+w.Width && w.Z < res.Area.Z+res.Area.Height && res.Area.Z < w.Z+w.Height {
				t.Fatal("room over reservation", r, res)
			}
		}
	}
}
