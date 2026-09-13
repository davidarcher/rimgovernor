package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func settlementGiftSubmissionRequest(t *testing.T, id string) SettlementGiftSubmissionRequest {
	t.Helper()
	gift, err := domain.NewSettlementGift("caravan-1", "settlement-1", "faction-1", []domain.PawnID{"pawn-1", "pawn-2"}, 100)
	if err != nil {
		t.Fatal(err)
	}
	return SettlementGiftSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Gift: gift}
}

func TestSettlementGiftSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "settlement-gift-submission.db")
	s := open(t, path)
	request := settlementGiftSubmissionRequest(t, "request")
	first, created, err := s.SubmitSettlementGift(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitSettlementGift(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*SettlementGiftSubmissionRequest){
		func(v *SettlementGiftSubmissionRequest) { v.World.Map = 1 },
		func(v *SettlementGiftSubmissionRequest) { v.World.Load = "other" },
		func(v *SettlementGiftSubmissionRequest) { v.World.Colony = "other" },
		func(v *SettlementGiftSubmissionRequest) {
			v.Gift, _ = domain.NewSettlementGift(v.Gift.Caravan(), v.Gift.Settlement(), v.Gift.Faction(), []domain.PawnID{"pawn-1"}, v.Gift.Silver())
		},
		func(v *SettlementGiftSubmissionRequest) {
			v.Gift, _ = domain.NewSettlementGift(v.Gift.Caravan(), v.Gift.Settlement(), v.Gift.Faction(), v.Gift.CrewIDs(), 999)
		},
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitSettlementGift(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitSettlementGift(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupSettlementGiftSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestSettlementGiftSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "settlement-gift-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_settlement_gift_submission BEFORE INSERT ON settlement_gift_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitSettlementGift(ctx, settlementGiftSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "settlement_gift_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_settlement_gift_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitSettlementGift(ctx, settlementGiftSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestSettlementGiftSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "settlement-gift-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*SettlementGiftSubmissionRequest){
		func(v *SettlementGiftSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *SettlementGiftSubmissionRequest) { v.World.Map = -1 },
		func(v *SettlementGiftSubmissionRequest) { v.World.Load = "" },
		func(v *SettlementGiftSubmissionRequest) { v.Gift = domain.SettlementGift{} },
	} {
		request := settlementGiftSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitSettlementGift(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupSettlementGiftSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and settlement gift submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestSettlementGiftSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "settlement-gift-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	gift := settlementGiftSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitSettlementGift(ctx, gift); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	gift.RequestID = "gift-only"
	first, created, err := s.SubmitSettlementGift(ctx, gift)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "gift-only"); err == nil {
		t.Fatal("building lookup accepted a settlement gift submission")
	}
}
