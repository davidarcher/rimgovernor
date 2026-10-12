package policy

import (
	"reflect"
	"testing"
)

// The perimeter around a tiled base: a tiled plan holds no hallways, so the
// ring is cut from its rooms alone. On every replay fixture the derived plan
// carries a ring (empty where the map seals the base) whose ground holds the shelter, which no room's footprint
// overlaps, and the same derive gives the same ring.
func TestPerimeterRingOverTiledFixtures(t *testing.T) {
	for _, f := range replayFixtures {
		if f.name == "hundred-colonists" {
			continue // the large fixture runs under the parity gate
		}
		t.Run(f.name, func(t *testing.T) {
			s := f.survey(t)
			plan, err := tiledParityPlan(f, s)
			if err != nil {
				t.Skipf("no tiled plan: %v", err)
			}
			if len(plan.Hallways()) != 0 {
				t.Fatalf("a tiled plan holds hallways: %+v", plan.Hallways())
			}
			ring, ok := plan.Ring()
			if !ok {
				t.Fatal("no ring")
			}
			occupied := map[any]string{}
			for _, c := range ring.Footprint.Cells() {
				occupied[c] = "ring"
			}
			for _, r := range plan.AllRooms() {
				for _, c := range r.Region.Cells() {
					if occupied[c] == "ring" {
						t.Errorf("room #%d (%s) floor cell %v lies on the ring", r.ID, r.Role, c)
					}
				}
			}
			for _, r := range plan.roomsOf(PlannedShelter) {
				if ring.Ring.Sealed.Empty() {
					break // no ring to draw: the map seals the base itself
				}
				for _, c := range r.Region.Cells() {
					if !ring.Ring.Sealed.Contains(c) {
						t.Fatalf("shelter cell %v lies outside the ring ground %v (opening %q, footprint %v, shelter %v)", c, ring.Ring.Sealed.Bounds(), ring.Ring.Opening, ring.Footprint.Bounds(), r.Region.Bounds())
					}
				}
			}
			again, err := tiledParityPlan(f, s)
			if err != nil {
				t.Fatal(err)
			}
			if got, _ := again.Ring(); !reflect.DeepEqual(got, ring) {
				t.Error("the ring is not deterministic")
			}
		})
	}
}
