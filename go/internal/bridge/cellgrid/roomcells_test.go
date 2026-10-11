package cellgrid

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// CellsByRoom agrees with grouping Cells by their Room fact, and reads the
// room key of a held cell only.
func TestCellsByRoomMatchesCells(t *testing.T) {
	g := keyframeGrid()
	g.Rect = WireRect(policy.Rectangle{X: 5, Z: 7, Width: 2, Height: 1})
	g.Strings = []string{"R1"}
	g.Cell = codes(1, 1)
	g.Room = sparse([]uint32{0, 1}, []uint32{1, 1}, nil)
	grid, err := Apply(nil, true, g)
	if err != nil {
		t.Fatal(err)
	}
	want := map[string][]domain.Cell{}
	for _, c := range grid.Cells() {
		if key, known := c.Room.Value(); known {
			want[key] = append(want[key], c.Cell)
		}
	}
	if got := grid.CellsByRoom(); len(want) != 1 || !reflect.DeepEqual(got, want) {
		t.Fatalf("CellsByRoom = %v, want %v", got, want)
	}
}
