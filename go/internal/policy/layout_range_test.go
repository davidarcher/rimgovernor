package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRangeStandsScaleWithCapableAdults(t *testing.T) {
	for capable, want := range map[int]int{0: 2, 1: 2, 3: 2, 4: 2, 5: 3, 8: 4, 9: 5, 15: 8, 16: 8, 40: 8} {
		if got := RangeStandsFor(capable); got != want {
			t.Errorf("%d capable adults: %d stands, want %d", capable, got, want)
		}
	}
}

func TestRangeLayoutIsOneRowOfStandsAndADummyRowFacingThem(t *testing.T) {
	for _, facing := range []domain.Rotation{domain.North, domain.South, domain.East, domain.West} {
		room := rangeFromBlock(Rectangle{X: 20, Z: 30, Width: 20, Height: 20}, facing, 5)
		pieces := RangeLayout(room)
		if len(pieces) != 10 {
			t.Fatalf("%s: %d pieces", facing, len(pieces))
		}
		seen := map[domain.Cell]bool{}
		var stands, dummies []domain.Cell
		for _, p := range pieces {
			if seen[p.Cell] || !contains(room.Region.Bounds(), p.Cell) {
				t.Fatalf("%s: piece off the interior or doubled: %+v", facing, p)
			}
			seen[p.Cell] = true
			if p.Kind == RangeStand {
				stands = append(stands, p.Cell)
			} else {
				dummies = append(dummies, p.Cell)
			}
		}
		for i := range stands {
			d := domain.Cell{X: dummies[i].X - stands[i].X, Z: dummies[i].Z - stands[i].Z}
			want := map[domain.Rotation]domain.Cell{domain.North: {Z: RangeDistance}, domain.South: {Z: -RangeDistance}, domain.East: {X: RangeDistance}, domain.West: {X: -RangeDistance}}[facing]
			if d != want {
				t.Fatalf("%s: dummy %d lies %+v from its stand, want %+v", facing, i, d, want)
			}
			if i > 0 {
				step := domain.Cell{X: stands[i].X - stands[i-1].X, Z: stands[i].Z - stands[i-1].Z}
				if step.X+step.Z != 1 || step.X*step.Z != 0 {
					t.Fatalf("%s: stands %d and %d are not side by side with no gap: %+v", facing, i-1, i, step)
				}
			}
		}
		// The blocked footprint is the interior with a one-cell margin, nothing more.
		if block := rangeBlock(room); block != pad(room.Region.Bounds(), 1) {
			t.Fatalf("%s: block %+v is not the interior and its margin", facing, block)
		}
	}
}

// A site beside the core (map edge, cliff or water ahead of it) faces along the
// axis of its rows toward the end farther from the core, never back at the core.
func TestRangeFacingFallsBackAcrossTheCore(t *testing.T) {
	ext := Rectangle{X: 100, Z: 100, Width: 20, Height: 20}
	east := Rectangle{X: 130, Z: 125, Width: 6, Height: 15}
	if got := rangeFacing(domain.East, true, east, ext); got != domain.North {
		t.Errorf("east site above the core centre, rows along X: faces %s, want north", got)
	}
	if got := rangeFacing(domain.East, true, Rectangle{X: 130, Z: 90, Width: 6, Height: 15}, ext); got != domain.South {
		t.Errorf("east site below the core centre, rows along X: faces %s, want south", got)
	}
	north := Rectangle{X: 125, Z: 130, Width: 15, Height: 6}
	if got := rangeFacing(domain.North, false, north, ext); got != domain.East {
		t.Errorf("north site right of the core centre, rows along Z: faces %s, want east", got)
	}
	if got := rangeFacing(domain.North, true, north, ext); got != domain.North {
		t.Errorf("north site with rows along X: faces %s, want north (away)", got)
	}
	if got := rangeFacing(domain.West, false, east, ext); got != domain.West {
		t.Errorf("west site with rows along Z: faces %s, want west (away)", got)
	}
}
