package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func zoneCreateSubmissionRequest(t *testing.T, id string) ZoneCreateSubmissionRequest {
	t.Helper()
	zone, err := domain.NewZoneCreate(domain.GrowingZone, "Rice", []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})
	if err != nil {
		t.Fatal(err)
	}
	return ZoneCreateSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Zone: zone}
}

func TestZoneCreateSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "zone-create-submission.db")
	s := open(t, path)
	request := zoneCreateSubmissionRequest(t, "request")
	first, created, err := s.SubmitZoneCreate(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitZoneCreate(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	otherZone, err := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, []domain.Cell{{X: 0, Z: 0}})
	if err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*ZoneCreateSubmissionRequest){
		func(v *ZoneCreateSubmissionRequest) { v.World.Map = 1 },
		func(v *ZoneCreateSubmissionRequest) { v.World.Load = "other" },
		func(v *ZoneCreateSubmissionRequest) { v.World.Colony = "other" },
		func(v *ZoneCreateSubmissionRequest) { v.Zone = otherZone },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitZoneCreate(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitZoneCreate(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupZoneCreateSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestZoneCreateSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "zone-create-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_zone_create_submission BEFORE INSERT ON zone_create_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitZoneCreate(ctx, zoneCreateSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "zone_create_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_zone_create_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitZoneCreate(ctx, zoneCreateSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestZoneCreateSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "zone-create-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*ZoneCreateSubmissionRequest){
		func(v *ZoneCreateSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *ZoneCreateSubmissionRequest) { v.World.Map = -1 },
		func(v *ZoneCreateSubmissionRequest) { v.World.Load = "" },
		func(v *ZoneCreateSubmissionRequest) { v.Zone = domain.ZoneCreate{} },
	} {
		request := zoneCreateSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitZoneCreate(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupZoneCreateSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and zone create submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestZoneCreateSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "zone-create-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	zone := zoneCreateSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitZoneCreate(ctx, zone); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	zone.RequestID = "zone-only"
	first, created, err := s.SubmitZoneCreate(ctx, zone)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "zone-only"); err == nil {
		t.Fatal("building lookup accepted a zone create submission")
	}
}
