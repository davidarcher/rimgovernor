package main

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func newTestServe(t *testing.T, h http.HandlerFunc) *ServeClient {
	t.Helper()
	srv := httptest.NewServer(h)
	t.Cleanup(srv.Close)
	return NewServeClient(func() string { return srv.URL })
}

func TestServeClientDecodesEachEndpoint(t *testing.T) {
	c := newTestServe(t, func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/api/state":
			w.Write([]byte(`{"sessionId":"s","connected":true,"mode":"automate","status":{"label":"ok"},"game":{"tick":4200,"paused":true,"stale":false},"identity":{"colonyId":"c","mapId":1,"loadToken":"l"},"activePlanId":"p1"}`))
		case "/api/spectator/now":
			w.Write([]byte(`{"tick":4200,"concerns":[{"concern":"Feed","method":"Hunt","blocked":"no_hunter"}],"pacing":{"reason":"held","detail":"d"}}`))
		case "/api/routines":
			w.Write([]byte(`{"reviewsEnabled":true,"sections":[{"x":1}],"development":{"tick":4200,"capacity":3,"heldWorkers":1,"limiting":"capacity","committed":["Feed"],"rows":[{"concern":"Feed","score":1.5,"selected":true,"reason":"selected"},{"concern":"Roof","score":0.5,"reason":"capacity","bottleneck":"Construction"}]},"progress":[{"concern":"Feed","blocked":"no_hunter"}]}`))
		default:
			w.WriteHeader(404)
		}
	})
	ctx := context.Background()
	if st := c.State(ctx); st.Value == nil || *st.Value.Game.Tick != 4200 || !*st.Value.Game.Paused || !st.Value.Connected || string(*st.Value.ActivePlanID) != "p1" || st.Value.Identity.ColonyID != "c" || st.Stale {
		t.Fatalf("state %+v", st)
	}
	if n := c.Now(ctx); n.Value == nil || n.Value.Concerns[0].Concern != "Feed" || n.Value.Concerns[0].Blocked != "no_hunter" {
		t.Fatalf("now %+v", n)
	}
	r := c.Routines(ctx)
	if r.Value == nil || r.Value.Development == nil || len(r.Value.Development.Rows) != 2 || r.Value.Development.Rows[1].Reason != "capacity" || r.Value.Development.Rows[1].Bottleneck != "Construction" || r.Value.Progress[0].Blocked != "no_hunter" {
		t.Fatalf("routines %+v", r)
	}
}

func TestServeClientKeepsLastGoodAsStale(t *testing.T) {
	var fail atomic.Bool
	c := newTestServe(t, func(w http.ResponseWriter, r *http.Request) {
		if fail.Load() {
			w.WriteHeader(503)
			w.Write([]byte(`{"code":"unavailable","detail":"Controller data is unavailable"}`))
			return
		}
		w.Write([]byte(`{"tick":7}`))
	})
	ctx := context.Background()
	if n := c.Now(ctx); n.Value == nil || *n.Value.Tick != 7 || n.Stale || n.Error != "" {
		t.Fatalf("first %+v", n)
	}
	fail.Store(true)
	n := c.Now(ctx)
	if n.Value == nil || *n.Value.Tick != 7 || !n.Stale || !strings.Contains(n.Error, "503") || !strings.Contains(n.Error, "unavailable") || n.NotServed {
		t.Fatalf("after failure %+v", n)
	}
	fail.Store(false)
	if n := c.Now(ctx); n.Stale || n.Error != "" || *n.Value.Tick != 7 {
		t.Fatalf("recovered %+v", n)
	}
}

func TestServeClientFailureBeforeAnyReadingIsNotStale(t *testing.T) {
	c := newTestServe(t, func(w http.ResponseWriter, r *http.Request) { w.Write([]byte(`not json`)) })
	n := c.Now(context.Background())
	if n.Value != nil || n.Stale || n.Error == "" {
		t.Fatalf("%+v", n)
	}
}

func TestServeClientNotServedIsNotAnError(t *testing.T) {
	var gone atomic.Bool
	c := newTestServe(t, func(w http.ResponseWriter, r *http.Request) {
		if gone.Load() {
			w.WriteHeader(404)
			w.Write([]byte(`{"code":"not_found","detail":"Routine diagnostics are not enabled"}`))
			return
		}
		w.Write([]byte(`{"reviewsEnabled":true}`))
	})
	ctx := context.Background()
	if r := c.Routines(ctx); r.Value == nil {
		t.Fatalf("%+v", r)
	}
	gone.Store(true)
	r := c.Routines(ctx)
	if !r.NotServed || r.Value != nil || r.Stale || r.Error != "" {
		t.Fatalf("404 %+v", r)
	}
}

func TestServeClientTimesOut(t *testing.T) {
	release := make(chan struct{})
	c := newTestServe(t, func(w http.ResponseWriter, r *http.Request) {
		select {
		case <-release:
		case <-r.Context().Done():
		}
	})
	defer close(release)
	c.http.Timeout = 50 * time.Millisecond
	start := time.Now()
	n := c.Now(context.Background())
	if n.Error == "" || n.Value != nil || time.Since(start) > 5*time.Second {
		t.Fatalf("%+v after %v", n, time.Since(start))
	}
}
