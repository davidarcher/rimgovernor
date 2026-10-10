package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"strings"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

func isSittable(row *d.ThingDef) bool { return Buildable(row) && row.GetBuilding().GetIsSittable() }

func isEatSurface(row *d.ThingDef) bool {
	return Buildable(row) && row.GetSurfaceType() == d.SurfaceType_SURFACE_TYPE_EAT
}

func TestDiningFurnitureIsARuleOverTheRows(t *testing.T) {
	slice := buildingsSlice(t)
	got, err := catalogOf(slice).DiningFurniture()
	if err != nil {
		t.Fatal(err)
	}
	// Comfort per cost: the stool leads the dining chair and the armchair.
	if got.Chair.Def != "Stool" || got.Table.Def != "Table1x2c" || got.Pin.Def != "HorseshoesPin" || got.Lane != 6 {
		t.Errorf("dining furniture %+v", got)
	}
	if got.Table.Size.X != 1 || got.Table.Size.Z != 2 {
		t.Errorf("table size %+v", got.Table.Size)
	}
	// A pricier-per-comfort stool gives the chair to the next best.
	slice.ScaleCosts("Stool", 100, 1)
	if got, err = catalogOf(slice).DiningFurniture(); err != nil || got.Chair.Def != "DiningChair" {
		t.Errorf("chair %+v, %v", got.Chair, err)
	}
}

func TestDiningFurnitureRefusesWhatItCannotFindOrLayOut(t *testing.T) {
	for name, test := range map[string]struct {
		strip func(*recordedrows.Slice)
		want  string
	}{
		"no chair": {func(s *recordedrows.Slice) { s.DropWhere(isSittable) }, "sittable"},
		"no table": {func(s *recordedrows.Slice) { s.DropWhere(isEatSurface) }, "eating surface"},
		"no pin":   {func(s *recordedrows.Slice) { s.Wire.Defs.JoyGiverDefs = nil }, "joy building"},
		"odd table": {func(s *recordedrows.Slice) {
			s.DropWhere(func(row *d.ThingDef) bool { return isEatSurface(row) && row.GetDefName() != "Table2x2c" })
		}, "2x2"},
	} {
		slice := buildingsSlice(t)
		test.strip(slice)
		_, err := catalogOf(slice).DiningFurniture()
		if err == nil || !strings.Contains(err.Error(), test.want) {
			t.Errorf("%s: %v, want an error naming %q", name, err, test.want)
		}
	}
	if _, err := (*DefinitionCatalog)(nil).DiningFurniture(); err == nil {
		t.Error("a nil catalog gave dining furniture")
	}
}
