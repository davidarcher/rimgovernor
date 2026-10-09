package store

import (
	"context"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestRebuildFamiliesRoundTripsEachFamily writes each family into one store,
// rebuilds a second store from its blobs, and expects equal blobs.
func TestRebuildFamiliesRoundTripsEachFamily(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	source := open(t, memoryPath(t))
	w := extentWorld("colony", "load-1", 1)
	plan := policy.LayoutPlan{
		Spine: []policy.SpineSegment{{From: domain.Cell{X: 10, Z: 20}, To: domain.Cell{X: 40, Z: 20}}},
		Rooms: []policy.PlannedRoom{{Role: policy.PlannedStorage, Interior: policy.Rectangle{X: 12, Z: 22, Width: 7, Height: 5}, Door: domain.Cell{X: 15, Z: 21}, DoorRot: domain.South, Dug: true}},
	}
	if err := source.RecordLayoutPlan(ctx, w, 100, plan); err != nil {
		t.Fatal(err)
	}
	world := World{Colony: "colony", Load: "load-1", Map: 1}
	wall, _ := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 4}, domain.North, "BlocksGranite")
	layout := policy.DefenseLayout{Chokepoint: domain.Cell{X: 9, Z: 15}, Entry: domain.Cell{X: 9, Z: 8}, Toward: domain.North, Width: 3,
		Firing: []policy.FiringPosition{{Cell: domain.Cell{X: 9, Z: 22}}}, Tiers: []policy.DefenseTier{{Name: policy.TierChokepoint, Buildings: []domain.Building{wall}, Reserved: []domain.Cell{{X: 3, Z: 4}}}}}
	defense, err := NewDefenseLayoutRecord(world, "project-1", layout, []domain.Cell{{X: 9, Z: 30}})
	if err != nil {
		t.Fatal(err)
	}
	if err = source.SaveDefenseLayout(ctx, defense); err != nil {
		t.Fatal(err)
	}
	if err = source.SaveProductionLadder(ctx, ProductionLadderRecord{World: world, Tick: 7, Resource: "MeleeWeapon_Gladius", Bench: "FueledSmithy", Recipe: "Make_MeleeWeapon_Gladius", Research: []string{"Smithing", "Electricity"}}); err != nil {
		t.Fatal(err)
	}
	if err = source.SaveSoldierSquad(ctx, SoldierSquadRecord{World: world, Members: []policy.PawnID{"Thing_Human2", "Thing_Human1"}}); err != nil {
		t.Fatal(err)
	}
	saved, err := source.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{GovernorLayoutPlanKey, GovernorDefenseLayoutKey, GovernorProductionLadderKey, GovernorSoldierSquadKey} {
		if saved[key] == "" {
			t.Fatal("missing blob", key)
		}
		// Each family alone round-trips, and the others come back empty.
		target := open(t, memoryPath(t))
		if err = target.RebuildFamilies(ctx, saved); err != nil {
			t.Fatal(err)
		}
		if err = target.RebuildFamilies(ctx, map[string]string{key: saved[key]}); err != nil {
			t.Fatal(key, err)
		}
		got, err := target.GovernorStateBlobs(ctx)
		if err != nil {
			t.Fatal(err)
		}
		if want := map[string]string{key: saved[key]}; !reflect.DeepEqual(got, want) {
			t.Fatalf("%s:\n got %v\nwant %v", key, got, want)
		}
	}
	target := open(t, memoryPath(t))
	if err = target.RebuildFamilies(ctx, saved); err != nil {
		t.Fatal(err)
	}
	if got, err := target.GovernorStateBlobs(ctx); err != nil || !reflect.DeepEqual(got, saved) {
		t.Fatalf("all families: %v\n got %v\nwant %v", err, got, saved)
	}
	if err = target.RebuildFamilies(ctx, nil); err != nil {
		t.Fatal(err)
	}
	if got, err := target.GovernorStateBlobs(ctx); err != nil || len(got) != 0 {
		t.Fatal("empty save left rows", got, err)
	}
}
