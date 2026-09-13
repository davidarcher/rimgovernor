package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func questFulfillSubmissionRequest(t *testing.T, id string) QuestFulfillSubmissionRequest {
	t.Helper()
	fulfill, err := domain.NewQuestFulfill("quest-1", "caravan-1", []domain.PawnID{"pawn-1", "pawn-2"})
	if err != nil {
		t.Fatal(err)
	}
	return QuestFulfillSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Fulfill: fulfill}
}

func TestQuestFulfillSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "quest-fulfill-submission.db")
	s := open(t, path)
	request := questFulfillSubmissionRequest(t, "request")
	first, created, err := s.SubmitQuestFulfill(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitQuestFulfill(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*QuestFulfillSubmissionRequest){
		func(v *QuestFulfillSubmissionRequest) { v.World.Map = 1 },
		func(v *QuestFulfillSubmissionRequest) { v.World.Load = "other" },
		func(v *QuestFulfillSubmissionRequest) { v.World.Colony = "other" },
		func(v *QuestFulfillSubmissionRequest) {
			v.Fulfill, _ = domain.NewQuestFulfill(v.Fulfill.Quest(), v.Fulfill.Caravan(), []domain.PawnID{"pawn-1"})
		},
		func(v *QuestFulfillSubmissionRequest) {
			v.Fulfill, _ = domain.NewQuestFulfill("quest-2", v.Fulfill.Caravan(), v.Fulfill.CrewIDs())
		},
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitQuestFulfill(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitQuestFulfill(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupQuestFulfillSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestQuestFulfillSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "quest-fulfill-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_quest_fulfill_submission BEFORE INSERT ON quest_fulfill_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitQuestFulfill(ctx, questFulfillSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "quest_fulfill_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_quest_fulfill_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitQuestFulfill(ctx, questFulfillSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestQuestFulfillSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "quest-fulfill-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*QuestFulfillSubmissionRequest){
		func(v *QuestFulfillSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *QuestFulfillSubmissionRequest) { v.World.Map = -1 },
		func(v *QuestFulfillSubmissionRequest) { v.World.Load = "" },
		func(v *QuestFulfillSubmissionRequest) { v.Fulfill = domain.QuestFulfill{} },
	} {
		request := questFulfillSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitQuestFulfill(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupQuestFulfillSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and quest fulfill submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestQuestFulfillSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "quest-fulfill-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	fulfill := questFulfillSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitQuestFulfill(ctx, fulfill); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	fulfill.RequestID = "fulfill-only"
	first, created, err := s.SubmitQuestFulfill(ctx, fulfill)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "fulfill-only"); err == nil {
		t.Fatal("building lookup accepted a quest fulfill submission")
	}
}
