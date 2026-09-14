package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestResourceRecipeDeficitsRejectsUnknownIngredients(t *testing.T) {
	if _, err := ResourceRecipeDeficits(domain.Unknown[[][]Amount](), nil); err == nil {
		t.Fatalf("expected an error for unknown ingredients")
	}
}

func TestResourceRecipeDeficitsComputesPerAlternativeDeficit(t *testing.T) {
	ingredients := domain.Known([][]Amount{
		{{Resource: "Steel", Count: 50}, {Resource: "Plasteel", Count: 20}},
		{{Resource: "WoodLog", Count: 10}},
	})
	stock := map[Resource]int64{"Steel": 30, "Plasteel": 25, "WoodLog": 4}
	got, err := ResourceRecipeDeficits(ingredients, stock)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	want := [][]ResourceRequirement{
		{{Resource: "Steel", Required: 50, Deficit: 20}, {Resource: "Plasteel", Required: 20, Deficit: 0}},
		{{Resource: "WoodLog", Required: 10, Deficit: 6}},
	}
	if len(got) != len(want) {
		t.Fatalf("got %v want %v", got, want)
	}
	for i := range want {
		if len(got[i]) != len(want[i]) {
			t.Fatalf("slot %d: got %v want %v", i, got[i], want[i])
		}
		for j := range want[i] {
			if got[i][j] != want[i][j] {
				t.Fatalf("slot %d choice %d: got %v want %v", i, j, got[i][j], want[i][j])
			}
		}
	}
}

func TestResourceRecipeDeficitsMissingStockTreatedAsZero(t *testing.T) {
	ingredients := domain.Known([][]Amount{{{Resource: "Uranium", Count: 5}}})
	got, err := ResourceRecipeDeficits(ingredients, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got[0][0].Deficit != 5 {
		t.Fatalf("got %v", got)
	}
}

func TestResourceExtractionAdvancedDetectsMiningProgress(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	current := []MiningProgress{{ThingID: "rock1", HitPoints: 80}}
	if !ResourceExtractionAdvanced(10, 5, current, prior, nil, nil) {
		t.Fatalf("expected mining progress to be detected")
	}
}

func TestResourceExtractionAdvancedDetectsDrillingProgress(t *testing.T) {
	prior := []DrillingProgress{{ThingID: "drill1", Progress: 0.2}}
	current := []DrillingProgress{{ThingID: "drill1", Progress: 0.5}}
	if !ResourceExtractionAdvanced(10, 5, nil, nil, current, prior) {
		t.Fatalf("expected drilling progress to be detected")
	}
}

func TestResourceExtractionAdvancedNoChange(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	current := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	if ResourceExtractionAdvanced(10, 5, current, prior, nil, nil) {
		t.Fatalf("expected no progress")
	}
}

func TestResourceExtractionAdvancedIncreaseInHitPointsIsNotProgress(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 50}}
	current := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	if ResourceExtractionAdvanced(10, 5, current, prior, nil, nil) {
		t.Fatalf("expected an increase in hit points to not count as progress")
	}
}

func TestResourceExtractionAdvancedRejectsStaleTick(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	current := []MiningProgress{{ThingID: "rock1", HitPoints: 10}}
	if ResourceExtractionAdvanced(4, 5, current, prior, nil, nil) {
		t.Fatalf("expected a tick before the last observed progress to never establish an advance")
	}
}

func TestResourceExtractionAdvancedUnseenIdentityIsNotProgress(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	current := []MiningProgress{{ThingID: "rock2", HitPoints: 10}}
	if ResourceExtractionAdvanced(10, 5, current, prior, nil, nil) {
		t.Fatalf("expected a newly observed identity with no prior baseline to not count as progress")
	}
}

