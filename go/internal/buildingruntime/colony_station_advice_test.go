package buildingruntime

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// An unread colony skips the advice with the unknown fact named, never a
// guess; the plan is not touched.
func TestStationDecisionSkipsOnUnknownFacts(t *testing.T) {
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{{ID: 1, Role: policy.PlannedKitchen}}}
	facts := stationFactsOf(observation.ColonyProjection{}, plan, policy.MapSurvey{}, nil, policy.NewLedgerView(policy.LedgerViewNone, 0))
	d, _, held := stationDecision(facts, nil)
	if held {
		t.Error("an unread colony made advice")
	}
	if d.Kind != "layout_stations" || d.Verdict != "skipped" || d.Reason != "facts_unknown" {
		t.Fatalf("decision = %+v, want skipped/facts_unknown", d)
	}
	if len(plan.Rooms) != 1 || plan.Rooms[0].ID != 1 {
		t.Error("the plan changed")
	}
}

func TestStationFactsOfReadsLedgerOrdersOnlyWhenReconciled(t *testing.T) {
	var projection observation.ColonyProjection
	orders := []policy.LedgerOrderView{{Recipe: "Make", BenchKind: "Bench", Mode: string(domain.StockTarget), Target: 4}}
	reconciled := policy.NewLedgerView(policy.LedgerViewReconciled, 1)
	reconciled.Orders = orders
	unread := policy.NewLedgerView(policy.LedgerViewUnread, 1)
	unread.Orders = orders
	needs := map[policy.Resource]int64{"Steel": 10}

	f := stationFactsOf(projection, policy.LayoutPlan{}, policy.MapSurvey{}, needs, reconciled)
	got, known := f.Orders.Value()
	if !known || !reflect.DeepEqual(got, []policy.OrderSpec{{Recipe: "Make", BenchKind: "Bench", Mode: domain.StockTarget, Target: 4}}) {
		t.Errorf("reconciled orders = %+v, known %v", got, known)
	}
	if stock, known := f.Stock.Value(); !known || stock["Steel"] != 10 {
		t.Errorf("stock = %+v, known %v", stock, known)
	}
	f = stationFactsOf(projection, policy.LayoutPlan{}, policy.MapSurvey{}, nil, unread)
	if _, known := f.Orders.Value(); known {
		t.Error("orders of an unread ledger are unknown, not empty")
	}
	if _, known := f.Stock.Value(); known {
		t.Error("an empty demand is unknown, not zero stock")
	}
}

// The graph's fields follow the crops the colony's zones are sown with, each
// once: never a crop nobody sowed, never a building definition.
func TestGrowingCropsAreTheSownPlants(t *testing.T) {
	plant := func(name string) observation.PlanningDefinition {
		return observation.PlanningDefinition{Name: name, GrowDays: domain.Known(6.0)}
	}
	var projection observation.ColonyProjection
	projection.Definitions = []observation.PlanningDefinition{plant("Plant_Rice"), plant("Plant_Corn"), {Name: "Heater"}}
	projection.Farms = []observation.FarmZoneFact{{ID: "1", Crop: "Plant_Rice"}, {ID: "2", Crop: "Plant_Rice"}, {ID: "3"}}
	got := growingCrops(projection)
	if len(got) != 1 || got[0].Name != "Plant_Rice" {
		t.Errorf("growing crops %+v", got)
	}
}

func TestStationAdviceAttrsListGroupsAndEmbedReport(t *testing.T) {
	group := policy.RoomGroup{ID: 0, Role: policy.RoomRoleKitchen, Stations: []policy.GroupStation{{Node: "Stove/work", Count: 1}}, Footprint: 3, Clearance: 2, Area: 5,
		Costs: []policy.ConflictCost{{TicksPerDay: 100}, {TicksPerDay: 50}}}
	advice := policy.StationAdvice{
		Clustering: policy.Clustering{Groups: []policy.RoomGroup{group}, Decisions: []policy.ClusterDecision{{A: "a", B: "b", Conflict: policy.ConflictColdBand}, {A: "c", B: "d"}}},
		Groups:     []policy.GroupAdvice{{Group: group, Rooms: []int32{7}, Capacity: 4, Outgrown: true}},
		Embedding: policy.Embedding{
			Blocks:    []policy.EmbeddedBlock{{Group: 0, Interior: policy.Rectangle{X: 1, Z: 2, Width: 3, Height: 2}}},
			Decisions: []policy.EmbedDecision{{Group: 0, Other: -1, Verdict: policy.EmbedPlace}, {Group: 0, Other: 1, Verdict: policy.EmbedUnsatisfied, Reason: policy.ReasonEmbedLinkNotAdjacent}},
		},
	}
	attrs := stationAdviceAttrs(advice)
	groups := attrs["groups"].([]map[string]any)
	if len(groups) != 1 || groups[0]["role"] != "Kitchen" || groups[0]["cost_ticks_per_day"] != 150.0 || groups[0]["outgrown"] != true || !reflect.DeepEqual(groups[0]["rooms"], []int32{7}) {
		t.Errorf("groups = %+v", groups)
	}
	if _, ok := groups[0]["block"]; !ok {
		t.Error("placed group carries no block")
	}
	if got := attrs["conflicts"].([]map[string]any); len(got) != 1 || got[0]["conflict"] != policy.ConflictColdBand {
		t.Errorf("conflicts = %+v", got)
	}
	if issues := attrs["embed"].(map[string]any)["issues"].([]map[string]any); len(issues) != 1 || issues[0]["reason"] != policy.ReasonEmbedLinkNotAdjacent {
		t.Errorf("embed issues = %+v", issues)
	}
}
