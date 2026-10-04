package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
)

type seen struct {
	path, token, ctype, body string
}

// writeServe is a fake serve: a session endpoint that hands out the token
// current, and POST routes whose answers the test scripts.
type writeServe struct {
	mu       sync.Mutex
	token    string
	sessions int
	posts    []seen
	answer   func(path string, n int) (int, string)
}

func (w *writeServe) handler(rw http.ResponseWriter, r *http.Request) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if r.URL.Path == "/api/player/session" {
		w.sessions++
		rw.Write([]byte(`{"token":"` + w.token + `","mode":"explicit-player"}`))
		return
	}
	b, _ := io.ReadAll(r.Body)
	w.posts = append(w.posts, seen{r.URL.Path, r.Header.Get("X-RimGovernor-Player"), r.Header.Get("Content-Type"), string(b)})
	if r.Header.Get("X-RimGovernor-Player") != w.token {
		rw.WriteHeader(403)
		rw.Write([]byte(`{"code":"player_auth","detail":"Player session token required"}`))
		return
	}
	status, body := w.answer(r.URL.Path, len(w.posts))
	rw.WriteHeader(status)
	rw.Write([]byte(body))
}

func newWriteServe(t *testing.T, answer func(path string, n int) (int, string)) (*writeServe, *ServeClient) {
	t.Helper()
	w := &writeServe{token: "tok-1", answer: answer}
	srv := httptest.NewServer(http.HandlerFunc(w.handler))
	t.Cleanup(srv.Close)
	return w, NewServeClient(func() string { return srv.URL })
}

const runningRecord = `{"record":{"requestId":"r1","kind":"resume","phase":"running"},"state":{"enabled":true,"observationKnown":true},"error":null}`

var world = httpapi.Identity{ColonyID: "c", MapID: 3, LoadToken: "l"}

func TestWriteBootstrapsTokenAndSendsControlBody(t *testing.T) {
	w, c := newWriteServe(t, func(string, int) (int, string) { return 200, runningRecord })
	res := c.Resume(context.Background(), "r1", world)
	if res.Outcome != WriteAccepted || res.Control == nil || res.Control.Record.Phase != "running" {
		t.Fatalf("%+v", res)
	}
	p := w.posts[0]
	if p.path != "/api/player/control/resume" || p.token != "tok-1" || p.ctype != "application/json" {
		t.Fatalf("%+v", p)
	}
	var body map[string]any
	if err := json.Unmarshal([]byte(p.body), &body); err != nil || len(body) != 2 || body["requestId"] != "r1" {
		t.Fatalf("body %s", p.body)
	}
	exp := body["expected"].(map[string]any)
	if exp["colonyId"] != "c" || exp["loadToken"] != "l" || exp["mapId"] != float64(3) || len(exp) != 3 {
		t.Fatalf("expected %v", exp)
	}
	c.Pause(context.Background(), "r2", world)
	if w.sessions != 1 {
		t.Fatalf("token should be cached, %d session fetches", w.sessions)
	}
}

func TestWriteNoPlayerCapabilityIsRejectedNotServed(t *testing.T) {
	c := newTestServe(t, func(rw http.ResponseWriter, r *http.Request) { rw.WriteHeader(404) })
	res := c.Pause(context.Background(), "r1", world)
	if res.Outcome != WriteRejected || res.Status != 404 {
		t.Fatalf("%+v", res)
	}
	if served, err := c.Session(context.Background()); served || err != nil {
		t.Fatalf("served=%v err=%v", served, err)
	}
}

func TestWriteRetriesAfterUncertainWithSameRequestID(t *testing.T) {
	w, c := newWriteServe(t, func(_ string, n int) (int, string) {
		if n == 1 {
			return 503, `{"record":{"requestId":"r1","kind":"resume","phase":"pending"},"state":{"enabled":false,"observationKnown":true},"error":{"code":"uncertain","detail":"Control outcome is unresolved; inspect this request ID"}}`
		}
		return 200, runningRecord
	})
	first := c.Resume(context.Background(), "r1", world)
	if first.Outcome != WriteUncertain || first.Code != "uncertain" || first.Control == nil || first.Control.Record.Phase != "pending" {
		t.Fatalf("first %+v", first)
	}
	second := c.Resume(context.Background(), "r1", world)
	if second.Outcome != WriteAccepted {
		t.Fatalf("second %+v", second)
	}
	var a, b controlBody
	json.Unmarshal([]byte(w.posts[0].body), &a)
	json.Unmarshal([]byte(w.posts[1].body), &b)
	if a != b || a.RequestID != "r1" {
		t.Fatalf("retry body changed: %s vs %s", w.posts[0].body, w.posts[1].body)
	}
}

