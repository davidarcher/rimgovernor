package buildingruntime

import (
	"context"
	"path/filepath"
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

// A quest accept native refused (the offer expired before dispatch) is
// cancelled so the method closes; one still awaiting its dispatch is left
// alone (#717).
func TestCancelRefusedQuestAcceptsSettlesTheRefusedAccept(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	journal, err := store.Open(ctx, filepath.Join(t.TempDir(), "joiner.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "p", Revision: domain.PlanRevision(^uint64(0))}
	g, err := domain.NewGoal("population", domain.AutopilotGoal, 2, snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreateGoal(ctx, g); err != nil {
		t.Fatal(err)
	}
	state, err := journal.ReviewGoal(ctx, g.ID, 0, snapshot, 10, domain.NeedDeficit, false)
	if err != nil {
		t.Fatal(err)
	}
	id := domain.PlanID("refused")
	accept, err := domain.NewQuestAccept("Quest_0", "", 0)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewQuestAcceptAction(domain.ActionID(id+"-0"), accept)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if state, err = journal.CommitGoalMethod(ctx, g.ID, state.Revision, "joiner-Quest_0-0", plan); err != nil {
		t.Fatal(err)
	}
	stage := func() domain.Stage {
		t.Helper()
		plan, err := journal.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(err)
		}
		return plan.Progress[0].View().Stage
	}
	if err = cancelRefusedQuestAccepts(ctx, journal, state); err != nil || stage() != domain.Pending {
		t.Fatalf("undispatched accept: stage %s err %v, want pending", stage(), err)
	}
	target := snapshot
	target.Plan, target.Revision, target.Native = "refused", 1, 2
	admission := store.QuestAcceptAdmission{Snapshot: target, Tick: 20, Quest: "Quest_0", RewardChoice: 0, QuestSnapshotToken: "quest-cas"}
	if _, err = journal.PrepareQuestAccept(ctx, id, "refused-0", admission); err != nil {
		t.Fatal(err)
	}
	p, err := journal.Dispatch(ctx, id, "refused-0", target, 20)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = journal.RecordReceipt(ctx, id, "refused-0", p.View().Attempt, domain.ReceiptRefused); err != nil {
		t.Fatal(err)
	}
	if err = cancelRefusedQuestAccepts(ctx, journal, state); err != nil {
		t.Fatal(err)
	}
	if got := stage(); got != domain.Cancelled {
		t.Fatalf("refused accept stage = %s, want cancelled", got)
	}
}
