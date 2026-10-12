package bridge

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Butchering spawns filth where the butcher stands (Corpse.ButcherProducts),
// and the defs name it only as the recipe's Butchery special product: the
// flow of the flesh and mechanoid butchering carries the flag, a station's
// other recipes (kibble) do not, and a node is filthy only while an order
// butchers at it.
func TestButcheryFlowsCarryTheFilthFlag(t *testing.T) {
	flows, err := sharedRecordedCatalog(t).StationRecipeFlows(recordedStations(t))
	if err != nil {
		t.Fatal(err)
	}
	for recipe, want := range map[string]bool{"ButcherCorpseFlesh": true, "ButcherCorpseMechanoid": true, "Make_Kibble": false, "Make_Shell_Smoke": false} {
		if flows[recipe].Butchery != want {
			t.Errorf("%s butchery flag %v, want %v", recipe, flows[recipe].Butchery, want)
		}
	}
	in := baselineGraphInput(t)
	in.Orders = []policy.OrderSpec{{Recipe: "Make_Shell_Smoke", BenchKind: "TableMachining", Mode: domain.StockTarget, Target: 4}, {Recipe: "Make_Kibble", BenchKind: "TableButcher", Mode: domain.StockTarget, Target: 4}}
	g, err := policy.BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	for _, n := range g.Nodes {
		if n.Filth {
			t.Errorf("%s is filthy without a butchering order", n.ID)
		}
	}
}

// A butcher order gives the butcher table its own cluster group, planned as the
// butchery, linked to the cold store of the corpses and the meat; the kitchen
// group is the one the colony has without the order.
func TestButcherOrderMakesAButcheryGroup(t *testing.T) {
	cluster := func(orders ...policy.OrderSpec) (policy.StationGraph, policy.Clustering) {
		in := baselineGraphInput(t)
		in.Orders = orders
		g, err := policy.BuildStationGraph(in)
		if err != nil {
			t.Fatal(err)
		}
		constants, err := sharedRecordedCatalog(t).GameConstants()
		if err != nil {
			t.Fatal(err)
		}
		got, err := policy.ClusterStations(policy.ClusterInput{Graph: g, Catalog: in.Catalog, RoofDistance: float64(constants.GetRoofCollapseUtility().GetRoofMaxSupportDistance())})
		if err != nil {
			t.Fatal(err)
		}
		return g, got
	}
	_, without := cluster()
	g, with := cluster(policy.OrderSpec{Recipe: "ButcherCorpseFlesh", BenchKind: "TableButcher", Mode: domain.StockTarget, Target: 4})
	table := "TableButcher/work"
	node, ok := g.Node(table)
	if !ok || node.Count != 1 {
		t.Fatalf("no butcher table node: %+v", g.Nodes)
	}
	var cold, dry string
	for _, n := range g.Nodes {
		switch n.Purpose {
		case policy.PurposeStoreCold:
			cold = n.ID
		case policy.PurposeStore:
			dry = n.ID
		}
	}
	if !g.Requires(cold, table) || g.Weight(cold, table) <= 0 || g.Weight(dry, table) <= 0 {
		t.Errorf("butcher flows: cold %v required %v, plain %v", g.Weight(cold, table), g.Requires(cold, table), g.Weight(dry, table))
	}
	var closet, kitchen, freezer policy.RoomGroup
	for _, grp := range with.Groups {
		if slices.ContainsFunc(grp.Stations, func(s policy.GroupStation) bool { return s.Node == table }) {
			closet = grp
		}
		if slices.ContainsFunc(grp.Stations, func(s policy.GroupStation) bool { return s.Node == cold }) {
			freezer = grp
		}
		if grp.Role == policy.RoomRoleKitchen && grp.Planned == "" {
			kitchen = grp
		}
	}
	if closet.Planned != policy.PlannedButchery || len(closet.Stations) != 1 || closet.ID == freezer.ID {
		t.Fatalf("butcher closet %+v", closet)
	}
	if !slices.ContainsFunc(closet.Links, func(l policy.GroupLink) bool { return l.To == freezer.ID && l.Required }) {
		t.Errorf("closet %+v is not linked to the group %d holding cold storage", closet.Links, freezer.ID)
	}
	if len(with.Groups) != len(without.Groups)+1 || kitchen.Role != policy.RoomRoleKitchen {
		t.Errorf("groups %d with the order, %d without; kitchen %+v", len(with.Groups), len(without.Groups), kitchen)
	}
	for _, grp := range without.Groups {
		if grp.Role == policy.RoomRoleKitchen && !slices.ContainsFunc(with.Groups, func(w policy.RoomGroup) bool {
			return w.Role == grp.Role && reflect.DeepEqual(w.Stations, grp.Stations)
		}) {
			t.Errorf("the kitchen group changed: %+v", grp)
		}
	}
	_, again := cluster(policy.OrderSpec{Recipe: "ButcherCorpseFlesh", BenchKind: "TableButcher", Mode: domain.StockTarget, Target: 4})
	if !reflect.DeepEqual(with, again) {
		t.Error("clustering changed")
	}
}

// The price of a butchering is read from the defs: blood's Cleanliness stat
// and the FoodPoisonChance curve, flat at zero from cleanliness -2 up.
func TestFilthModelReadsBloodAndFoodPoisonCurve(t *testing.T) {
	m := sharedRecordedCatalog(t).FilthModel()
	if m.Def != "Filth_Blood" || m.Cleanliness != -10 {
		t.Errorf("model %+v", m)
	}
	want := []policy.CurvePoint{{X: -5, Y: float64(float32(0.05))}, {X: -3.5, Y: float64(float32(0.025))}, {X: -2, Y: 0}}
	if !reflect.DeepEqual(m.FoodPoison, want) {
		t.Errorf("curve %v, want %v", m.FoodPoison, want)
	}
}
