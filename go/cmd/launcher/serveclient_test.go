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

func TestServeClientDecodesLedger(t *testing.T) {
	c := newTestServe(t, func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/ledger" {
			w.WriteHeader(404)
			return
		}
		w.Write([]byte(`{"status":"reconciled","tick":9,"orders":[{"recipe":"Make_Vest","state":"placed","benches":["B1"]}]}`))
	})
	if l := c.Ledger(context.Background()); l.Value == nil || l.Value.Tick != 9 || l.Value.Orders[0].Benches[0] != "B1" || l.Stale {
		t.Fatalf("ledger %+v", l)
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
	if n := c.Ledger(ctx); n.Value == nil || n.Value.Tick != 7 || n.Stale || n.Error != "" {
		t.Fatalf("first %+v", n)
	}
	fail.Store(true)
	n := c.Ledger(ctx)
	if n.Value == nil || n.Value.Tick != 7 || !n.Stale || !strings.Contains(n.Error, "503") || !strings.Contains(n.Error, "unavailable") || n.NotServed {
		t.Fatalf("after failure %+v", n)
	}
	fail.Store(false)
	if n := c.Ledger(ctx); n.Stale || n.Error != "" || n.Value.Tick != 7 {
		t.Fatalf("recovered %+v", n)
	}
}

func TestServeClientFailureBeforeAnyReadingIsNotStale(t *testing.T) {
	c := newTestServe(t, func(w http.ResponseWriter, r *http.Request) { w.Write([]byte(`not json`)) })
	n := c.Ledger(context.Background())
	if n.Value != nil || n.Stale || n.Error == "" {
		t.Fatalf("%+v", n)
	}
}

func TestServeClientNotServedIsNotAnError(t *testing.T) {
	var gone atomic.Bool
	c := newTestServe(t, func(w http.ResponseWriter, r *http.Request) {
		if gone.Load() {
			w.WriteHeader(404)
			w.Write([]byte(`{"code":"not_found","detail":"Ledger diagnostics are not enabled"}`))
			return
		}
		w.Write([]byte(`{"tick":1}`))
	})
	ctx := context.Background()
	if r := c.Ledger(ctx); r.Value == nil {
		t.Fatalf("%+v", r)
	}
	gone.Store(true)
	r := c.Ledger(ctx)
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
	n := c.Ledger(context.Background())
	if n.Error == "" || n.Value != nil || time.Since(start) > 5*time.Second {
		t.Fatalf("%+v after %v", n, time.Since(start))
	}
}
