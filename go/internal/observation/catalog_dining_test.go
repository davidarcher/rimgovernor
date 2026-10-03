package observation

import "testing"

// The dining chair and table are rules over the recorded game's rows: the
// sittable building with the most comfort per cost, and the smallest eating
// surface. The pin's lane is its row's watch stand distance plus its cell.
func TestRecordedCatalogDiningFurniture(t *testing.T) {
	catalog := recordedCatalog(t)
	if chair, err := catalog.DiningChair(); err != nil || chair != "Stool" {
		t.Errorf("chair %q, %v, want Stool", chair, err)
	}
	if table, err := catalog.DiningTable(); err != nil || table != "Table1x2c" {
		t.Errorf("table %q, %v, want Table1x2c", table, err)
	}
	pin := catalog.ThingDef("HorseshoesPin")
	if got := pin.GetBuilding().GetWatchBuildingStandDistanceRange().GetMax(); got != 5 {
		t.Errorf("pin stand distance %d, want 5 (lane 6)", got)
	}
}
