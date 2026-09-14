package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func buildingTemperatureSubmissionRequest(t *testing.T, id string) BuildingTemperatureSubmissionRequest {
	t.Helper()
	patch, err := domain.NewBuildingTemperature("thing-1", 20.5, "token-1")
	if err != nil {
		t.Fatal(err)
	}
	return BuildingTemperatureSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Patch: patch}
}

func TestBuildingTemperatureSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "building-temperature-submission.db")
	s := open(t, path)
	request := buildingTemperatureSubmissionRequest(t, "request")
	first, created, err := s.SubmitBuildingTemperature(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitBuildingTemperature(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*BuildingTemperatureSubmissionRequest){
		func(v *BuildingTemperatureSubmissionRequest) { v.World.Map = 1 },
		func(v *BuildingTemperatureSubmissionRequest) { v.World.Load = "other" },
		func(v *BuildingTemperatureSubmissionRequest) { v.World.Colony = "other" },
		func(v *BuildingTemperatureSubmissionRequest) {
			v.Patch, _ = domain.NewBuildingTemperature("other-thing", 20.5, "token-1")
		},
		func(v *BuildingTemperatureSubmissionRequest) {
			v.Patch, _ = domain.NewBuildingTemperature("thing-1", 21.5, "token-1")
		},
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitBuildingTemperature(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitBuildingTemperature(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupBuildingTemperatureSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestBuildingTemperatureSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "building-temperature-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_building_temperature_submission BEFORE INSERT ON building_temperature_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitBuildingTemperature(ctx, buildingTemperatureSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "building_temperature_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_building_temperature_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitBuildingTemperature(ctx, buildingTemperatureSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestBuildingTemperatureSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "building-temperature-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*BuildingTemperatureSubmissionRequest){
		func(v *BuildingTemperatureSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *BuildingTemperatureSubmissionRequest) { v.World.Map = -1 },
		func(v *BuildingTemperatureSubmissionRequest) { v.World.Load = "" },
		func(v *BuildingTemperatureSubmissionRequest) { v.Patch = domain.BuildingTemperature{} },
	} {
		request := buildingTemperatureSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitBuildingTemperature(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupBuildingTemperatureSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and building-temperature submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestBuildingTemperatureSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "building-temperature-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	patch := buildingTemperatureSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitBuildingTemperature(ctx, patch); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	patch.RequestID = "building-temperature-only"
	first, created, err := s.SubmitBuildingTemperature(ctx, patch)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "building-temperature-only"); err == nil {
		t.Fatal("building lookup accepted a building temperature submission")
	}
}