func TestSelectResourceSourcesNoDeficitSelectsNothing(t *testing.T) {
	sources := []ResourceSource{{ThingID: "a", Yield: 50, Distance: 1}}
	if got := SelectResourceSources(sources, 100, 100, 0); got != nil {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesAccountsForStockAndPending(t *testing.T) {
	sources := []ResourceSource{{ThingID: "a", Yield: 10, Distance: 1}}
	// target 100, stock 95, pending 5 -> needed 0
	if got := SelectResourceSources(sources, 100, 95, 5); got != nil {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesOrdersByDistanceThenThingID(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "far", Yield: 100, Distance: 10},
		{ThingID: "near", Yield: 100, Distance: 1},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "near" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesSkipsDesignatedAndZeroYield(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "designated", Yield: 100, Distance: 1, Designated: true},
		{ThingID: "empty", Yield: 0, Distance: 2},
		{ThingID: "usable", Yield: 100, Distance: 3},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "usable" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesCombinesMultipleUntilCovered(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "a", Yield: 30, Distance: 1},
		{ThingID: "b", Yield: 30, Distance: 2},
		{ThingID: "c", Yield: 30, Distance: 3},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 2 || got[0].ThingID != "a" || got[1].ThingID != "b" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesMineRequiresOpenSurfaceSafety(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "unsafe", Yield: 100, Distance: 1, Method: ResourceSourceMine, Safety: "unknown"},
		{ThingID: "safe", Yield: 100, Distance: 2, Method: ResourceSourceMine, Safety: "open_surface"},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "safe" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesAtMostOneMinePerCall(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "mine1", Yield: 10, Distance: 1, Method: ResourceSourceMine, Safety: "open_surface"},
		{ThingID: "mine2", Yield: 10, Distance: 2, Method: ResourceSourceMine, Safety: "open_surface"},
		{ThingID: "surface", Yield: 100, Distance: 3},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "mine1" {
		t.Fatalf("expected only the nearest mine source and nothing after it, got %v", got)
	}
}

func TestSelectResourceSourcesMineNeverFollowsAnyPriorSelection(t *testing.T) {
	// A mine source is only ever added when nothing has been selected yet --
	// ordinary sources selected first stop the loop before a later mine
	// source is reached, matching production_policy.py's
	// "if method == 'mine' and selected: break".
	sources := []ResourceSource{
		{ThingID: "surface", Yield: 10, Distance: 1},
		{ThingID: "mine1", Yield: 100, Distance: 2, Method: ResourceSourceMine, Safety: "open_surface"},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "surface" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesCapAtEight(t *testing.T) {
	var sources []ResourceSource
	for i := 0; i < 12; i++ {
		sources = append(sources, ResourceSource{ThingID: string(rune('a' + i)), Yield: 1, Distance: float64(i)})
	}
	got := SelectResourceSources(sources, 1000, 0, 0)
	if len(got) != 8 {
		t.Fatalf("got %d sources", len(got))
	}
}

func TestSelectResourceStorageZoneSkipsWithoutMineSelection(t *testing.T) {
	selected := []ResourceSource{{ThingID: "wood1", Yield: 40, Method: "cut"}}
	storage := ResourceStorage{Capacity: 0, StackLimit: 75, Haulers: 0}
	zone, needed, blocked, err := SelectResourceStorageZone(selected, 0, storage)
	if err != nil || needed || blocked || len(zone.Cells) != 0 {
		t.Fatalf("got zone=%v needed=%v blocked=%v err=%v", zone, needed, blocked, err)
	}
}

func TestSelectResourceStorageZoneSkipsWhenCapacityAlreadyCovers(t *testing.T) {
	selected := []ResourceSource{{ThingID: "rock1", Yield: 40, Method: ResourceSourceMine}}
	storage := ResourceStorage{Capacity: 100, StackLimit: 75, Haulers: 1}
	zone, needed, blocked, err := SelectResourceStorageZone(selected, 0, storage)
	if err != nil || needed || blocked || len(zone.Cells) != 0 {
		t.Fatalf("got zone=%v needed=%v blocked=%v err=%v", zone, needed, blocked, err)
	}
}

func TestSelectResourceStorageZoneBlockedWithoutHaulersEvenWhenCapacitySuffices(t *testing.T) {
	// Mirrors production_policy.py's resource_method: the hauler check fires
	// unconditionally whenever a mine source is selected, before capacity is
	// even considered.
	selected := []ResourceSource{{ThingID: "rock1", Yield: 40, Method: ResourceSourceMine}}
	storage := ResourceStorage{Capacity: 1000, StackLimit: 75, Haulers: 0}
	zone, needed, blocked, err := SelectResourceStorageZone(selected, 0, storage)
	if err != nil || needed || !blocked || len(zone.Cells) != 0 {
		t.Fatalf("got zone=%v needed=%v blocked=%v err=%v", zone, needed, blocked, err)
	}
}

func TestSelectResourceStorageZoneBuildsShortfallCells(t *testing.T) {
	selected := []ResourceSource{{ThingID: "rock1", Yield: 100, Method: ResourceSourceMine}}
	storage := ResourceStorage{Capacity: 20, StackLimit: 30, Haulers: 1,
		Candidates: []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 2}, {X: 3, Z: 3}, {X: 4, Z: 4}}}
	// capacityNeeded = 100 + pending(10) = 110; shortfall = 90; cellsNeeded = ceil(90/30) = 3
	zone, needed, blocked, err := SelectResourceStorageZone(selected, 10, storage)
	if err != nil || !needed || blocked {
		t.Fatalf("got zone=%v needed=%v blocked=%v err=%v", zone, needed, blocked, err)
	}
	if len(zone.Cells) != 3 || zone.Cells[0] != (domain.Cell{X: 1, Z: 1}) || zone.Cells[2] != (domain.Cell{X: 3, Z: 3}) {
		t.Fatalf("got cells %v", zone.Cells)
	}
}

func TestSelectResourceStorageZoneBlockedWhenNoCandidatesCoverShortfall(t *testing.T) {
	selected := []ResourceSource{{ThingID: "rock1", Yield: 100, Method: ResourceSourceMine}}
	storage := ResourceStorage{Capacity: 0, StackLimit: 30, Haulers: 1}
	zone, needed, blocked, err := SelectResourceStorageZone(selected, 0, storage)
	if err != nil || needed || !blocked || len(zone.Cells) != 0 {
		t.Fatalf("got zone=%v needed=%v blocked=%v err=%v", zone, needed, blocked, err)
	}
}

func TestSelectResourceStorageZoneRejectsInvalidInput(t *testing.T) {
	selected := []ResourceSource{{ThingID: "rock1", Yield: 10, Method: ResourceSourceMine}}
	if _, _, _, err := SelectResourceStorageZone(selected, -1, ResourceStorage{StackLimit: 1, Haulers: 1}); err == nil {
		t.Fatal("expected rejection of negative pending")
	}
	if _, _, _, err := SelectResourceStorageZone(selected, 0, ResourceStorage{Capacity: -1, StackLimit: 1, Haulers: 1}); err == nil {
		t.Fatal("expected rejection of negative capacity")
	}
}

func TestSelectResourceTargetNoConfigOrUnknownStockSelectsNothing(t *testing.T) {
	if _, _, ok, err := SelectResourceTarget(nil, domain.Known([]Amount{{"Steel", 0}})); err != nil || ok {
		t.Fatalf("expected no target with no configured resources, got ok=%v err=%v", ok, err)
	}
	targets := map[Resource]int64{"Steel": 100}
	if _, _, ok, err := SelectResourceTarget(targets, domain.Unknown[[]Amount]()); err != nil || ok {
		t.Fatalf("expected no target with unknown stock, got ok=%v err=%v", ok, err)
	}
}

func TestSelectResourceTargetEverythingCoveredSelectsNothing(t *testing.T) {
	targets := map[Resource]int64{"Steel": 100, "WoodLog": 50}
	stock := domain.Known([]Amount{{"Steel", 100}, {"WoodLog", 200}})
	if _, _, ok, err := SelectResourceTarget(targets, stock); err != nil || ok {
		t.Fatalf("expected no deficit to select, got ok=%v err=%v", ok, err)
	}
}

func TestSelectResourceTargetPicksWorstProportionalDeficit(t *testing.T) {
	// Steel: 80/100 short by 20% ; Plasteel: 10/50 short by 80% -> Plasteel wins
	// even though its absolute deficit (40) is smaller than Steel's (20)? Here
	// it is also larger, so pick a case where proportion and absolute amount
	// disagree to prove proportion (not absolute deficit) drives selection.
	targets := map[Resource]int64{"Steel": 1000, "Plasteel": 50}
	stock := domain.Known([]Amount{{"Steel", 500}, {"Plasteel", 10}})
	resource, target, ok, err := SelectResourceTarget(targets, stock)
	if err != nil || !ok || resource != "Plasteel" || target != 50 {
		t.Fatalf("got %v %v %v %v", resource, target, ok, err)
	}
}

func TestSelectResourceTargetMissingStockTreatedAsFullyUnstocked(t *testing.T) {
	targets := map[Resource]int64{"Components": 10}
	resource, target, ok, err := SelectResourceTarget(targets, domain.Known([]Amount{}))
	if err != nil || !ok || resource != "Components" || target != 10 {
		t.Fatalf("got %v %v %v %v", resource, target, ok, err)
	}
}

func TestSelectResourceTargetRejectsInvalidConfigOrStock(t *testing.T) {
	if _, _, _, err := SelectResourceTarget(map[Resource]int64{"Steel": 0}, domain.Known([]Amount{})); err == nil {
		t.Fatalf("expected an error for a non-positive target")
	}
	if _, _, _, err := SelectResourceTarget(map[Resource]int64{"Steel": 100}, domain.Known([]Amount{{"Steel", -1}})); err == nil {
		t.Fatalf("expected an error for negative stock")
	}
	if _, _, _, err := SelectResourceTarget(map[Resource]int64{"Steel": 100}, domain.Known([]Amount{{"Steel", 1}, {"Steel", 2}})); err == nil {
		t.Fatalf("expected an error for duplicate stock rows")
	}
}

func resourceMethodFixture() ResourceMethodRequest {
	recipe := GearRecipe{Definition: "Smelt", Products: []Resource{"Steel"}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]Amount{{{"Slag", 5}}}), RequiredWork: domain.Known([]WorkRequirement{})}
	bench := GearBench{ID: "bench", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{recipe})}
	return ResourceMethodRequest{Resource: "Steel", Target: 100, Benches: domain.Known([]GearBench{bench}), Stock: []Stock{{"Slag", domain.Known(int64(50))}}}
}

