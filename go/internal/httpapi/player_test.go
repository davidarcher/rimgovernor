package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

type playerFixture struct {
	journal   *store.Store
	calls     int
	state     buildingruntime.ControlState
	uncertain bool
	seen      store.ControlRequest
}

func (f *playerFixture) Submit(ctx context.Context, q store.SubmissionRequest) (store.Submission, bool, error) {
	f.calls++
	return f.journal.SubmitBuilding(ctx, q)
}
func (f *playerFixture) Acquire(ctx context.Context, q store.ControlRequest) (store.ControlRecord, error) {
	f.calls++
	f.seen = q
	r, created, e := f.journal.BeginControl(ctx, q)
	if e != nil || !created {
		return r, e
	}
	if f.uncertain {
		r, e = f.journal.CompleteControl(ctx, q.RequestID, store.UncertainControl, 0)
		if e != nil {
			return r, e
		}
		return r, errors.New("secret native diagnostic")
	}
	return f.journal.CompleteControl(ctx, q.RequestID, store.GrantedControl, 7)
}
func (f *playerFixture) Manual(ctx context.Context, q store.ControlRequest) (store.ControlRecord, error) {
	f.calls++
	f.seen = q
	r, created, e := f.journal.BeginControl(ctx, q)
	if e != nil || !created {
		return r, e
	}
	return f.journal.CompleteControl(ctx, q.RequestID, store.DisabledControl, 0)
}
func (f *playerFixture) State() buildingruntime.ControlState { return f.state }
func playerAPI(t *testing.T) (*Server, *playerFixture) {
	t.Helper()
	db, e := store.Open(context.Background(), filepath.Join(t.TempDir(), "player.db"))
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(func() { db.Close() })
	f := &playerFixture{journal: db}
	s, e := NewWithPlayer(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan), f, db)
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(func() { s.Close() })
	return s, f
}
func playerCall(s *Server, method, path, body, token string) *httptest.ResponseRecorder {
	r := httptest.NewRequest(method, "http://127.0.0.1"+path, strings.NewReader(body))
	if method == "POST" {
		r.Header.Set("Content-Type", "application/json")
	}
	if token != "" {
		r.Header.Set("X-RimGovernor-Player", token)
	}
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	return w
}
func TestPlayerHTTPAuthenticationAndBoundaries(t *testing.T) {
	s, f := playerAPI(t)
	bootstrap := playerCall(s, "GET", "/api/player/session", "", "")
	var session struct{ Token, Mode string }
	if e := json.Unmarshal(bootstrap.Body.Bytes(), &session); e != nil || len(session.Token) != 64 || session.Mode != "explicit-player" {
		t.Fatal(bootstrap.Body.String(), e)
	}
	for _, token := range []string{"", "wrong"} {
		w := playerCall(s, "POST", "/api/buildings/plans", submissionJSON, token)
		if w.Code != 403 {
			t.Fatal(w.Code)
		}
	}
	for key, value := range map[string]string{"Origin": "http://foreign.example", "Sec-Fetch-Site": "cross-site"} {
		r := httptest.NewRequest("POST", "http://127.0.0.1/api/buildings/plans", strings.NewReader(submissionJSON))
		r.Header.Set("Content-Type", "application/json")
		r.Header.Set("X-RimGovernor-Player", session.Token)
		r.Header.Set(key, value)
		w := httptest.NewRecorder()
		s.Handler().ServeHTTP(w, r)
		if w.Code != 403 {
			t.Fatal(w.Code)
		}
	}
	for _, tc := range []struct {
		method, path, body string
		code               int
	}{{"GET", "/api/buildings/plans", "", 405}, {"POST", "/api/player/session", "", 405}, {"GET", "/api/player/session", "body", 400}, {"POST", "/api/buildings/plans?x=1", submissionJSON, 400}, {"POST", "/api/buildings/plans", strings.Repeat("x", 8193), 400}, {"POST", "/api/buildings/plans", `{}`, 400}, {"POST", "/api/player/control/manual", manualJSON[:len(manualJSON)-1] + `,"kind":"acquire"}`, 400}, {"GET", "/api/player/control?requestId=a&requestId=b", "", 400}} {
		w := playerCall(s, tc.method, tc.path, tc.body, session.Token)
		if w.Code != tc.code {
			t.Fatal(tc, w.Code, w.Body.String())
		}
	}
	r := httptest.NewRequest("POST", "http://127.0.0.1/api/buildings/plans", strings.NewReader(submissionJSON))
	r.Header.Set("X-RimGovernor-Player", session.Token)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	if w.Code != 415 || f.calls != 0 {
		t.Fatal(w.Code, f.calls)
	}
	if bootstrap.Header().Get("Cache-Control") != "no-store" || bootstrap.Header().Get("Access-Control-Allow-Origin") != "" {
		t.Fatal(bootstrap.Header())
	}
	readonly := newTestAPI(t, s.snapshots, s.plans)
	if w := playerCall(readonly, "POST", "/api/buildings/plans", submissionJSON, session.Token); w.Code != 501 {
		t.Fatal(w.Code)
	}
	if w := playerCall(readonly, "GET", "/api/player/session", "", ""); w.Code != 404 {
		t.Fatal(w.Code)
	}
}
func TestPlayerHTTPDurableRecoveryAndLiveState(t *testing.T) {
	s, f := playerAPI(t)
	token := s.playerToken
	for i := 0; i < 2; i++ {
		w := playerCall(s, "POST", "/api/buildings/plans", submissionJSON, token)
		want := 201
		if i == 1 {
			want = 200
		}
		if w.Code != want {
			t.Fatal(w.Code, w.Body.String())
		}
	}
	w := playerCall(s, "GET", "/api/buildings/submission?requestId=request", "", "")
	var submission submissionDTO
	if e := json.Unmarshal(w.Body.Bytes(), &submission); e != nil || w.Code != 200 || submission.Building.Stuff != "" || submission.Revision != 1 {
		t.Fatal(w.Body.String(), e)
	}
	acquire := `{"requestId":"control","expected":` + requestWorld + `,"planId":"` + string(submission.PlanID) + `","revision":"1","expectedDirection":"0"}`
	f.uncertain = true
	w = playerCall(s, "POST", "/api/player/control/acquire", acquire, token)
	var result controlDTO
	if e := json.Unmarshal(w.Body.Bytes(), &result); e != nil || w.Code != 503 || result.Record == nil || result.Record.Phase != store.UncertainControl || result.State.Enabled || result.State.Generation != nil || result.Error == nil || strings.Contains(w.Body.String(), "secret") {
		t.Fatal(w.Code, w.Body.String(), e)
	}
	if f.seen.Kind != store.AcquireControl || f.seen.Plan != submission.PlanID {
		t.Fatal(f.seen)
	}
	for _, path := range []string{"/api/player/control", "/api/player/control?requestId=control"} {
		w = playerCall(s, "GET", path, "", "")
		if w.Code != 200 || !strings.Contains(w.Body.String(), `"phase":"uncertain"`) {
			t.Fatal(w.Code, w.Body.String())
		}
	}
	w = playerCall(s, "POST", "/api/player/control/acquire", acquire, token)
	if w.Code != 200 || !strings.Contains(w.Body.String(), `"enabled":false`) {
		t.Fatal(w.Code, w.Body.String())
	}
	manual := strings.Replace(manualJSON, `"request"`, `"manual"`, 1)
	w = playerCall(s, "POST", "/api/player/control/manual", manual, token)
	if w.Code != 200 || f.seen.Kind != store.ManualControl || !strings.Contains(w.Body.String(), `"phase":"disabled"`) {
		t.Fatal(w.Code, w.Body.String())
	}
	w = playerCall(s, "GET", "/api/player/control?requestId=missing", "", "")
	if w.Code != 404 {
		t.Fatal(w.Code)
	}
}
func TestPlayerHTTPProjectionGuards(t *testing.T) {
	s, _ := playerAPI(t)
	w := playerCall(s, "GET", "/api/player/control", "", "")
	if w.Code != 200 || !strings.Contains(w.Body.String(), `"record":null`) {
		t.Fatal(w.Code, w.Body.String())
	}
	if _, e := projectPlayerState(buildingruntime.ControlState{Enabled: true}); e == nil {
		t.Fatal("fabricated authority")
	}
	q := store.ControlRequest{RequestID: "a", Kind: store.ManualControl, World: store.World{Colony: "colony", Load: "load"}}
	if _, e := projectControl(store.ControlRecord{Request: q, Direction: 1, Phase: "invented"}); e == nil {
		t.Fatal("unknown phase")
	}
	if _, e := projectControl(store.ControlRecord{Request: q, Direction: 1, Phase: store.GrantedControl, NativeGeneration: 3}); e == nil {
		t.Fatal("Manual grant")
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/control", nil).WithContext(ctx)
	out := httptest.NewRecorder()
	s.Handler().ServeHTTP(out, r)
	if out.Code != 408 {
		t.Fatal(out.Code)
	}
}

func TestPlayerHTTPHistoricalGrantDoesNotEnable(t *testing.T) {
	s, f := playerAPI(t)
	submitted, _, e := f.journal.SubmitBuilding(context.Background(), mustSubmission(t))
	if e != nil {
		t.Fatal(e)
	}
	request := store.ControlRequest{RequestID: "granted", Kind: store.AcquireControl, World: submitted.Request.World, Plan: submitted.Plan, Revision: 1}
	if _, _, e = f.journal.BeginControl(context.Background(), request); e != nil {
		t.Fatal(e)
	}
	if _, e = f.journal.CompleteControl(context.Background(), request.RequestID, store.GrantedControl, 7); e != nil {
		t.Fatal(e)
	}
	w := playerCall(s, "GET", "/api/player/control?requestId=granted", "", "")
	if w.Code != 200 || !strings.Contains(w.Body.String(), `"phase":"granted"`) || !strings.Contains(w.Body.String(), `"enabled":false`) || !strings.Contains(w.Body.String(), `"generation":null`) {
		t.Fatal(w.Code, w.Body.String())
	}
	if f.calls != 0 {
		t.Fatal("read acquired authority")
	}
}
func mustSubmission(t *testing.T) store.SubmissionRequest {
	t.Helper()
	q, e := decodeBuildingSubmission(strings.NewReader(submissionJSON))
	if e != nil {
		t.Fatal(e)
	}
	return q
}

func (f *playerFixture) SubmitDraft(ctx context.Context, q store.DraftSubmissionRequest) (store.DraftSubmission, bool, error) {
	f.calls++
	return f.journal.SubmitDraft(ctx, q)
}
func (f *playerFixture) SubmitCaravanDeparture(ctx context.Context, q store.CaravanDepartureSubmissionRequest) (store.CaravanDepartureSubmission, bool, error) {
	f.calls++
	return f.journal.SubmitCaravanDeparture(ctx, q)
}
func (f *playerFixture) SubmitQuestAccept(ctx context.Context, q store.QuestAcceptSubmissionRequest) (store.QuestAcceptSubmission, bool, error) {
	f.calls++
	return f.journal.SubmitQuestAccept(ctx, q)
}
func (f *playerFixture) SubmitSettlementGift(ctx context.Context, q store.SettlementGiftSubmissionRequest) (store.SettlementGiftSubmission, bool, error) {
	f.calls++
	return f.journal.SubmitSettlementGift(ctx, q)
}
