package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SiteCell.Equal lists every field by hand (a list field makes == illegal):
// a field added without it fails here.
func TestSiteCellEqualCoversEveryField(t *testing.T) {
	typ := reflect.TypeOf(SiteCell{})
	if typ.NumField() != 20 {
		t.Fatalf("SiteCell has %d fields, Equal was written for 20: extend Equal and this count", typ.NumField())
	}
	base := SiteCell{Cell: domain.Cell{X: 1}}
	if !base.Equal(base) {
		t.Fatal("a cell differs from itself")
	}
	other := base
	other.Things = []Thing{{Def: "Wall", Category: ThingBuilding, Building: &BuildingState{HitPoints: 1}}}
	if base.Equal(other) || other.Equal(base) {
		t.Fatal("things not compared")
	}
	same := other
	same.Things = []Thing{{Def: "Wall", Category: ThingBuilding, Building: &BuildingState{HitPoints: 1}}}
	if !other.Equal(same) {
		t.Fatal("equal things differ")
	}
	same.Things[0].Building.Burning = true
	if other.Equal(same) {
		t.Fatal("building state not compared")
	}
}
