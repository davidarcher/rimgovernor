package httpapi

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/spectator"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// spectatorServer serves /api/spectator/now over a ring holding one window,
// its stop and the step that acted on it.
func spectatorServer(t *testing.T, withRecorder bool) *Server {
	t.Helper()
	ring := ""
	if withRecorder {
		ring = filepath.Join(t.TempDir(), "flight", "flight.jsonl")
		recorder, err := bridge.NewFlightRecorder(ring, bridge.FlightRunID("run-a"))
		if err != nil {
			t.Fatal(err)
		}
		defer recorder.Close()
		for _, event := range []struct {
			kind    string
			payload map[string]any
		}{
			{"scheduler_step", map[string]any{"admitted": true, "running": true, "window_ticks": 2500}},
			{"scheduler_stop", map[string]any{"reason": "STOP_REASON_COLONIST_HEALTH", "evidence": "health", "cursor": 41, "benign": false, "tick": 5040, "detected_tick": 5030, "stop_ticks": 10, "age_at_reply_ms": 12.0}},
			{"clock_step", map[string]any{"reads": 4, "tools": map[string]any{}, "stop": true, "stop_latency_ms": 40.0, "stop_pause_s": 0.5, "elapsed_ms": 9.0}},
		} {
			if _, err := recorder.Event(event.kind, map[string]any{"tick": 5040}, false, event.payload); err != nil {
				t.Fatal(err)
			}
		}
	}
	snapshot := Snapshot{Connected: true, Tick: domain.Known(domain.Tick(5050))}
	status := RoutineStatus{ReviewsEnabled: true, MethodsEnabled: true,
		Stage:    &policy.ColonyStageRecord{Stage: policy.StageReserves, Since: 4000, Blocker: policy.StageBlockerWood, Reason: "wood floor 120 of 400"},
		Progress: []policy.GoalProgress{{Goal: "MaintainFoodStorage", Method: "hunt", Expected: "designated animal killed", LastProgress: 4500, NextReview: 6000, Blocked: policy.BlockedNoWorker}}}
	s, err := New(Config{FlightRecorder: ring, ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20,
		Routines: routineStatusFunc(func(context.Context) (RoutineStatus, error) { return status, nil })},
		snapshotFunc(func(context.Context) (Snapshot, error) { return snapshot, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	return s
}

func TestSpectatorNowPanel(t *testing.T) {
	server := testHTTP(t, spectatorServer(t, true))
	status, body := get(t, server.URL+spectatorNowPath)
	var now spectator.Now
	if err := json.Unmarshal(body, &now); err != nil {
		t.Fatal(err, string(body))
	}
	if status != 200 || now.Tick == nil || *now.Tick != 5050 {
		t.Fatalf("%d %s", status, body)
	}
	if now.Stage == nil || now.Stage.Stage != "Reserves" || now.Stage.Blocker != "wood" {
		t.Fatalf("stage: %s", body)
	}
	if len(now.Goals) != 1 || now.Goals[0].Goal != "MaintainFoodStorage" || now.Goals[0].Blocked != "no_worker" {
		t.Fatalf("goals: %s", body)
	}
	if now.Pacing.Reason != spectator.ReasonStopped || now.Pacing.Detail != "STOP_REASON_COLONIST_HEALTH" || now.Pacing.WindowTicks != 2500 {
		t.Fatalf("pacing: %s", body)
	}
	stop := now.LastStop
	if stop == nil || stop.Cursor != 41 || stop.Tick != 5040 || stop.DetectedTick == nil || *stop.StopTicks != 10 {
		t.Fatalf("stop: %s", body)
	}
	if stop.ObserveMs == nil || *stop.ObserveMs != 12 || stop.ActedMs == nil || *stop.ActedMs != 40 || stop.ReadmitMs == nil || *stop.ReadmitMs != 500 {
		t.Fatalf("stop legs: %s", body)
	}
	if now.Stops != (spectator.Counts{Stops: 1, Reactive: 1}) {
		t.Fatalf("stops: %s", body)
	}
}

// Without a flight recorder the panel still answers: the review's own facts
// stand and the recorder-derived fields stay empty rather than invented.
func TestSpectatorNowWithoutARecorder(t *testing.T) {
	server := testHTTP(t, spectatorServer(t, false))
	status, body := get(t, server.URL+spectatorNowPath)
	var now spectator.Now
	if err := json.Unmarshal(body, &now); err != nil {
		t.Fatal(err, string(body))
	}
	if status != 200 || now.Stage == nil || len(now.Goals) != 1 {
		t.Fatalf("%d %s", status, body)
	}
	if now.LastStop != nil || now.Pacing.Reason != spectator.ReasonUnknown || now.Pacing.EffectiveTPS != 0 {
		t.Fatalf("recorder-derived fields: %s", body)
	}
}

func TestSpectatorNowIsReadOnlyAndNeedsAProvider(t *testing.T) {
	api := spectatorServer(t, true)
	if out := playerCall(api, "POST", spectatorNowPath, "", ""); out.Code != 405 {
		t.Fatal(out.Code, out.Body.String())
	}
	if status, _ := get(t, testHTTP(t, api).URL+spectatorNowPath+"?since=1"); status != 400 {
		t.Fatal(status)
	}
	s, err := New(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20},
		snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	if status, _ := get(t, testHTTP(t, s).URL+spectatorNowPath); status != 404 {
		t.Fatal(status)
	}
}

// journalDigest hashes a backup of the journal: a write of any kind — a
// submission, a control record, a clock event — changes it.
func journalDigest(t *testing.T, db *store.Store) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "journal.sqlite")
	if err := db.Snapshot(context.Background(), path); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	sum := sha256.Sum256(data)
	return string(sum[:])
}

// viewerAPI is the viewer path against the fake native: the presentation and
// media providers a viewer reads, the routine provider the spectator panel
// projects, and a real journal with the control fixture that records every
// control operation.
func viewerAPI(t *testing.T) (*Server, *presentationMediaFake, *playerFixture, *store.Store, string) {
	t.Helper()
	db, err := store.Open(context.Background(), storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	identity := &c.Identity{ColonyId: proto.String("colony"), MapId: proto.Int32(0), LoadToken: proto.String("load")}
	observed := &c.ObservationContext{Identity: identity, Tick: proto.Int64(5)}
	media := &presentationMediaFake{
		render: &p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: observed, Supported: proto.Bool(true), Suspended: proto.Bool(false), WindowVisible: proto.Bool(true), RemainingLeaseMs: proto.Uint32(5000)}}},
		video:  &p.VideoReply{Outcome: &p.VideoReply_State{State: &p.VideoState{Context: observed, Supported: proto.Bool(true), Active: proto.Bool(true), SourceId: proto.String("video-abc"), RemainingLeaseMs: proto.Uint32(8000)}}},
	}
	fixture := &playerFixture{journal: db}
	snapshot := Snapshot{Connected: true, Tick: domain.Known(domain.Tick(5)), Identity: domain.Known(observation.Identity{Colony: "colony", Load: "load", Map: 0, Tick: 5})}
	status := RoutineStatus{ReviewsEnabled: true, Stage: &policy.ColonyStageRecord{Stage: policy.StageFoothold, Since: 0, Blocker: policy.StageBlockerShelter, Reason: "no shelter for all", Held: true}}
	api, err := NewWithPlayer(Config{Presentation: &presentationFake{camera: &p.CameraReply{Outcome: &p.CameraReply_Camera{Camera: &p.CameraState{Context: observed}}},
		renderState: &p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: observed, Supported: proto.Bool(true), WindowVisible: proto.Bool(true)}}}}, PresentationMedia: media,
		Routines:    routineStatusFunc(func(context.Context) (RoutineStatus, error) { return status, nil }),
		ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20},
		snapshotFunc(func(context.Context) (Snapshot, error) { return snapshot, nil }), planFunc(unavailablePlan), fixture, db)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { api.Close() })
	out := playerCall(api, "GET", "/api/player/session", "", "")
	var session struct{ Token string }
	if err := json.Unmarshal(out.Body.Bytes(), &session); err != nil || session.Token == "" {
		t.Fatal(out.Body.String(), err)
	}
	return api, media, fixture, db, session.Token
}

