package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func zoneEditSubmissionRequest(t *testing.T, id string) ZoneEditSubmissionRequest {
	t.Helper()
	edit, err := domain.NewZoneEditAdd("zone-1", "token-1", []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})
	if err != nil {
		t.Fatal(err)
	}
	return ZoneEditSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Edit: edit}
}

func TestZoneEditSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "zone-edit-submission.db")
	s := open(t, path)
	request := zoneEditSubmissionRequest(t, "request")
	first, created, err := s.SubmitZoneEdit(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitZoneEdit(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	otherEdit, err := domain.NewZoneEditDelete("zone-1", "token-1")
	if err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*ZoneEditSubmissionRequest){
		func(v *ZoneEditSubmissionRequest) { v.World.Map = 1 },
		func(v *ZoneEditSubmissionRequest) { v.World.Load = "other" },
		func(v *ZoneEditSubmissionRequest) { v.World.Colony = "other" },
		func(v *ZoneEditSubmissionRequest) { v.Edit = otherEdit },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitZoneEdit(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitZoneEdit(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupZoneEditSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestZoneEditSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "zone-edit-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_zone_edit_submission BEFORE INSERT ON zone_edit_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitZoneEdit(ctx, zoneEditSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "zone_edit_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_zone_edit_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitZoneEdit(ctx, zoneEditSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestZoneEditSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "zone-edit-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*ZoneEditSubmissionRequest){
		func(v *ZoneEditSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *ZoneEditSubmissionRequest) { v.World.Map = -1 },
		func(v *ZoneEditSubmissionRequest) { v.World.Load = "" },
		func(v *ZoneEditSubmissionRequest) { v.Edit = domain.ZoneEdit{} },
	} {
		request := zoneEditSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitZoneEdit(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupZoneEditSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Zone create and zone edit submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestZoneEditSubmissionSharesNamespaceWithZoneCreate(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "zone-edit-namespace.db"))
	create := zoneCreateSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitZoneCreate(ctx, create); err != nil {
		t.Fatal(err)
	}
	edit := zoneEditSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitZoneEdit(ctx, edit); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	edit.RequestID = "edit-only"
	first, created, err := s.SubmitZoneEdit(ctx, edit)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupZoneCreateSubmission(ctx, "edit-only"); err == nil {
		t.Fatal("zone create lookup accepted a zone edit submission")
	}
}
