package buildingruntime

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// TestClockHarvestAndStockRowsWakeTheirPlanners (#670): the native rows
// for a growing zone turning harvestable (colony, narrowed to zone ids, no
// rectangle) and for a stock crossing a declared level (colony, whole
// family) wake a step under the running window. The harvest row marks the
// id-keyed colony sections stale and wakes the zone readers without the
// colony-only planners; the stock row wakes every colony reader, the
// supply and resource planners among them.
func TestClockHarvestAndStockRowsWakeTheirPlanners(t *testing.T) {
	colony := []k.FactFamily{k.FactFamily_FACT_FAMILY_COLONY}
	noKind := func(domain.ActionID) (domain.ActionKind, bool) { return "", false }
	wake := func(o *k.ObservationInvalidated) (StepReason, []string) {
		t.Helper()
		page := clockFactsPage(clockFactsInvalidated(o))
		outcomes, families, authority, stopped, _ := clockPageWakeStopped(page)
		if len(outcomes) != 0 || authority || stopped || !reflect.DeepEqual(families, []bridge.FactFamily{bridge.FactColony}) {
			t.Fatalf("%s: outcomes=%v families=%v authority=%v stopped=%v", o.GetReason(), outcomes, families, authority, stopped)
		}
		reason := StepReason{Cause: StepWake, Families: families, Sections: clockPageSections(page)}
		ok, planners := selectedPlanners(reason, noKind)
		if !ok {
			t.Fatalf("%s: no planners selected", o.GetReason())
		}
		return reason, planners
	}

	harvest, harvestPlanners := wake(&k.ObservationInvalidated{Families: colony, EntityIds: []string{"Zone_12"}, Reason: proto.String("zones harvestable: Zone_12")})
	if !reflect.DeepEqual(harvest.Sections, []facts.Section{facts.Zones, facts.Buildings, facts.Bills}) {
		t.Fatalf("harvest sections %v", harvest.Sections)
	}
	for _, name := range []string{"fields", "haul", "foodStorageUpkeep"} {
		if !slices.Contains(harvestPlanners, name) {
			t.Fatalf("harvest row did not wake %s: %v", name, harvestPlanners)
		}
	}
	if slices.Contains(harvestPlanners, "supplies") {
		t.Fatalf("a narrowed harvest row woke the colony-only supplies planner: %v", harvestPlanners)
	}

	stock, stockPlanners := wake(&k.ObservationInvalidated{Families: colony, Reason: proto.String("stock WoodLog fell below its level")})
	if !reflect.DeepEqual(stock.Sections, facts.FamilySections(bridge.FactColony)) {
		t.Fatalf("stock sections %v", stock.Sections)
	}
	for _, name := range []string{"supplies", "resource", "foodAcquisition", "woodAcquisition", "resourceAcquisition"} {
		if !slices.Contains(stockPlanners, name) {
			t.Fatalf("stock row did not wake %s: %v", name, stockPlanners)
		}
	}

	// The fact store keeps the zones section's value and marks only the
	// ripe zone stale, so the next read is a delta over it.
	f := newClockFacts(nil, nil)
	scope := facts.Scope{Load: "l", Generation: 1}
	facts.Put(f.store, scope, facts.Zones, facts.Held[int]{Value: 3, AsOf: 1, Source: "z"})
	f.apply(clockFactsPage(clockFactsInvalidated(&k.ObservationInvalidated{Families: colony, EntityIds: []string{"Zone_12"}})))
	if held, ok := facts.Get[int](f.store, facts.Zones); !ok || held.Value != 3 || !reflect.DeepEqual(held.Stale.IDs, []string{"Zone_12"}) {
		t.Fatalf("zones after harvest row = %+v ok=%v", held, ok)
	}
}
