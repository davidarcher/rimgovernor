package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	"net/http/httptest"
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

func (f *playerFixture) Resume(ctx context.Context, q store.ControlRequest) (store.ControlRecord, error) {
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
	return f.journal.CompleteControl(ctx, q.RequestID, store.RunningControl, 7)
}
func (f *playerFixture) Pause(ctx context.Context, q store.ControlRequest) (store.ControlRecord, error) {
	f.calls++
	f.seen = q
	r, created, e := f.journal.BeginControl(ctx, q)
	if e != nil || !created {
		return r, e
	}
	return f.journal.CompleteControl(ctx, q.RequestID, store.PausedControl, 0)
}
func (f *playerFixture) State() buildingruntime.ControlState { return f.state }
func playerAPI(t *testing.T) (*Server, *playerFixture) {
	t.Helper()
	db, e := store.Open(context.Background(), storetest.Path(t))
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
		w := playerCall(s, "POST", "/api/player/control/pause", manualJSON, token)
		if w.Code != 403 {
			t.Fatal(w.Code)
		}
	}
	for key, value := range map[string]string{"Origin": "http://foreign.example", "Sec-Fetch-Site": "cross-site"} {
		r := httptest.NewRequest("POST", "http://127.0.0.1/api/player/control/pause", strings.NewReader(manualJSON))
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
	}{{"GET", "/api/player/control/pause", "", 405}, {"POST", "/api/player/session", "", 405}, {"GET", "/api/player/session", "body", 400}, {"POST", "/api/player/control/pause?x=1", manualJSON, 400}, {"POST", "/api/player/control/pause", strings.Repeat("x", 8193), 400}, {"POST", "/api/player/control/pause", `{}`, 400}, {"POST", "/api/player/control/pause", manualJSON[:len(manualJSON)-1] + `,"kind":"resume"}`, 400}, {"GET", "/api/player/control?requestId=a", "", 400}} {
		w := playerCall(s, tc.method, tc.path, tc.body, session.Token)
		if w.Code != tc.code {
			t.Fatal(tc, w.Code, w.Body.String())
		}
	}
	r := httptest.NewRequest("POST", "http://127.0.0.1/api/player/control/pause", strings.NewReader(manualJSON))
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
	if w := playerCall(readonly, "POST", "/api/player/control/pause", manualJSON, session.Token); w.Code != 501 {
		t.Fatal(w.Code)
	}
	if w := playerCall(readonly, "GET", "/api/player/session", "", ""); w.Code != 404 {
		t.Fatal(w.Code)
	}
}
func TestPlayerHTTPDurableRecoveryAndLiveState(t *testing.T) {
	s, f := playerAPI(t)
	token := s.playerToken
	acquire := `{"requestId":"control","expected":` + requestWorld + `}`
	f.uncertain = true
	w := playerCall(s, "POST", "/api/player/control/resume", acquire, token)
	var result controlDTO
	if e := json.Unmarshal(w.Body.Bytes(), &result); e != nil || w.Code != 503 || result.Record == nil || result.Record.Phase != store.UncertainControl || result.State.Enabled || result.State.Generation != nil || result.Error == nil || strings.Contains(w.Body.String(), "secret") {
		t.Fatal(w.Code, w.Body.String(), e)
	}
	if f.seen.Kind != store.ResumeControl || f.seen.RequestID != "control" {
		t.Fatal(f.seen)
	}
	w = playerCall(s, "GET", "/api/player/control", "", "")
	if w.Code != 200 || !strings.Contains(w.Body.String(), `"phase":"uncertain"`) {
		t.Fatal(w.Code, w.Body.String())
	}
	w = playerCall(s, "POST", "/api/player/control/resume", acquire, token)
	if w.Code != 200 || !strings.Contains(w.Body.String(), `"enabled":false`) {
		t.Fatal(w.Code, w.Body.String())
	}
	manual := strings.Replace(manualJSON, `"request"`, `"manual"`, 1)
	w = playerCall(s, "POST", "/api/player/control/pause", manual, token)
	if w.Code != 200 || f.seen.Kind != store.PauseControl || !strings.Contains(w.Body.String(), `"phase":"paused"`) {
		t.Fatal(w.Code, w.Body.String())
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
	q := store.ControlRequest{RequestID: "a", Kind: store.PauseControl, World: store.World{Colony: "colony", Load: "load"}}
	if _, e := projectControl(store.ControlRecord{Request: q, Phase: "invented"}); e == nil {
		t.Fatal("unknown phase")
	}
	if _, e := projectControl(store.ControlRecord{Request: q, Phase: store.RunningControl, NativeGeneration: 3}); e == nil {
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
	request := store.ControlRequest{RequestID: "granted", Kind: store.ResumeControl, World: store.World{Colony: "colony", Load: "load", Map: 0}}
	if _, _, e := f.journal.BeginControl(context.Background(), request); e != nil {
		t.Fatal(e)
	}
	if _, e := f.journal.CompleteControl(context.Background(), request.RequestID, store.RunningControl, 7); e != nil {
		t.Fatal(e)
	}
	w := playerCall(s, "GET", "/api/player/control", "", "")
	if w.Code != 200 || !strings.Contains(w.Body.String(), `"phase":"running"`) || !strings.Contains(w.Body.String(), `"enabled":false`) || !strings.Contains(w.Body.String(), `"generation":null`) {
		t.Fatal(w.Code, w.Body.String())
	}
	if f.calls != 0 {
		t.Fatal("read acquired authority")
	}
}
