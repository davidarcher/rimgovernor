package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func caravanDepartureSubmissionRequest(t *testing.T, id string) CaravanDepartureSubmissionRequest {
	t.Helper()
	departure, err := domain.NewCaravanDeparture([]domain.PawnID{"pawn-1", "pawn-2"}, []domain.CargoItem{{Definition: "Pemmican", Count: 40}}, 12)
	if err != nil {
		t.Fatal(err)
	}
	return CaravanDepartureSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Departure: departure}
}

func TestCaravanDepartureSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "caravan-submission.db")
	s := open(t, path)
	request := caravanDepartureSubmissionRequest(t, "request")
	first, created, err := s.SubmitCaravanDeparture(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitCaravanDeparture(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*CaravanDepartureSubmissionRequest){
		func(v *CaravanDepartureSubmissionRequest) { v.World.Map = 1 },
		func(v *CaravanDepartureSubmissionRequest) { v.World.Load = "other" },
		func(v *CaravanDepartureSubmissionRequest) { v.World.Colony = "other" },
		func(v *CaravanDepartureSubmissionRequest) {
			v.Departure, _ = domain.NewCaravanDeparture([]domain.PawnID{"pawn-1"}, v.Departure.Cargo(), v.Departure.DestinationTile())
		},
		func(v *CaravanDepartureSubmissionRequest) {
			v.Departure, _ = domain.NewCaravanDeparture(v.Departure.Crew(), v.Departure.Cargo(), 99)
		},
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitCaravanDeparture(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitCaravanDeparture(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupCaravanDepartureSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestCaravanDepartureSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "caravan-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_caravan_submission BEFORE INSERT ON caravan_departure_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitCaravanDeparture(ctx, caravanDepartureSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "caravan_departure_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_caravan_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitCaravanDeparture(ctx, caravanDepartureSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestCaravanDepartureSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "caravan-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*CaravanDepartureSubmissionRequest){
		func(v *CaravanDepartureSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *CaravanDepartureSubmissionRequest) { v.World.Map = -1 },
		func(v *CaravanDepartureSubmissionRequest) { v.World.Load = "" },
		func(v *CaravanDepartureSubmissionRequest) { v.Departure = domain.CaravanDeparture{} },
	} {
		request := caravanDepartureSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitCaravanDeparture(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupCaravanDepartureSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and caravan departure submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestCaravanDepartureSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "caravan-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	caravan := caravanDepartureSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitCaravanDeparture(ctx, caravan); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	caravan.RequestID = "caravan-only"
	first, created, err := s.SubmitCaravanDeparture(ctx, caravan)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "caravan-only"); err == nil {
		t.Fatal("building lookup accepted a caravan departure submission")
	}
}
