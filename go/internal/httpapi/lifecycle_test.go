package httpapi

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

type lifecycleFake struct {
	save       *l.SaveReply
	load       *l.LoadReply
	err        error
	calls      int
	seenSave   *l.SaveRequest
	seenLoad   *l.LoadRequest
	readSaveID string
	readLoadID string
}

func (f *lifecycleFake) Save(ctx context.Context, q *l.SaveRequest) (*l.SaveReply, bridge.Result, error) {
	f.calls++
	f.seenSave = q
	return f.save, bridge.Result{}, f.err
}
func (f *lifecycleFake) ReadSave(ctx context.Context, id string) (*l.SaveReply, bridge.Result, error) {
	f.calls++
	f.readSaveID = id
	return f.save, bridge.Result{}, f.err
}
func (f *lifecycleFake) Load(ctx context.Context, q *l.LoadRequest) (*l.LoadReply, bridge.Result, error) {
	f.calls++
	f.seenLoad = q
	return f.load, bridge.Result{}, f.err
}
func (f *lifecycleFake) ReadLoad(ctx context.Context, id string) (*l.LoadReply, bridge.Result, error) {
	f.calls++
	f.readLoadID = id
	return f.load, bridge.Result{}, f.err
}

func lifecycleAPI(t *testing.T, mode string) (*Server, *lifecycleFake, string) {
	t.Helper()
	db, e := store.Open(context.Background(), filepath.Join(t.TempDir(), "lifecycle.db"))
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(func() { db.Close() })
	identity := &c.Identity{ColonyId: proto.String("colony"), MapId: proto.Int32(0), LoadToken: proto.String("load")}
	observed := &c.ObservationContext{Identity: identity, Tick: proto.Int64(42)}
	fake := &lifecycleFake{
		save: &l.SaveReply{Outcome: &l.SaveReply_Completed{Completed: &l.SaveCompleted{
			RequestId: proto.String("save-1"), SaveName: proto.String("checkpoint"), Context: observed,
			Paused: proto.Bool(true), PlayerDirection: proto.Uint64(1), ByteLength: proto.Uint64(1024)}}},
		load: &l.LoadReply{Outcome: &l.LoadReply_Completed{Completed: &l.LoadCompleted{
			RequestId: proto.String("load-1"), SaveName: proto.String("checkpoint"),
			Loaded: &l.LoadedIdentity{Context: observed, Paused: proto.Bool(true)}, Readiness: l.Readiness_READINESS_MAP.Enum()}}},
	}
	f := &playerFixture{journal: db}
	snapshot := Snapshot{
		Connected:  true,
		Mode:       mode,
		Identity:   domain.Known(observation.Identity{Colony: "colony", Load: "load", Map: 0, Tick: 42}),
		Generation: domain.Known(domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Direction: 1, Plan: "plan", Revision: 1, Native: 1}),
		Tick:       domain.Known(domain.Tick(42)),
	}
	s, e := NewWithPlayer(Config{Lifecycle: fake, ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20},
		snapshotFunc(func(context.Context) (Snapshot, error) { return snapshot, nil }), planFunc(unavailablePlan), f, db)
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(func() { s.Close() })
	token := playerCall(s, "GET", "/api/player/session", "", "")
	var session struct{ Token string }
	if err := json.Unmarshal(token.Body.Bytes(), &session); err != nil || session.Token == "" {
		t.Fatal(token.Body.String(), err)
	}
	return s, fake, session.Token
}

