package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestLayoutPlanFirebreaksFillTwoWideGaps(t *testing.T) {
	// Two 3x2 fields at x 0-2 and 5-7 leave a 2-wide break at x 3-4; a
	// third field at x 12 is too far to form one.
	var zones []LayoutZone
	for _, x := range []int32{0, 5, 12} {
		zones = append(zones, LayoutZone{Kind: ZoneField, Runs: []RowRun{{Z: 0, X: x, Length: 3}, {Z: 1, X: x, Length: 3}}})
	}
	zones = append(zones, LayoutZone{Kind: ZonePasture, Runs: []RowRun{{Z: 0, X: 8, Length: 4}}})
	got := LayoutPlan{Zones: zones}.Firebreaks()
	want := []domain.Cell{{X: 3, Z: 0}, {X: 4, Z: 0}, {X: 3, Z: 1}, {X: 4, Z: 1}}
	if len(got) != len(want) {
		t.Fatalf("firebreaks %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("firebreaks %v, want %v", got, want)
		}
	}
	if fields := (LayoutPlan{Zones: zones}).FieldCells(); len(fields) != 18 || fields[domain.Cell{X: 3, Z: 0}] {
		t.Fatalf("field cells %d", len(fields))
	}
}
