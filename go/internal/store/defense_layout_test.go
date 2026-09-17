package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestDefenseLayoutRoundTripPerWorld(t *testing.T) {
	db, err := Open(context.Background(), memoryPath(t))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	world := World{Colony: "colony", Load: "load", Map: 1}
	wall, _ := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 4}, domain.North, "BlocksGranite")
	layout := policy.DefenseLayout{Chokepoint: domain.Cell{X: 9, Z: 15}, Entry: domain.Cell{X: 9, Z: 8}, Toward: domain.North, Width: 3,
		Firing: []policy.FiringPosition{{Cell: domain.Cell{X: 9, Z: 22}}}, Tiers: []policy.DefenseTier{{Name: policy.TierChokepoint, Buildings: []domain.Building{wall}, Reserved: []domain.Cell{{X: 3, Z: 4}}}}}
	record, err := NewDefenseLayoutRecord(world, "goal-1", 2, layout, []domain.Cell{{X: 9, Z: 30}})
	if err != nil {
		t.Fatal(err)
	}
	if _, ok, err := db.LoadDefenseLayout(context.Background(), world); err != nil || ok {
		t.Fatal("layout before save", ok, err)
	}
	if err = db.SaveDefenseLayout(context.Background(), record); err != nil {
		t.Fatal(err)
	}
	got, ok, err := db.LoadDefenseLayout(context.Background(), world)
	if err != nil || !ok || got.Firing[0] != (domain.Cell{X: 9, Z: 22}) || got.Epoch != 2 || got.Entrances[0] != (domain.Cell{X: 9, Z: 30}) {
		t.Fatalf("%v %v %+v", ok, err, got)
	}
	tier, buildings, ok := got.Tier(policy.TierChokepoint)
	if !ok || tier.Reserved[0] != (domain.Cell{X: 3, Z: 4}) || buildings[0] != wall {
		t.Fatalf("%+v %v", tier, buildings)
	}
	// A reload of the same colony keeps the geometry under its saved load
	// (the planner adopts it); another colony or map does not see it.
	if reloaded, ok, err := db.LoadDefenseLayout(context.Background(), World{Colony: "colony", Load: "reload", Map: 1}); err != nil || !ok || reloaded.World != world {
		t.Fatal("reloaded colony lost its layout", ok, err)
	}
	if _, ok, err = db.LoadDefenseLayout(context.Background(), World{Colony: "colony", Load: "load", Map: 2}); err != nil || ok {
		t.Fatal("other map layout returned", ok, err)
	}
	if _, ok, err = db.LoadDefenseLayout(context.Background(), World{Colony: "other", Load: "load", Map: 1}); err != nil || ok {
		t.Fatal("other colony layout returned", ok, err)
	}
	got.Complete = true
	if err = db.SaveDefenseLayout(context.Background(), got); err != nil {
		t.Fatal(err)
	}
	if got, _, _ = db.LoadDefenseLayout(context.Background(), world); !got.Complete {
		t.Fatal("completion not persisted")
	}
	record.Firing = nil
	if err = db.SaveDefenseLayout(context.Background(), record); err == nil {
		t.Fatal("saved a layout without firing cells")
	}
}
