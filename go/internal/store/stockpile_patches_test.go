package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A completed stockpile_patch on a zone is the zone's applied settings from
// its completion tick on, and one on a storage building the building's.
func TestStockpilePatchesListTheLatestCompletedPatch(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "patches.db"))
	defer s.Close()
	r := animalFeedRoutineRequest()
	g := routineGoal(t, reviewRoutine(t, s, &r), policy.MaintainAnimalFeed)
	zonePatch, _ := domain.NewStockpilePatch(domain.StorageZoneTarget, "Zone_7", domain.FoodFilter(), domain.ImportantPriority, "kitchen")
	shelfPatch, _ := domain.NewStockpilePatch(domain.StorageBuildingTarget, "Shelf_1", domain.FoodFilter(), domain.ImportantPriority, "")
	za, _ := domain.NewStockpilePatchAction("patch-zone", zonePatch)
	sa, _ := domain.NewStockpilePatchAction("patch-shelf", shelfPatch)
	plan, err := domain.NewPlan("patch-plan", 1, []domain.Action{za, sa})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "stockpiles-1", plan); err != nil {
		t.Fatal(err)
	}
	current := r.Current
	current.Plan, current.Revision = plan.ID(), plan.Revision()
	for _, id := range []domain.ActionID{"patch-zone", "patch-shelf"} {
		if _, err = s.Prepare(ctx, plan.ID(), id, current, 11); err != nil {
			t.Fatal(id, err)
		}
		if _, err = s.Dispatch(ctx, plan.ID(), id, current, 11); err != nil {
			t.Fatal(id, err)
		}
		if _, err = s.RecordReceipt(ctx, plan.ID(), id, 1, domain.ReceiptAccepted); err != nil {
			t.Fatal(id, err)
		}
	}
	got, err := s.StockpilePatches(ctx, r.Current, 12)
	if err != nil || len(got) != 2 {
		t.Fatal(got, err)
	}
	if p := got["Zone_7"]; p.Kind != domain.StorageZoneTarget || p.Filter != domain.FoodFilter() || p.Priority != domain.ImportantPriority || p.Role != "kitchen" || p.Tick != 11 {
		t.Fatalf("applied %+v", p)
	}
	if p := got["Shelf_1"]; p.Kind != domain.StorageBuildingTarget || p.Filter != domain.FoodFilter() || p.Tick != 11 {
		t.Fatalf("shelf applied %+v", p)
	}
	if got, err = s.StockpilePatches(ctx, r.Current, 10); err != nil || len(got) != 0 {
		t.Fatal("a future completion applied", got, err)
	}
}
