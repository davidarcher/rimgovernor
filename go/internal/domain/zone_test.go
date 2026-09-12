package domain

import "testing"

func TestZoneCanonicalConnectedAndImmutable(t *testing.T) {
	cells := []Cell{{X: 2, Z: 1}, {X: 1, Z: 1}}
	z, err := NewZoneCreate(GrowingZone, "Plant_Rice", cells)
	if err != nil {
		t.Fatal(err)
	}
	cells[0].X = 8
	got := z.Cells()
	got[0].X = 9
	same, err := NewZoneCreate(GrowingZone, "Plant_Rice", []Cell{{X: 1, Z: 1}, {X: 2, Z: 1}})
	if err != nil || z != same {
		t.Fatal("mutable/canonical", z, err)
	}
	for _, bad := range [][]Cell{nil, {{X: 1, Z: 1}, {X: 1, Z: 1}}, {{X: 1, Z: 1}, {X: 3, Z: 1}}, {{X: -1, Z: 0}}} {
		if _, err := NewZoneCreate(GrowingZone, "Plant_Rice", bad); err == nil {
			t.Fatal(bad)
		}
	}
}
