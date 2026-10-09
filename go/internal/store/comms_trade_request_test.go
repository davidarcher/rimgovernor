package store

import (
	"context"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestCommsTradeRequestPendingJournalReload(t *testing.T) {
	ctx := context.Background()
	s, path, g := goalFixture(t)
	request := domain.CommsTradeRequest{Kind: domain.TradeRequestCaravan, Faction: "ally", TraderKind: "bulk", Console: "console", Negotiator: "pawn", ExpectedLastRequestTick: -240000}
	action, err := domain.NewCommsTradeRequestAction("request", request)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("request-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	record := `{"comms_trade_request":{"Kind":"caravan","Faction":"ally","TraderKind":"bulk","Console":"console","Negotiator":"pawn","ExpectedLastRequestTick":-240000}}`
	if _, err = s.CommitMethodRecord(ctx, g.Standard.ID, g.Revision, "request", "", plan, record); err != nil {
		t.Fatal(err)
	}
	snapshot := g.Standard.Snapshot
	snapshot.Plan = plan.ID()
	snapshot.Revision = 1
	if _, err = s.Prepare(ctx, plan.ID(), action.ID(), snapshot, 100); err != nil {
		t.Fatal(err)
	}
	progress, err := s.Dispatch(ctx, plan.ID(), action.ID(), snapshot, 100)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipt(ctx, plan.ID(), action.ID(), progress.View().Attempt, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	saved, err := s.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	got, ok := saved.Spec.Actions()[0].CommsTradeRequest()
	if !ok || got != request || saved.Progress[0].View().Stage != domain.AwaitingObservation || !saved.Progress[0].View().Unresolved {
		t.Fatal(saved, got)
	}
	owner, err := s.LoadStandard(ctx, g.Standard.ID)
	if err != nil {
		t.Fatal(err)
	}
	if owner.Standard.Record != record {
		t.Fatal("saved intent lost", owner.Standard.Record)
	}
	blobs, err := s.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var blob GovernorStandardBlob
	if json.Unmarshal([]byte(blobs["standard/"+string(owner.Standard.ID)]), &blob) != nil || blob.Standard.Record != record {
		t.Fatal("intent did not reach save blob", blobs)
	}
	if _, err = s.CommitMethod(ctx, owner.Standard.ID, owner.Revision, "local-supply", planForRequestLocal(t)); err != nil {
		t.Fatal("uncertain request blocked ordinary supply", err)
	}
}

func planForRequestLocal(t *testing.T) domain.PlanSpec {
	t.Helper()
	building, err := domain.NewBuilding("Wall", domain.Cell{X: 4, Z: 4}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewBuildingAction("local", building, domain.TierExpand)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("local", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	return plan
}
