package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func questAcceptSubmissionRequest(t *testing.T, id string) QuestAcceptSubmissionRequest {
	t.Helper()
	accept, err := domain.NewQuestAccept("quest-1", "pawn-1", -1)
	if err != nil {
		t.Fatal(err)
	}
	return QuestAcceptSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Accept: accept}
}

func TestQuestAcceptSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "quest-accept-submission.db")
	s := open(t, path)
	request := questAcceptSubmissionRequest(t, "request")
	first, created, err := s.SubmitQuestAccept(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitQuestAccept(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*QuestAcceptSubmissionRequest){
		func(v *QuestAcceptSubmissionRequest) { v.World.Map = 1 },
		func(v *QuestAcceptSubmissionRequest) { v.World.Load = "other" },
		func(v *QuestAcceptSubmissionRequest) { v.World.Colony = "other" },
		func(v *QuestAcceptSubmissionRequest) {
			v.Accept, _ = domain.NewQuestAccept(v.Accept.Quest(), "other-pawn", v.Accept.RewardChoice())
		},
		func(v *QuestAcceptSubmissionRequest) {
			v.Accept, _ = domain.NewQuestAccept(v.Accept.Quest(), v.Accept.AccepterPawn(), 0)
		},
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitQuestAccept(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitQuestAccept(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupQuestAcceptSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestQuestAcceptSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "quest-accept-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_quest_accept_submission BEFORE INSERT ON quest_accept_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitQuestAccept(ctx, questAcceptSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "quest_accept_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_quest_accept_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitQuestAccept(ctx, questAcceptSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestQuestAcceptSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "quest-accept-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*QuestAcceptSubmissionRequest){
		func(v *QuestAcceptSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *QuestAcceptSubmissionRequest) { v.World.Map = -1 },
		func(v *QuestAcceptSubmissionRequest) { v.World.Load = "" },
		func(v *QuestAcceptSubmissionRequest) { v.Accept = domain.QuestAccept{} },
	} {
		request := questAcceptSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitQuestAccept(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupQuestAcceptSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and quest accept submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestQuestAcceptSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "quest-accept-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	quest := questAcceptSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitQuestAccept(ctx, quest); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	quest.RequestID = "quest-only"
	first, created, err := s.SubmitQuestAccept(ctx, quest)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "quest-only"); err == nil {
		t.Fatal("building lookup accepted a quest accept submission")
	}
}
