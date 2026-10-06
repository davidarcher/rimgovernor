package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// animalFeedRoundsRequest opens a MaintainAnimalFeed deficit, a goal the
// one-zone stockpile methods bind to.
func animalFeedRoundsRequest() RoundsRequest {
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.AnimalUpkeep = policy.AnimalUpkeepObservation{
		Animals: domain.Known([]policy.UpkeepAnimal{{ID: "animal", Definition: "Husky", RequiresPen: domain.Known(false), Contained: domain.Known(true), Release: domain.Known(false), Slaughter: domain.Known(false)}}),
		Food:    domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "animal", NutritionPerDay: domain.Known(1.0)}}}),
	}
	return r
}

func stockpilePlan(t *testing.T, id domain.PlanID, cells ...[]domain.Cell) domain.PlanSpec {
	t.Helper()
	var actions []domain.Action
	for i, block := range cells {
		zone, err := domain.NewFilteredStockpileZone(domain.FoodFilter(), domain.ImportantPriority, block)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewZoneCreateAction(domain.ActionID(string(id)+"-"+string(rune('a'+i))), zone)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

func growingPlan(t *testing.T, id domain.PlanID, cells []domain.Cell) domain.PlanSpec {
	t.Helper()
	return growingCropPlan(t, id, "Plant_Rice", cells)
}

func growingCropPlan(t *testing.T, id domain.PlanID, crop string, cells []domain.Cell) domain.PlanSpec {
	t.Helper()
	zone, err := domain.NewZoneCreate(domain.GrowingZone, crop, cells)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction(domain.ActionID(string(id)+"-a"), zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

// A zone method committed under MaintainResource must bind to the resource
// target goal; the production ladder once stalled on "plan or action
// identity already exists" at this admission before the goal was bound (#155).
func TestCommitStockpileZoneMethodBindsToResourceTargetGoal(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Policy.ResourceTargets = map[policy.Resource]int64{"MeleeWeapon_Gladius": 3}
	r.Facts.Resources = domain.Known([]policy.Amount{})
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainResource)
	if g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	zone, err := allowListZone(domain.ImportantPriority, []string{"Steel"}, []domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction("ingredient-zone-a", zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("ingredient-zone", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "ingredient-storage-0", plan); err != nil {
		t.Fatal(err)
	}
}

// A resource's field is a growing zone of any crop under MaintainResource
// (#2285); the social crops keep their brewing gate.
func TestCommitResourceFieldZoneUnderMaintainResource(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Policy.ResourceTargets = map[policy.Resource]int64{"Cloth": 30}
	r.Facts.Resources = domain.Known([]policy.Amount{})
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainResource)
	cells := []domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}}
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "hops", growingCropPlan(t, "hops-plan", "Plant_Hops", cells)); err == nil {
		t.Fatal("a social field committed before brewing is finished")
	}
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "cotton", growingCropPlan(t, "cotton-plan", "Plant_Cotton", cells)); err != nil {
		t.Fatal("a cotton field is a resource field", err)
	}
}

// MaintainAnimalFeed's delivery fallback (rounds_animal_feed.go) commits one
// kibble-only stockpile inside the animals' area when no bench is reachable
// there; the live run refused it here with "plan or action identity already
// exists" until the goal was bound (#311).
func TestCommitStockpileZoneMethodBindsToAnimalFeedGoal(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Facts.AnimalUpkeep = policy.AnimalUpkeepObservation{
		Animals: domain.Known([]policy.UpkeepAnimal{{ID: "animal", Definition: "Husky", RequiresPen: domain.Known(false), Contained: domain.Known(true), Release: domain.Known(false), Slaughter: domain.Known(false)}}),
		Food:    domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "animal", NutritionPerDay: domain.Known(1.0)}}}),
	}
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainAnimalFeed)
	if g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	zone, err := allowListZone(domain.ImportantPriority, []string{"Kibble"}, []domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction("feed-zone-a", zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("feed-zone", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "feed-storage-0", plan); err != nil {
		t.Fatal(err)
	}
}

// A stockpile zone is created once per colony in this slice: a plan proposing
// more than one stockpile action for the bound goal must be refused, unlike
// growing-field plans which may batch many patches per method.
func TestCommitStockpileZoneMethodCappedAtOneAction(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := animalFeedRoundsRequest()
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainAnimalFeed)
	plan := stockpilePlan(t, "storage-plan", []domain.Cell{{X: 4, Z: 6}}, []domain.Cell{{X: 8, Z: 6}})
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "food-storage", plan); err == nil {
		t.Fatal("expected rejection of multi-action stockpile plan")
	}
}

