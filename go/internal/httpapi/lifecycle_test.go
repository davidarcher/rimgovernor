package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

type lifecycleFake struct {
	save       *l.SaveReply
	load       *l.LoadReply
	err        error
	failCalls  int // when > 0, the first failCalls calls return err instead of succeeding
	calls      int
	seenSave   *l.SaveRequest
	seenLoad   *l.LoadRequest
	readSaveID string
	readLoadID string
}

func (f *lifecycleFake) errFor() error {
	if f.calls <= f.failCalls {
		return f.err
	}
	return nil
}
func (f *lifecycleFake) Save(ctx context.Context, q *l.SaveRequest) (*l.SaveReply, bridge.Result, error) {
	f.calls++
	f.seenSave = q
	return f.save, bridge.Result{}, f.errFor()
}
func (f *lifecycleFake) ReadSave(ctx context.Context, id string) (*l.SaveReply, bridge.Result, error) {
	f.calls++
	f.readSaveID = id
	return f.save, bridge.Result{}, f.errFor()
}
func (f *lifecycleFake) Load(ctx context.Context, q *l.LoadRequest) (*l.LoadReply, bridge.Result, error) {
	f.calls++
	f.seenLoad = q
	return f.load, bridge.Result{}, f.errFor()
}
func (f *lifecycleFake) ReadLoad(ctx context.Context, id string) (*l.LoadReply, bridge.Result, error) {
	f.calls++
	f.readLoadID = id
	return f.load, bridge.Result{}, f.errFor()
}

// fakeAttention records acknowledgements an attention-blocked lifecycle
// request retried against.
type fakeAttention struct {
	acked []string
	err   error
}

func (f *fakeAttention) AckAttention(ctx context.Context, attentionID string) error {
	f.acked = append(f.acked, attentionID)
	return f.err
}

func attentionRefusal(attentionID string) error {
	body, _ := json.Marshal(struct {
		Status    string `json:"status"`
		Attention struct {
			AttentionID string `json:"attentionId"`
		} `json:"attention"`
	}{Status: "blocked_by_attention", Attention: struct {
		AttentionID string `json:"attentionId"`
	}{attentionID}})
	return &bridge.Refusal{Tool: "games_call_tool", Result: bridge.Result{Structured: body}}
}

func lifecycleAPI(t *testing.T, mode string) (*Server, *lifecycleFake, string) {
	t.Helper()
	s, f, _, token := lifecycleAPIWithConfig(t, mode, true, nil)
	return s, f, token
}

// lifecycleAPIWithConfig builds a lifecycle test server like lifecycleAPI,
// additionally allowing the snapshot's identity to be omitted (a cold
// bootstrap, e.g. the game still at its main menu) and an
// AttentionAcknowledger to be wired in.
func lifecycleAPIWithConfig(t *testing.T, mode string, knownIdentity bool, fa *fakeAttention) (*Server, *lifecycleFake, *fakeAttention, string) {
	t.Helper()
	db, e := store.Open(context.Background(), storetest.Path(t))
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
	snapshot := Snapshot{Connected: true, Mode: mode}
	if knownIdentity {
		snapshot.Identity = domain.Known(observation.Identity{Colony: "colony", Load: "load", Map: 0, Tick: 42})
		snapshot.Generation = domain.Known(domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 1})
		snapshot.Tick = domain.Known(domain.Tick(42))
	}
	if fa == nil {
		fa = &fakeAttention{}
	}
	s, e := NewWithPlayer(Config{Lifecycle: fake, Attention: fa, ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20},
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
	return s, fake, fa, session.Token
}

