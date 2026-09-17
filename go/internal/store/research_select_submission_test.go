package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func researchSelectSubmissionRequest(t *testing.T, id string) ResearchSelectSubmissionRequest {
	t.Helper()
	value, err := domain.NewResearchSelect("project-1")
	if err != nil {
		t.Fatal(err)
	}
	return ResearchSelectSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Select: value}
}

func TestResearchSelectSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	request := researchSelectSubmissionRequest(t, "request")
	first, created, err := s.SubmitResearchSelect(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitResearchSelect(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*ResearchSelectSubmissionRequest){
		func(v *ResearchSelectSubmissionRequest) { v.World.Map = 1 },
		func(v *ResearchSelectSubmissionRequest) { v.World.Load = "other" },
		func(v *ResearchSelectSubmissionRequest) { v.World.Colony = "other" },
		func(v *ResearchSelectSubmissionRequest) { v.Select, _ = domain.NewResearchSelect("other-project") },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitResearchSelect(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitResearchSelect(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupResearchSelectSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestResearchSelectSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_research_select_submission BEFORE INSERT ON research_select_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitResearchSelect(ctx, researchSelectSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "research_select_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_research_select_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitResearchSelect(ctx, researchSelectSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestResearchSelectSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	ctx := context.Background()
	for _, change := range []func(*ResearchSelectSubmissionRequest){
		func(v *ResearchSelectSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *ResearchSelectSubmissionRequest) { v.World.Map = -1 },
		func(v *ResearchSelectSubmissionRequest) { v.World.Load = "" },
		func(v *ResearchSelectSubmissionRequest) { v.Select = domain.ResearchSelect{} },
	} {
		request := researchSelectSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitResearchSelect(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupResearchSelectSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and research select submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestResearchSelectSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	research := researchSelectSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitResearchSelect(ctx, research); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	research.RequestID = "research-only"
	first, created, err := s.SubmitResearchSelect(ctx, research)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "research-only"); err == nil {
		t.Fatal("building lookup accepted a research select submission")
	}
}