// MaintainStockpiles admits the opening stockpiles (general, food, dump,
// weapons) together as one method; the live colony refused every such
// create here with ErrConflict until the need was bound and uncapped.
func TestCommitOpeningStockpilesBindToStockpilesGoal(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.Stockpiles = domain.Known(policy.StockpileReview{Known: true, Active: true})
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainStockpiles)
	if g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	plan := stockpilePlan(t, "opening-plan", []domain.Cell{{X: 4, Z: 6}}, []domain.Cell{{X: 8, Z: 6}}, []domain.Cell{{X: 12, Z: 6}}, []domain.Cell{{X: 16, Z: 6}})
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "stockpile-create", plan); err != nil {
		t.Fatal(err)
	}
}

// A goal bound only to MaintainAnimalFeed must not admit a growing-zone plan:
// admitZoneMethod requires the plan's own zone kind to match the need the
// goal was actually bound under, not just any zone-create action family.
func TestCommitZoneMethodRejectsKindGoalMismatch(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := animalFeedRoundsRequest()
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainAnimalFeed)
	plan := growingPlan(t, "fields-plan", []domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}})
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "fields", plan); err == nil {
		t.Fatal("expected rejection of growing zone bound to food storage goal")
	}
}

// The allow-list (NothingPreset) stockpile variant persists and reloads its
// definition allow-list exactly, exercising insertAction/scanAction's new
// zone payload the same way the food-preset plans above exercise
// the rest of zonePayload.
func TestCommitAllowListStockpileZoneMethodRoundTrips(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := animalFeedRoundsRequest()
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainAnimalFeed)
	zone, err := allowListZone(domain.ImportantPriority, []string{"MealSimple", "MealFine"}, []domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}, {X: 6, Z: 6}, {X: 4, Z: 7}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction("storage-plan-a", zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("storage-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "food-storage", plan); err != nil {
		t.Fatal(err)
	}
	loaded, err := s.LoadPlan(ctx, "storage-plan")
	if err != nil {
		t.Fatal(err)
	}
	if len(loaded.Spec.Actions()) != 1 {
		t.Fatal("expected one reloaded action", loaded.Spec)
	}
	got, ok := loaded.Spec.Actions()[0].ZoneCreate()
	if !ok || got != zone {
		t.Fatal("allow-list zone did not round-trip", got, zone)
	}
}

func TestCommitStockpileZoneMethodRejectsOverlappingCells(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := animalFeedRoundsRequest()
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainAnimalFeed)
	zone1, err := domain.NewFilteredStockpileZone(domain.FoodFilter(), domain.ImportantPriority, []domain.Cell{{X: 4, Z: 6}})
	if err != nil {
		t.Fatal(err)
	}
	zone2, err := domain.NewFilteredStockpileZone(domain.FoodFilter(), domain.ImportantPriority, []domain.Cell{{X: 4, Z: 6}})
	if err != nil {
		t.Fatal(err)
	}
	a1, err := domain.NewZoneCreateAction("storage-plan-a", zone1)
	if err != nil {
		t.Fatal(err)
	}
	a2, err := domain.NewZoneCreateAction("storage-plan-b", zone2)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("storage-plan", 1, []domain.Action{a1, a2})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "food-storage", plan); err == nil {
		t.Fatal("expected rejection of overlapping stockpile cells")
	}
}

// A dispatched acquisition batch is refreshed every review and must not
// starve the field planner: a growing-zone method commits alongside open
// acquisition work, while a second field batch still waits for the first.
func TestCommitFieldMethodExemptFromAcquisitionOpenWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoundsRequest()
	tick := r.Tick
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.EnsureFoodSupply)
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "acquire-1", acquisitionPlan(t, "acquire-plan-1", "WoodLog")); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "acquire-plan-1", 1
	if _, err := s.Prepare(ctx, "acquire-plan-1", "acquire-plan-1-a", target, tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "acquire-plan-1", "acquire-plan-1-a", target, tick); err != nil {
		t.Fatal(err)
	}
	g, err := s.LoadStandard(ctx, g.Standard.ID)
	if err != nil {
		t.Fatal(err)
	}
	field := growingPlan(t, "field-plan-1", []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})
	if g, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "field-1", field); err != nil {
		t.Fatal("open acquisition blocked a field method", err)
	}
	// The committed field batch is open until its zone resolves, so another
	// field batch waits.
	second := growingPlan(t, "field-plan-2", []domain.Cell{{X: 5, Z: 5}})
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "field-2", second); err == nil {
		t.Fatal("open field work did not block a second field batch")
	}
}