func TestSelectResourceMethodProducesFundedRecipeThenWaitsOnceSeen(t *testing.T) {
	r := resourceMethodFixture()
	method, err := SelectResourceMethod(r)
	if err != nil || method.Kind != ResourceMethodProduce || method.Bench != "bench" || method.Recipe != "Smelt" || method.Resource != "Steel" || method.Target != 100 {
		t.Fatalf("got %v %v", method, err)
	}
	r.Seen = []domain.MethodID{method.ID}
	waiting, err := SelectResourceMethod(r)
	if err != nil || waiting.Kind != ResourceMethodWait {
		t.Fatalf("previously seen resource method must not repeat: %v %v", waiting, err)
	}
}

func TestSelectResourceMethodInvalidResourceOrTargetIsUnknown(t *testing.T) {
	r := resourceMethodFixture()
	r.Resource = ""
	if method, err := SelectResourceMethod(r); err != nil || method.Kind != ResourceMethodUnknown {
		t.Fatalf("got %v %v", method, err)
	}
	r = resourceMethodFixture()
	r.Target = 0
	if method, err := SelectResourceMethod(r); err != nil || method.Kind != ResourceMethodUnknown {
		t.Fatalf("got %v %v", method, err)
	}
	r = resourceMethodFixture()
	r.Target = 20000
	if method, err := SelectResourceMethod(r); err != nil || method.Kind != ResourceMethodUnknown {
		t.Fatalf("got %v %v", method, err)
	}
}