func TestControlsRetryReusesRequestIDAndReconciles(t *testing.T) {
	var mu sync.Mutex
	posts := 0
	var recordPhase string
	mux := http.NewServeMux()
	mux.HandleFunc("/api/player/session", func(rw http.ResponseWriter, r *http.Request) {
		rw.Write([]byte(`{"token":"t","mode":"explicit-player"}`))
	})
	mux.HandleFunc("/api/state", func(rw http.ResponseWriter, r *http.Request) {
		rw.Write([]byte(`{"connected":true,"game":{"stale":false},"identity":{"colonyId":"c","mapId":3,"loadToken":"l"}}`))
	})
	mux.HandleFunc("/api/player/clock", func(rw http.ResponseWriter, r *http.Request) {
		rw.Write([]byte(`{"revision":"3","inboxCursor":"9","reviewedCursor":"7","acknowledgedCursor":"5","holds":[]}`))
	})
	var ids []string
	mux.HandleFunc("/api/player/control", func(rw http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		id := ""
		if len(ids) > 0 {
			id = ids[0]
		}
		if recordPhase == "" {
			rw.Write([]byte(`{"record":null,"state":{"enabled":false,"observationKnown":true},"error":null}`))
			return
		}
		rw.Write([]byte(`{"record":{"requestId":"` + id + `","kind":"resume","phase":"` + recordPhase + `"},"state":{"enabled":true,"observationKnown":true},"error":null}`))
	})
	mux.HandleFunc("/api/player/control/resume", func(rw http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		posts++
		var b controlBody
		json.NewDecoder(r.Body).Decode(&b)
		ids = append(ids, b.RequestID)
		rw.WriteHeader(503)
		rw.Write([]byte(`{"record":{"requestId":"` + b.RequestID + `","kind":"resume","phase":"pending"},"state":{"enabled":false,"observationKnown":true},"error":{"code":"uncertain","detail":"unresolved"}}`))
	})
	srv := httptest.NewServer(mux)
	defer srv.Close()
	n := 0
	cs := NewControls(NewServeClient(func() string { return srv.URL }), func() bool { return true })
	cs.newID = func() string { n++; return "id-" + string(rune('0'+n)) }
	ctx := context.Background()
	cs.Poll(ctx)
	v := cs.View()
	if !v.Available || !v.CanResume || v.ResumeRetry {
		t.Fatalf("%+v", v)
	}
	v = cs.Bot(ctx, "resume")
	if !v.ResumeRetry || v.BotNote == "" || !v.BotNoteIsBad {
		t.Fatalf("after uncertain %+v", v)
	}
	// The journal still shows the request pending: retry re-POSTs the same id.
	mu.Lock()
	recordPhase = "pending"
	mu.Unlock()
	cs.Poll(ctx)
	cs.Bot(ctx, "resume")
	mu.Lock()
	if posts != 2 || ids[0] != ids[1] || ids[0] != "id-1" {
		t.Fatalf("posts=%d ids=%v", posts, ids)
	}
	// Now the journal shows it landed: retry reconciles without a POST.
	recordPhase = "running"
	mu.Unlock()
	v = cs.Bot(ctx, "resume")
	mu.Lock()
	defer mu.Unlock()
	if posts != 2 || v.ResumeRetry || v.BotNoteIsBad || v.BotNote != "Bot resumed." {
		t.Fatalf("posts=%d %+v", posts, v)
	}
}

func TestWriteClassifiesDefiniteRejections(t *testing.T) {
	for _, tc := range []struct {
		status int
		body   string
	}{
		{400, `{"code":"invalid_request","detail":"Invalid control request"}`},
		{409, `{"record":null,"state":{"enabled":false,"observationKnown":false},"error":{"code":"conflict","detail":"Request conflicts with current intent"}}`},
		{409, `{"record":null,"state":{"enabled":false,"observationKnown":false},"error":{"code":"capacity","detail":"Intent history is full"}}`},
	} {
		_, c := newWriteServe(t, func(string, int) (int, string) { return tc.status, tc.body })
		res := c.Pause(context.Background(), "r1", world)
		if res.Outcome != WriteRejected || res.Status != tc.status || res.Detail == "" {
			t.Errorf("%d: %+v", tc.status, res)
		}
	}
	for _, status := range []int{500, 503, 502} {
		_, c := newWriteServe(t, func(string, int) (int, string) {
			return status, `{"code":"unavailable","detail":"Player operation is unavailable"}`
		})
		if res := c.Pause(context.Background(), "r1", world); res.Outcome != WriteUncertain {
			t.Errorf("%d: %+v", status, res)
		}
	}
}

func TestWriteTimeoutIsUncertain(t *testing.T) {
	release := make(chan struct{})
	srv := httptest.NewServer(http.HandlerFunc(func(rw http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/player/session" {
			rw.Write([]byte(`{"token":"t","mode":"explicit-player"}`))
			return
		}
		select {
		case <-release:
		case <-r.Context().Done():
		}
	}))
	defer srv.Close()
	defer close(release)
	c := NewServeClient(func() string { return srv.URL })
	c.http.Timeout = 50 * time.Millisecond
	if res := c.Resume(context.Background(), "r1", world); res.Outcome != WriteUncertain {
		t.Fatalf("%+v", res)
	}
}

