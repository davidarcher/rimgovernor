package domain

import "testing"

func TestFishingZoneCreateAndExtend(t *testing.T) {
	cells := []Cell{{X: 1, Z: 1}, {X: 1, Z: 2}}
	created, err := NewFishingZone(cells)
	if err != nil {
		t.Fatal(err)
	}
	extended, err := NewFishingZoneExtension("Zone_4", cells)
	if err != nil {
		t.Fatal(err)
	}
	for _, z := range []ZoneCreate{created, extended} {
		copy, err := ReconstructZone(z)
		if err != nil || copy != z || z.Kind() != FishingZone || z.Label() != "RimGovernor fishing" {
			t.Fatal(z, err)
		}
		if _, err := NewZoneCreateAction("fish", z); err != nil {
			t.Fatal(err)
		}
	}
	if extended.ExtendZoneID() != "Zone_4" || created.ExtendZoneID() != "" || FishingPopulationFloor != .6 {
		t.Fatal("fishing configuration")
	}
	if _, err := NewFishingZoneExtension("", cells); err == nil {
		t.Fatal("missing target accepted")
	}
	if _, err := NewFishingZone([]Cell{{X: 1, Z: 1}, {X: 8, Z: 8}}); err == nil {
		t.Fatal("disconnected access")
	}
}