// A viewer connecting, watching and disconnecting must not change the
// simulation contract (#632): against the fake native, the whole viewer path
// — the video lease, the presentation reads and the spectator panel — issues
// no control operation (no speed request, no submission) and writes no
// journal row.
func TestViewerPathWritesNoJournalRowAndRequestsNoSpeed(t *testing.T) {
	api, media, fixture, db, token := viewerAPI(t)
	before := journalDigest(t, db)

	if out := playerCall(api, "POST", "/api/presentation/video-lease", `{"leaseSeconds":8}`, token); out.Code != 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	for _, route := range []string{"/api/presentation/render-state", "/api/presentation/camera", spectatorNowPath, "/api/state"} {
		if out := playerCall(api, "GET", route, "", token); out.Code != 200 {
			t.Fatal(route, out.Code, out.Body.String())
		}
	}
	if out := playerCall(api, "POST", "/api/presentation/video-lease", `{"leaseSeconds":0}`, token); out.Code != 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	// Two lease operations and the reads the viewer asked for: nothing else
	// reached native, and no control operation at all.
	if media.calls != 2 {
		t.Fatalf("viewer lease calls: %d", media.calls)
	}
	if fixture.calls != 0 {
		t.Fatalf("the viewer path issued %d control operation(s)", fixture.calls)
	}
	if journalDigest(t, db) != before {
		t.Fatal("the viewer path wrote to the journal")
	}
}
