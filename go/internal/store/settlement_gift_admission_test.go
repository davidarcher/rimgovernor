package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func settlementGiftStoreFixture(t *testing.T) (*Store, string, SettlementGiftAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "settlement_gift.db")
	s := open(t, path)
	gift, _ := domain.NewSettlementGift("caravan-1", "settlement-1", "faction-1", []domain.PawnID{"pawn-1", "pawn-2"}, 500)
	a, _ := domain.NewSettlementGiftAction("settlement-gift-1", gift)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	v := SettlementGiftAdmission{
		Snapshot: snapshot, Tick: 12, Caravan: "caravan-1", Settlement: "settlement-1", Faction: "faction-1",
		CrewIDs: []domain.PawnID{"pawn-1", "pawn-2"}, Silver: 500, CaravanSnapshotToken: "caravan-cas", FactionSnapshotToken: "faction-cas",
	}
	return s, path, v
}

func sameSettlementGiftAdmission(a, b SettlementGiftAdmission) bool {
	if a.Snapshot != b.Snapshot || a.Tick != b.Tick || a.Caravan != b.Caravan || a.Settlement != b.Settlement || a.Faction != b.Faction ||
		a.Silver != b.Silver || a.CaravanSnapshotToken != b.CaravanSnapshotToken || a.FactionSnapshotToken != b.FactionSnapshotToken || len(a.CrewIDs) != len(b.CrewIDs) {
		return false
	}
	for i := range a.CrewIDs {
		if a.CrewIDs[i] != b.CrewIDs[i] {
			return false
		}
	}
	return true
}

func TestSettlementGiftAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := settlementGiftStoreFixture(t)
	if _, err := s.PrepareSettlementGift(ctx, "plan", "settlement-gift-1", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.SettlementGiftAdmissions) != 1 || !sameSettlementGiftAdmission(state.SettlementGiftAdmissions[0].Admission, v) || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "settlement-gift-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestSettlementGiftDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := settlementGiftStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "settlement-gift-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "settlement-gift-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a settlement gift action")
	}
}

func TestSettlementGiftAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := settlementGiftStoreFixture(t)
			if _, err := s.PrepareSettlementGift(ctx, "plan", "settlement-gift-1", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "settlement-gift-1", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "settlement-gift-1", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
			if effect == domain.EffectUnsuccessful {
				observation.UnsuccessfulReason = domain.NativeFailure
			}
			if _, err := s.Observe(ctx, "plan", observation, v.Snapshot); err != nil {
				t.Fatal(err)
			}
			v.Tick = observation.Tick + 1
			data, err := json.Marshal(v)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE settlement_gift_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestSettlementGiftAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := settlementGiftStoreFixture(t)
	if _, err := s.PrepareSettlementGift(ctx, "plan", "settlement-gift-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "settlement-gift-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "settlement-gift-1", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareSettlementGift(ctx, "plan", "settlement-gift-1", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.CaravanSnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareSettlementGift(ctx, "plan", "settlement-gift-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "settlement-gift-1"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || !sameSettlementGiftAdmission(state.SettlementGiftAdmissions[0].Admission, v) {
		t.Fatal(state, err)
	}
}
