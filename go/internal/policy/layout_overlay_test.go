package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestLayoutOverlayClipsToTheMap(t *testing.T) {
	if r, ok := clip(Rectangle{X: -3, Z: 5, Width: 10, Height: 1}, Bounds{Width: 4, Height: 10}); !ok || r != (Rectangle{X: 0, Z: 5, Width: 4, Height: 1}) {
		t.Fatalf("%+v %v", r, ok)
	}
	if _, ok := clip(Rectangle{X: 20, Z: 0, Width: 2, Height: 2}, Bounds{Width: 4, Height: 4}); ok {
		t.Fatal("outside rect kept")
	}
	runs := cellRuns([]domain.Cell{{X: 1, Z: 0}, {X: 2, Z: 0}, {X: 4, Z: 0}, {X: 0, Z: 2}})
	if len(runs) != 3 || runs[0] != (RowRun{X: 1, Z: 0, Length: 2}) || runs[2] != (RowRun{X: 0, Z: 2, Length: 1}) {
		t.Fatalf("%+v", runs)
	}
}
