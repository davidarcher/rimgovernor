package buildingruntime

import (
	"context"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
)

// TestShelterCombatantsProjectsOpenFightRosters: the draft set is this
// world's open fight rosters, known and empty with no fight (#1367).
func TestShelterCombatantsProjectsOpenFightRosters(t *testing.T) {
	ctx := context.Background()
	db, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	world := store.World{Colony: "colony", Load: "load", Map: 1}
	got, err := shelterCombatants(ctx, db, world)
	if rows, known := got.Value(); err != nil || !known || len(rows) != 0 {
		t.Fatalf("no fight = %v %v %v, want known empty", rows, known, err)
	}
	for _, f := range []struct {
		plan   domain.PlanID
		world  store.World
		roster []domain.PawnID
	}{{"here", world, []domain.PawnID{"b", "a"}}, {"elsewhere", store.World{Colony: "colony", Load: "other", Map: 1}, []domain.PawnID{"c"}}} {
		plan, _ := domain.NewPlan(f.plan, 1, nil)
		if err = db.CreatePlan(ctx, plan); err != nil {
			t.Fatal(err)
		}
		if err = db.OpenCombatFight(ctx, f.plan, policy.CombatMemory{}, f.world, f.roster); err != nil {
			t.Fatal(err)
		}
	}
	got, err = shelterCombatants(ctx, db, world)
	if rows, known := got.Value(); err != nil || !known || !slices.Equal(rows, []policy.PawnID{"a", "b"}) {
		t.Fatalf("fight = %v %v %v, want [a b]", rows, known, err)
	}
}