func TestSelectResourceMethodExistingActiveBillWaits(t *testing.T) {
	r := resourceMethodFixture()
	v, _ := r.Benches.Value()
	v[0].Bills = domain.Known([]GearBill{{Active: domain.Known(true), Products: []Resource{"Steel"}}})
	r.Benches = domain.Known(v)
	method, err := SelectResourceMethod(r)
	if err != nil || method.Kind != ResourceMethodWait {
		t.Fatalf("got %v %v", method, err)
	}
}

func TestSelectResourceMethodUnfundedRecipeIsBlocked(t *testing.T) {
	r := resourceMethodFixture()
	r.Stock = []Stock{{"Slag", domain.Known(int64(2))}}
	method, err := SelectResourceMethod(r)
	if err != nil || method.Kind != ResourceMethodBlocked {
		t.Fatalf("got %v %v", method, err)
	}
}

func TestSelectResourceMethodNoRecipeProducingResourceIsBlocked(t *testing.T) {
	r := resourceMethodFixture()
	r.Resource = "Plasteel"
	method, err := SelectResourceMethod(r)
	if err != nil || method.Kind != ResourceMethodBlocked {
		t.Fatalf("got %v %v", method, err)
	}
}

func TestSelectResourceMethodUnknownBenchesRefuseGuessing(t *testing.T) {
	r := resourceMethodFixture()
	r.Benches = domain.Unknown[[]GearBench]()
	if method, err := SelectResourceMethod(r); err != nil || method.Kind != ResourceMethodUnknown {
		t.Fatalf("got %v %v", method, err)
	}
}

func TestProductionFloorsOmitsZeroReserves(t *testing.T) {
	floors, stopped, err := ProductionFloors(map[Resource]int64{"Steel": 50, "WoodLog": 0}, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(floors) != 1 || floors["Steel"] != 50 {
		t.Fatalf("got %v", floors)
	}
	if len(stopped) != 0 {
		t.Fatalf("got %v", stopped)
	}
}

func TestProductionFloorsSortsStopped(t *testing.T) {
	_, stopped, err := ProductionFloors(nil, []Resource{"WoodLog", "Steel"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(stopped) != 2 || stopped[0] != "Steel" || stopped[1] != "WoodLog" {
		t.Fatalf("got %v", stopped)
	}
}

func TestProductionFloorsRejectsDuplicateStopped(t *testing.T) {
	if _, _, err := ProductionFloors(nil, []Resource{"Steel", "Steel"}); err == nil {
		t.Fatalf("expected an error for duplicate stopped resource")
	}
}

func TestProductionFloorsRejectsInvalidReserve(t *testing.T) {
	if _, _, err := ProductionFloors(map[Resource]int64{"Steel": -1}, nil); err == nil {
		t.Fatalf("expected an error for negative reserve")
	}
	if _, _, err := ProductionFloors(map[Resource]int64{"": 5}, nil); err == nil {
		t.Fatalf("expected an error for invalid resource name")
	}
}

func TestProductionFloorsEmptyInputsAreValid(t *testing.T) {
	floors, stopped, err := ProductionFloors(nil, nil)
	if err != nil || len(floors) != 0 || len(stopped) != 0 {
		t.Fatalf("got %v %v %v", floors, stopped, err)
	}
}
