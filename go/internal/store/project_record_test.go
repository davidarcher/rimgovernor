package store

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"path/filepath"
	"testing"
)

func TestTradeMissionProjectRecordFollowsSaveAndCAS(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "mission.db"))
	w := scope()
	m := domain.TradeMission{HomeColony: w.Colony, HomeMap: w.Map, HomeTile: 2, Settlement: "settlement", SettlementTile: 3, Crew: []domain.PawnID{"pawn"}, Negotiator: "pawn", SilverBudget: 100, Demand: []domain.CargoItem{{Definition: "Steel", Count: 10}}, Pack: []domain.CargoItem{{Definition: "MealSurvivalPack", Count: 3}, {Definition: "Silver", Count: 100}}, Phase: domain.TradeMissionPlanned, ReturnHome: true}
	p, err := domain.NewTradeMissionProject("project-trade", 2, w, m)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreateProject(ctx, p); err != nil {
		t.Fatal(err)
	}
	m.Phase = domain.TradeMissionBuying
	m.PurchaseCommitted = true
	m.ReturnGoods = []domain.CargoItem{{Definition: "Steel", Count: 10}}
	record, err := m.Record()
	if err != nil {
		t.Fatal(err)
	}
	state, err := s.RecordProject(ctx, p.ID, 0, record)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordProject(ctx, p.ID, 0, record); !errors.Is(err, ErrConflict) {
		t.Fatal("stale write", err)
	}
	if _, err = s.RecordProject(ctx, p.ID, state.Revision, "{}"); err == nil {
		t.Fatal("invalid mission record accepted")
	}
	blobs, err := s.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	restored := open(t, filepath.Join(t.TempDir(), "restored.db"))
	if err = restored.RebuildStandards(ctx, blobs, nil); err != nil {
		t.Fatal(err)
	}
	state, err = restored.LoadProject(ctx, p.ID)
	if err != nil {
		t.Fatal(err)
	}
	mission, err := domain.DecodeTradeMission(state.Project.Record)
	if err != nil || mission.Phase != domain.TradeMissionBuying || !mission.PurchaseCommitted || len(mission.ReturnGoods) != 1 || len(state.History) != 0 {
		t.Fatal(mission, state, err)
	}
}

func TestCommitProjectMethodRecordRollsBackTogether(t *testing.T) {
	ctx := context.Background()
	s, state := projectFixture(t)
	id := domain.MintPlanID()
	if _, err := s.CommitProjectMethodRecord(ctx, state.Project.ID, state.Revision, "method", "", plan(t, id, domain.ActionID(string(id)+"-0")), string(make([]byte, domain.MaxProjectRecord+1))); err == nil {
		t.Fatal("overbound record committed")
	}
	after, err := s.LoadProject(ctx, state.Project.ID)
	if err != nil || after.Revision != state.Revision || len(after.History) != 0 {
		t.Fatal(after, err)
	}
	if _, err = s.LoadPlan(ctx, id); !errors.Is(err, ErrNotFound) {
		t.Fatal("rolled back method left plan", err)
	}
	after, err = s.CommitProjectMethodRecord(ctx, state.Project.ID, state.Revision, "method", "", plan(t, id, domain.ActionID(string(id)+"-0")), "intent")
	if err != nil || after.Project.Record != "intent" || len(after.History) != 1 {
		t.Fatal(after, err)
	}
}
