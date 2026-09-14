package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func husbandrySubmissionRequest(t *testing.T, id string) HusbandrySubmissionRequest {
	t.Helper()
	husbandry, err := domain.NewHusbandry("animal-1", domain.HusbandryTrain, "def-1")
	if err != nil {
		t.Fatal(err)
	}
	return HusbandrySubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Husbandry: husbandry}
}

func TestHusbandrySubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "husbandry-submission.db")
	s := open(t, path)
	request := husbandrySubmissionRequest(t, "request")
	first, created, err := s.SubmitHusbandry(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitHusbandry(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*HusbandrySubmissionRequest){
		func(v *HusbandrySubmissionRequest) { v.World.Map = 1 },
		func(v *HusbandrySubmissionRequest) { v.World.Load = "other" },
		func(v *HusbandrySubmissionRequest) { v.World.Colony = "other" },
		func(v *HusbandrySubmissionRequest) { v.Husbandry, _ = domain.NewHusbandry("other-animal", domain.HusbandryTrain, "def-1") },
		func(v *HusbandrySubmissionRequest) { v.Husbandry, _ = domain.NewHusbandry("animal-1", domain.HusbandrySlaughter, "") },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitHusbandry(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitHusbandry(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupHusbandrySubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestHusbandrySubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "husbandry-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_husbandry_submission BEFORE INSERT ON husbandry_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitHusbandry(ctx, husbandrySubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "husbandry_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_husbandry_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitHusbandry(ctx, husbandrySubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestHusbandrySubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "husbandry-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*HusbandrySubmissionRequest){
		func(v *HusbandrySubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *HusbandrySubmissionRequest) { v.World.Map = -1 },
		func(v *HusbandrySubmissionRequest) { v.World.Load = "" },
		func(v *HusbandrySubmissionRequest) { v.Husbandry = domain.Husbandry{} },
	} {
		request := husbandrySubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitHusbandry(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupHusbandrySubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and husbandry submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestHusbandrySubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "husbandry-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	husbandry := husbandrySubmissionRequest(t, "shared")
	if _, _, err := s.SubmitHusbandry(ctx, husbandry); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	husbandry.RequestID = "husbandry-only"
	first, created, err := s.SubmitHusbandry(ctx, husbandry)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "husbandry-only"); err == nil {
		t.Fatal("building lookup accepted a husbandry submission")
	}
}
