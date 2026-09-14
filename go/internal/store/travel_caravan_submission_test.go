package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func travelCaravanSubmissionRequest(t *testing.T, id string) TravelCaravanSubmissionRequest {
	t.Helper()
	travel, err := domain.NewTravelCaravan("caravan-1", domain.TravelMove, 42)
	if err != nil {
		t.Fatal(err)
	}
	return TravelCaravanSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Travel: travel}
}

func TestTravelCaravanSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "travel-caravan-submission.db")
	s := open(t, path)
	request := travelCaravanSubmissionRequest(t, "request")
	first, created, err := s.SubmitTravelCaravan(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitTravelCaravan(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*TravelCaravanSubmissionRequest){
		func(v *TravelCaravanSubmissionRequest) { v.World.Map = 1 },
		func(v *TravelCaravanSubmissionRequest) { v.World.Load = "other" },
		func(v *TravelCaravanSubmissionRequest) { v.World.Colony = "other" },
		func(v *TravelCaravanSubmissionRequest) {
			v.Travel, _ = domain.NewTravelCaravan(v.Travel.Caravan(), domain.TravelMove, 43)
		},
		func(v *TravelCaravanSubmissionRequest) {
			v.Travel, _ = domain.NewTravelCaravan("caravan-2", v.Travel.Kind(), v.Travel.DestinationTile())
		},
		func(v *TravelCaravanSubmissionRequest) {
			v.Travel, _ = domain.NewTravelCaravan(v.Travel.Caravan(), domain.TravelStop, -1)
		},
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitTravelCaravan(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitTravelCaravan(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupTravelCaravanSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestTravelCaravanSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "travel-caravan-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_travel_caravan_submission BEFORE INSERT ON travel_caravan_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitTravelCaravan(ctx, travelCaravanSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "travel_caravan_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_travel_caravan_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitTravelCaravan(ctx, travelCaravanSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestTravelCaravanSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "travel-caravan-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*TravelCaravanSubmissionRequest){
		func(v *TravelCaravanSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *TravelCaravanSubmissionRequest) { v.World.Map = -1 },
		func(v *TravelCaravanSubmissionRequest) { v.World.Load = "" },
		func(v *TravelCaravanSubmissionRequest) { v.Travel = domain.TravelCaravan{} },
	} {
		request := travelCaravanSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitTravelCaravan(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupTravelCaravanSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and travel caravan submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestTravelCaravanSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "travel-caravan-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	travel := travelCaravanSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitTravelCaravan(ctx, travel); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	travel.RequestID = "travel-only"
	first, created, err := s.SubmitTravelCaravan(ctx, travel)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "travel-only"); err == nil {
		t.Fatal("building lookup accepted a travel caravan submission")
	}
}