func TestLifecycleSave(t *testing.T) {
	s, f, token := lifecycleAPI(t, "manual")
	out := playerCall(s, "POST", "/api/lifecycle/save", `{"requestId":"save-1","saveName":"checkpoint"}`, token)
	if out.Code != 201 || f.calls != 1 {
		t.Fatal(out.Code, out.Body.String())
	}
	var dto saveReplyDTO
	if err := json.Unmarshal(out.Body.Bytes(), &dto); err != nil || dto.RequestID != "save-1" || dto.SaveName != "checkpoint" || !dto.Paused || dto.Identity.ColonyID != "colony" || dto.Tick != 42 || dto.ByteLength != 1024 {
		t.Fatal(out.Body.String(), err)
	}
	if f.seenSave.GetPlayer().GetIdentity().GetColonyId() != "colony" || f.seenSave.GetPlayer().GetPlayerDirection() != 1 || f.seenSave.GetPlayer().GetRequestId() != "save-1" || f.seenSave.GetSaveName() != "checkpoint" || f.seenSave.ExpectedTick != nil {
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
	db, e := store.Open(context.Background(), storetest.Path(t))
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

func TestLifecycleLoadColdBootstrapOmitsExpectedPlayer(t *testing.T) {
	s, f, _, token := lifecycleAPIWithConfig(t, "manual", false, nil)
	out := playerCall(s, "POST", "/api/lifecycle/load", `{"requestId":"load-1","saveName":"checkpoint","readiness":"map","timeoutMs":5000}`, token)
	if out.Code != 201 || f.calls != 1 {
		t.Fatal(out.Code, out.Body.String())
	}
	if f.seenLoad.ExpectedPlayer != nil {
		t.Fatal("expected nil ExpectedPlayer for a cold bootstrap load", f.seenLoad.ExpectedPlayer)
	}
}

func TestLifecycleLoadRetriesAfterAttentionAck(t *testing.T) {
	fa := &fakeAttention{}
	s, f, fa, token := lifecycleAPIWithConfig(t, "automate", true, fa)
	f.err = attentionRefusal("attn_1")
	f.failCalls = 1
	out := playerCall(s, "POST", "/api/lifecycle/load", `{"requestId":"load-1","saveName":"checkpoint","readiness":"map","timeoutMs":5000}`, token)
	if out.Code != 201 || f.calls != 2 {
		t.Fatal(out.Code, out.Body.String(), f.calls)
	}
	if len(fa.acked) != 1 || fa.acked[0] != "attn_1" {
		t.Fatal(fa.acked)
	}
}

func TestLifecycleReadLoadRetriesAfterAttentionAck(t *testing.T) {
	fa := &fakeAttention{}
	s, f, fa, _ := lifecycleAPIWithConfig(t, "manual", true, fa)
	f.err = attentionRefusal("attn_2")
	f.failCalls = 1
	out := playerCall(s, "GET", "/api/lifecycle/load?requestId=load-1", "", "")
	if out.Code != 200 || f.calls != 2 {
		t.Fatal(out.Code, out.Body.String(), f.calls)
	}
	if len(fa.acked) != 1 || fa.acked[0] != "attn_2" {
		t.Fatal(fa.acked)
	}
}

func TestLifecycleLoadDoesNotRetryOnUnrelatedFailure(t *testing.T) {
	fa := &fakeAttention{}
	s, f, fa, token := lifecycleAPIWithConfig(t, "automate", true, fa)
	f.err = errors.New("boom")
	f.failCalls = 1
	out := playerCall(s, "POST", "/api/lifecycle/load", `{"requestId":"load-1","saveName":"checkpoint","readiness":"map","timeoutMs":5000}`, token)
	if out.Code == 201 || f.calls != 1 {
		t.Fatal(out.Code, out.Body.String(), f.calls)
	}
	if len(fa.acked) != 0 {
		t.Fatal(fa.acked)
	}
}

func TestBlockingAttentionID(t *testing.T) {
	if _, ok := blockingAttentionID(errors.New("boom")); ok {
		t.Fatal("non-refusal error must not report an attention id")
	}
	unrelated := &bridge.Refusal{Tool: "games_call_tool", Result: bridge.Result{Structured: []byte(`{"status":"foreignOwner"}`)}}
	if _, ok := blockingAttentionID(unrelated); ok {
		t.Fatal("refusal without blocked_by_attention status must not report an attention id")
	}
	id, ok := blockingAttentionID(attentionRefusal("attn_9"))
	if !ok || id != "attn_9" {
		t.Fatal(id, ok)
	}
}