// A field batch made of farm infrastructure (a sun lamp) shares the
// exemption; any other building does not.
func TestCommitFieldInfrastructureExemptFromAcquisitionOpenWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoundsRequest()
	tick := r.Tick
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.EnsureFoodSupply)
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "acquire-1", acquisitionPlan(t, "acquire-plan-1", "WoodLog")); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "acquire-plan-1", 1
	if _, err := s.Prepare(ctx, "acquire-plan-1", "acquire-plan-1-a", target, tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "acquire-plan-1", "acquire-plan-1-a", target, tick); err != nil {
		t.Fatal(err)
	}
	g, err := s.LoadStandard(ctx, g.Standard.ID)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "wall-1", buildingOnlyPlan(t, "wall-plan-1", "Wall")); err == nil {
		t.Fatal("open acquisition did not block an unrelated building")
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "lamp-1", buildingOnlyPlan(t, "lamp-plan-1", "SunLamp")); err != nil {
		t.Fatal("open acquisition blocked a sun lamp field batch", err)
	}
}

func buildingOnlyPlan(t *testing.T, id, definition string) domain.PlanSpec {
	t.Helper()
	b, err := domain.NewBuilding(definition, domain.Cell{X: 3, Z: 3}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(domain.ActionID(id+"-0"), b)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(domain.PlanID(id), 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

// A basin batch stays open until its last basin stands; the re-crop of a
// basin that already finished must not wait for it (#102).
func TestCommitGrowerCropExemptFromOpenFieldWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoundsRequest()
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.EnsureFoodSupply)
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "basin-1", buildingOnlyPlan(t, "basin-plan-1", "HydroponicsBasin")); err != nil {
		t.Fatal(err)
	}
	g, err := s.LoadStandard(ctx, g.Standard.ID)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "wall-1", buildingOnlyPlan(t, "wall-plan-1", "Wall")); err == nil {
		t.Fatal("open basin batch did not block an unrelated building")
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "fields-recrop-basin-1", growerCropPlan(t, "recrop-plan-1")); err != nil {
		t.Fatal("open basin batch blocked a re-crop", err)
	}
}

func growerCropPlan(t *testing.T, id string) domain.PlanSpec {
	t.Helper()
	g, err := domain.NewGrowerCrop("Thing_HydroponicsBasin1", "Plant_Potato")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewGrowerCropAction(domain.ActionID(id+"-0"), g)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(domain.PlanID(id), 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

// Owners declare their stockpile rule in policy.StockpileZoneLimit: every
// department concern with stores may create one, the storage planner a batch.
func TestStockpileZoneLimitPerOwner(t *testing.T) {
	for id, want := range map[policy.ConcernID]int{
		policy.EnsureFoodSupply:        1,
		policy.MaintainFoodStorage:     1,
		policy.MaintainMedicalReserves: 1,
		policy.MaintainEquipment:       1,
		policy.MaintainResource:        1,
		policy.MaintainAnimalFeed:      1,
		policy.ClearHomeObstructions:   1,
		policy.MaintainStockpiles:      16,
		policy.MaintainHousing:         0,
		policy.MaintainFlooring:        0,
	} {
		if got := policy.StockpileZoneLimit(id); got != want {
			t.Errorf("%s: limit %d, want %d", id, got, want)
		}
	}
}

// Two owners create stockpile zones in the same cycle: open zone work of one
// does not block the other.
func TestCommitStockpileZonesOfTwoOwnersInOneCycle(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := animalFeedRoundsRequest()
	r.Policy.ResourceTargets = map[policy.Resource]int64{"MeleeWeapon_Gladius": 3}
	r.Facts.Resources = domain.Known([]policy.Amount{})
	out := reviewRounds(t, s, &r)
	feed := roundsGoal(t, out, policy.MaintainAnimalFeed)
	resource := roundsGoal(t, out, policy.MaintainResource)
	if _, err := s.CommitMethod(ctx, feed.Standard.ID, feed.Revision, "feed-storage-0", stockpilePlan(t, "feed-plan", []domain.Cell{{X: 4, Z: 6}})); err != nil {
		t.Fatal(err)
	}
	if _, err := s.CommitMethod(ctx, resource.Standard.ID, resource.Revision, "ingredient-storage-0", stockpilePlan(t, "ingredient-plan", []domain.Cell{{X: 20, Z: 6}})); err != nil {
		t.Fatal("open zone work of another owner blocked a store", err)
	}
}
