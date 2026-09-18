package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestProductionLadderRoundTripsPerWorld(t *testing.T) {
	ctx := context.Background()
	s, err := Open(ctx, filepath.Join(t.TempDir(), "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	w := World{Colony: "c", Load: "l", Map: 1}
	if _, ok, err := s.LoadProductionLadder(ctx, w); err != nil || ok {
		t.Fatal(ok, err)
	}
	record := ProductionLadderRecord{World: w, Tick: 7, Resource: "MeleeWeapon_Gladius", Bench: "FueledSmithy", Recipe: "Make_MeleeWeapon_Gladius", Research: []string{"Smithing", "Electricity"}}
	if err := s.SaveProductionLadder(ctx, record); err != nil {
		t.Fatal(err)
	}
	got, ok, err := s.LoadProductionLadder(ctx, w)
	if err != nil || !ok || got.Resource != record.Resource || len(got.Research) != 2 || got.Research[0] != "Electricity" {
		t.Fatal(got, ok, err)
	}
	if _, ok, err := s.LoadProductionLadder(ctx, World{Colony: "c", Load: "other", Map: 1}); err != nil || ok {
		t.Fatal("another load read the ladder", ok, err)
	}
	if err := s.SaveProductionLadder(ctx, ProductionLadderRecord{World: w, Tick: -1, Resource: "x"}); err == nil {
		t.Fatal("negative tick accepted")
	}
	if err := s.SaveProductionLadder(ctx, ProductionLadderRecord{World: w, Tick: domain.Tick(9), Resource: "MeleeWeapon_Gladius"}); err != nil {
		t.Fatal(err)
	}
	if got, _, _ = s.LoadProductionLadder(ctx, w); len(got.Research) != 0 || got.Tick != 9 {
		t.Fatal(got)
	}
}
