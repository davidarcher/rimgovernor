package bridge

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// baselineClustering clusters the baseline colony's station graph on the
// recorded catalog, with a machining order so the colony has a workshop.
func baselineClustering(t *testing.T) (policy.StationGraphInput, policy.Clustering) {
	t.Helper()
	in := baselineGraphInput(t)
	in.Orders = []policy.OrderSpec{{Recipe: "Make_Shell_Smoke", BenchKind: "TableMachining", Mode: domain.StockTarget, Target: 4}}
	g, err := policy.BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	constants, err := sharedRecordedCatalog(t).GameConstants()
	if err != nil {
		t.Fatal(err)
	}
	got, err := policy.ClusterStations(policy.ClusterInput{
		Graph: g, Catalog: in.Catalog,
		RoofDistance: float64(constants.GetRoofCollapseUtility().GetRoofMaxSupportDistance()),
	})
	if err != nil {
		t.Fatal(err)
	}
	return in, got
}

// The baseline colony's groups come from its stations alone and cover the
// room types the layout names, except where a role has no gameplay effect to
// protect: the dining room and the storeroom beside a bench share a room
// with the stove or the bench that outweighs them (room types kept
// those apart).
//
//	kitchen   stove + table + cold shelf + cooler -> Kitchen, cold (old: kitchen,
//	          dining, freezer; the cold shelf joins the stove it has a required
//	          link to, the temperature-band conflict is recorded not refused)
//	workshop  bench + dry shelves    -> Workshop  (old: workshop + storage)
//	bedrooms  one bed per group      -> Bedroom x6
//	hospital  medical bed            -> Hospital
//	prison    prisoner bed           -> PrisonCell
func TestBaselineClustering(t *testing.T) {
	_, got := baselineClustering(t)
	want := map[policy.RoomRole]int{
		policy.RoomRoleKitchen: 1, policy.RoomRoleWorkshop: 1,
		policy.RoomRoleBedroom: 6, policy.RoomRoleHospital: 1, policy.RoomRolePrisonCell: 1,
	}
	have := map[policy.RoomRole]int{}
	circulation, outskirts := 0, 0
	for _, g := range got.Groups {
		if g.Planned == policy.PlannedCirculation {
			circulation++ // seven sleeping rooms outnumber the three public ones (#2911)
			continue
		}
		switch g.Planned {
		case policy.PlannedTomb, policy.PlannedMorgue, policy.PlannedGraveyard, policy.PlannedWasteYard, policy.PlannedIncinerator:
			outskirts++ // the tomb, morgue, graveyard, waste yard and incinerator (#2882)
			continue
		}
		have[g.Role]++
	}
	if circulation != 1 || outskirts != 5 {
		t.Errorf("%d circulation corridors, %d outskirts rooms, want 1 and 5", circulation, outskirts)
	}
	if !reflect.DeepEqual(have, want) {
		t.Errorf("roles %v, want %v\n%+v", have, want, got.Groups)
	}
	var kitchen policy.RoomGroup
	for _, g := range got.Groups {
		if g.Role == policy.RoomRoleKitchen {
			kitchen = g
		}
	}
	if len(kitchen.Stations) != 4 || !slices.Contains(kitchen.Requires, policy.EnvCold) || !slices.ContainsFunc(kitchen.Stations, func(s policy.GroupStation) bool { return s.Purpose == policy.PurposeCool }) {
		t.Errorf("kitchen holds %+v (needs %v), want the stove, table, cold shelf and a cooler", kitchen.Stations, kitchen.Requires)
	}
	for _, d := range got.Decisions {
		if d.Verdict == policy.ClusterUnsatisfied {
			t.Errorf("unsatisfied: %+v", d)
		}
	}
	for _, g := range got.Groups {
		if g.Area <= 0 || g.Area != g.Footprint+g.Clearance {
			t.Errorf("group %d area %+v", g.ID, g)
		}
	}
}

// Clustering is a pure function of the graph.
func TestBaselineClusteringDeterministic(t *testing.T) {
	_, first := baselineClustering(t)
	for range 3 {
		if _, again := baselineClustering(t); !reflect.DeepEqual(first, again) {
			t.Fatal("clustering changed")
		}
	}
}
