package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
)

func TestQuestAcceptIsARoutineExecutableKind(t *testing.T) {
	t.Parallel()
	// The joiner answer (#250) is dispatched under the routine worker like
	// every other routine method; a kind missing from the allowlist commits
	// a plan whose action then sits at pending until the offer expires.
	if !routineExecutableKind(domain.QuestAcceptAction) {
		t.Fatal("quest_accept must be routine executable")
	}
}

func TestJoinerDefenseTiersFromStoredRecord(t *testing.T) {
	ctx := context.Background()
	journal := storetest.Open(t)
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0}
	if tiers, err := routineJoinerDefenseTiers(ctx, journal, snapshot); err != nil || tiers != domain.Known(0) {
		t.Fatal(tiers, err)
	}
	building := []store.DefenseBuilding{{Definition: "Barricade", Cell: domain.Cell{X: 1, Z: 1}, Rotation: domain.North, Stuff: "WoodLog"}}
	record := store.DefenseLayoutRecord{World: store.World{Colony: "colony", Load: "load", Map: 0}, Goal: "defense", Firing: []domain.Cell{{X: 1, Z: 2}}}
	for _, tt := range []struct {
		name         policy.DefenseTierName
		built, empty bool
		want         int
	}{
		{policy.TierFiringLine, false, false, 0}, {policy.TierFiringLine, true, false, 1},
		{policy.TierTurrets, true, false, 1}, {policy.TierTurrets, true, true, 0},
		{policy.TierFunnel, true, false, 0},
	} {
		tier := store.DefenseTierRecord{Name: tt.name, Built: tt.built, Buildings: building}
		if tt.empty {
			tier.Buildings = nil
		}
		record.Tiers = []store.DefenseTierRecord{tier}
		if err := journal.SaveDefenseLayout(ctx, record); err != nil {
			t.Fatal(err)
		}
		if tiers, err := routineJoinerDefenseTiers(ctx, journal, snapshot); err != nil || tiers != domain.Known(tt.want) {
			t.Fatal(tt, tiers, err)
		}
	}
	snapshot.Load = "reload"
	if tiers, err := routineJoinerDefenseTiers(ctx, journal, snapshot); err != nil || tiers != domain.Unknown[int]() {
		t.Fatal("stale load defense must be unknown", tiers, err)
	}
}