func TestLifecycleSave(t *testing.T) {
	s, f, token := lifecycleAPI(t, "manual")
	out := playerCall(s, "POST", "/api/lifecycle/save", `{"requestId":"save-1","saveName":"checkpoint"}`, token)
	if out.Code != 201 || f.calls != 1 {
		t.Fatal(out.Code, out.Body.String())
	}
	var dto saveReplyDTO
	if err := json.Unmarshal(out.Body.Bytes(), &dto); err != nil || dto.RequestID != "save-1" || dto.SaveName != "checkpoint" || !dto.Paused || dto.Identity.ColonyID != "colony" || dto.Tick != 42 || dto.Direction != 1 || dto.ByteLength != 1024 {
		t.Fatal(out.Body.String(), err)
	}
	if f.seenSave.GetPlayer().GetIdentity().GetColonyId() != "colony" || f.seenSave.GetPlayer().GetPlayerDirection() != 1 || f.seenSave.GetPlayer().GetRequestId() != "save-1" || f.seenSave.GetSaveName() != "checkpoint" || f.seenSave.GetExpectedTick() != 42 {
		t.Fatal(f.seenSave)
	}
}
func TestLifecycleSaveRequiresManualControl(t *testing.T) {
	s, f, token := lifecycleAPI(t, "automate")
	out := playerCall(s, "POST", "/api/lifecycle/save", `{"requestId":"save-1","saveName":"checkpoint"}`, token)
	if out.Code != 409 || f.calls != 0 {
		t.Fatal(out.Code, out.Body.String())
	}
}
func TestLifecycleLoad(t *testing.T) {
	s, f, token := lifecycleAPI(t, "automate")
	out := playerCall(s, "POST", "/api/lifecycle/load", `{"requestId":"load-1","saveName":"checkpoint","readiness":"map","timeoutMs":5000}`, token)
	if out.Code != 201 || f.calls != 1 {
		t.Fatal(out.Code, out.Body.String())
	}
	var dto loadReplyDTO
	if err := json.Unmarshal(out.Body.Bytes(), &dto); err != nil || dto.RequestID != "load-1" || dto.SaveName != "checkpoint" || dto.Readiness != "map" || !dto.Paused || dto.Identity.ColonyID != "colony" {
		t.Fatal(out.Body.String(), err)
	}
	if f.seenLoad.GetRequestId() != "load-1" || f.seenLoad.GetSaveName() != "checkpoint" || f.seenLoad.GetReadiness() != l.Readiness_READINESS_MAP || f.seenLoad.GetTimeoutMs() != 5000 {
		t.Fatal(f.seenLoad)
	}
	if f.seenLoad.GetExpectedPlayer().GetIdentity().GetColonyId() != "colony" || f.seenLoad.GetExpectedPlayer().GetPlayerDirection() != 1 || f.seenLoad.GetExpectedPlayer().GetRequestId() != "load-1" {
		t.Fatal(f.seenLoad.GetExpectedPlayer())
	}
}
func TestLifecycleReadSaveAndLoad(t *testing.T) {
	s, f, _ := lifecycleAPI(t, "manual")
	out := playerCall(s, "GET", "/api/lifecycle/save?requestId=save-1", "", "")
	if out.Code != 200 || f.readSaveID != "save-1" {
		t.Fatal(out.Code, out.Body.String())
	}
	out = playerCall(s, "GET", "/api/lifecycle/load?requestId=load-1", "", "")
	if out.Code != 200 || f.readLoadID != "load-1" {
		t.Fatal(out.Code, out.Body.String())
	}
}
func TestLifecycleReadRequiresRequestID(t *testing.T) {
	s, f, _ := lifecycleAPI(t, "manual")
	for _, path := range []string{"/api/lifecycle/save", "/api/lifecycle/load"} {
		out := playerCall(s, "GET", path, "", "")
		if out.Code != 400 || f.calls != 0 {
			t.Fatal(path, out.Code, out.Body.String())
		}
	}
}
func TestLifecycleRequiresPlayerToken(t *testing.T) {
	s, f, _ := lifecycleAPI(t, "manual")
	for _, tc := range []struct{ path, body string }{
		{"/api/lifecycle/save", `{"requestId":"save-1","saveName":"checkpoint"}`},
		{"/api/lifecycle/load", `{"requestId":"load-1","saveName":"checkpoint","readiness":"map","timeoutMs":5000}`},
	} {
		out := playerCall(s, "POST", tc.path, tc.body, "")
		if out.Code != 403 || f.calls != 0 {
			t.Fatal(tc.path, out.Code)
		}
		out = playerCall(s, "POST", tc.path, tc.body, "wrong-token")
		if out.Code != 403 || f.calls != 0 {
			t.Fatal(tc.path, out.Code)
		}
	}
}
func TestLifecycleValidation(t *testing.T) {
	for _, tc := range []struct{ name, path, body string }{
		{"missingSaveName", "/api/lifecycle/save", `{"requestId":"save-1"}`},
		{"invalidRequestID", "/api/lifecycle/save", `{"requestId":"","saveName":"checkpoint"}`},
		{"unknownField", "/api/lifecycle/save", `{"requestId":"save-1","saveName":"checkpoint","extra":true}`},
		{"missingReadiness", "/api/lifecycle/load", `{"requestId":"load-1","saveName":"checkpoint","timeoutMs":5000}`},
		{"unknownReadiness", "/api/lifecycle/load", `{"requestId":"load-1","saveName":"checkpoint","readiness":"sideways","timeoutMs":5000}`},
		{"timeoutTooSmall", "/api/lifecycle/load", `{"requestId":"load-1","saveName":"checkpoint","readiness":"map","timeoutMs":10}`},
		{"timeoutTooLarge", "/api/lifecycle/load", `{"requestId":"load-1","saveName":"checkpoint","readiness":"map","timeoutMs":999999}`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s, f, token := lifecycleAPI(t, "manual")
			out := playerCall(s, "POST", tc.path, tc.body, token)
			if out.Code != 400 || f.calls != 0 {
				t.Fatal(out.Code, out.Body.String())
			}
		})
	}
}
func TestLifecycleMethodNotAllowed(t *testing.T) {
	s, f, token := lifecycleAPI(t, "manual")
	out := playerCall(s, "PUT", "/api/lifecycle/save", `{}`, token)
	if out.Code != 405 || f.calls != 0 {
		t.Fatal(out.Code, out.Body.String())
	}
}
func TestLifecycleUnavailableWithoutConfig(t *testing.T) {
	db, e := store.Open(context.Background(), filepath.Join(t.TempDir(), "lifecycle2.db"))
	if e != nil {
		t.Fatal(e)
	}
	defer db.Close()
	f := &playerFixture{journal: db}
	s, e := NewWithPlayer(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan), f, db)
	if e != nil {
		t.Fatal(e)
	}
	defer s.Close()
	token := playerCall(s, "GET", "/api/player/session", "", "")
	var session struct{ Token string }
	_ = json.Unmarshal(token.Body.Bytes(), &session)
	out := playerCall(s, "POST", "/api/lifecycle/save", `{"requestId":"save-1","saveName":"checkpoint"}`, session.Token)
	if out.Code != 404 {
		t.Fatal(out.Code, out.Body.String())
	}
}