func TestWriteRefetchesTokenOn403AndReusesRequestID(t *testing.T) {
	w, c := newWriteServe(t, func(string, int) (int, string) { return 200, runningRecord })
	if res := c.Pause(context.Background(), "r1", world); res.Outcome != WriteAccepted {
		t.Fatalf("%+v", res)
	}
	// The controller restarted: a new token, the cached one is now wrong.
	w.mu.Lock()
	w.token = "tok-2"
	w.mu.Unlock()
	res := c.Pause(context.Background(), "r2", world)
	if res.Outcome != WriteAccepted {
		t.Fatalf("%+v", res)
	}
	if w.sessions != 2 || len(w.posts) != 3 {
		t.Fatalf("sessions=%d posts=%+v", w.sessions, w.posts)
	}
	if w.posts[1].token != "tok-1" || w.posts[2].token != "tok-2" || w.posts[1].body != w.posts[2].body {
		t.Fatalf("%+v", w.posts)
	}
}

func TestWritePersistent403IsRejectedAfterOneRetry(t *testing.T) {
	// The session always hands out a token the writes then refuse.
	srv := httptest.NewServer(http.HandlerFunc(func(rw http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/player/session" {
			rw.Write([]byte(`{"token":"stale","mode":"explicit-player"}`))
			return
		}
		rw.WriteHeader(403)
		rw.Write([]byte(`{"code":"player_auth","detail":"Player session token required"}`))
	}))
	defer srv.Close()
	c := NewServeClient(func() string { return srv.URL })
	res := c.Pause(context.Background(), "r1", world)
	if res.Outcome != WriteRejected || res.Status != 403 || res.Code != "player_auth" {
		t.Fatalf("%+v", res)
	}
}

func TestAcknowledgeBodyCarriesStringCursors(t *testing.T) {
	w, c := newWriteServe(t, func(string, int) (int, string) {
		return 200, `{"revision":"4","inboxCursor":"9","reviewedCursor":"7","acknowledgedCursor":"7","holds":[]}`
	})
	res := c.AcknowledgeClock(context.Background(), ClockAck{RequestID: "a1", ExpectedRevision: "3", ThroughCursor: "7"})
	if res.Outcome != WriteAccepted || res.Clock == nil || res.Clock.Acknowledged != "7" {
		t.Fatalf("%+v", res)
	}
	p := w.posts[0]
	if p.path != "/api/player/clock/acknowledge" || p.body != `{"requestId":"a1","expectedRevision":"3","throughCursor":"7"}` {
		t.Fatalf("%+v", p)
	}
	// A stale revision is a definite 409.
	_, c = newWriteServe(t, func(string, int) (int, string) {
		return 409, `{"code":"conflict","detail":"Request conflicts with current intent"}`
	})
	if res := c.AcknowledgeClock(context.Background(), ClockAck{"a1", "2", "7"}); res.Outcome != WriteRejected {
		t.Fatalf("%+v", res)
	}
}

func TestControlFeedKeeps503RecordButNotBareFailure(t *testing.T) {
	var bare bool
	c := newTestServe(t, func(rw http.ResponseWriter, r *http.Request) {
		rw.WriteHeader(503)
		if bare {
			rw.Write([]byte(`{"code":"unavailable","detail":"Controller data is unavailable"}`))
			return
		}
		rw.Write([]byte(`{"record":{"requestId":"r","kind":"resume","phase":"uncertain"},"state":{"enabled":false,"observationKnown":true},"error":{"code":"uncertain","detail":"unresolved"}}`))
	})
	r := c.Control(context.Background())
	if r.Value == nil || r.Value.Error == nil || r.Value.Error.Code != "uncertain" || r.Value.Record.Phase != "uncertain" || r.Stale {
		t.Fatalf("%+v", r)
	}
	bare = true
	r = c.Control(context.Background())
	if r.Error == "" || !r.Stale {
		t.Fatalf("%+v", r)
	}
}

func TestReconcileControlMatchesByRequestID(t *testing.T) {
	c := newTestServe(t, func(rw http.ResponseWriter, r *http.Request) {
		rw.Write([]byte(`{"record":{"requestId":"r1","kind":"pause","phase":"paused"},"state":{"enabled":false,"observationKnown":true},"error":null}`))
	})
	if res := c.ReconcileControl(context.Background(), "r1"); res.Outcome != WriteAccepted {
		t.Fatalf("%+v", res)
	}
	if res := c.ReconcileControl(context.Background(), "other"); res.Outcome != WriteUncertain {
		t.Fatalf("%+v", res)
	}
}
