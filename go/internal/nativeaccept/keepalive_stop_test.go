package nativeaccept

import (
	"context"
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// Stop waits for the keep-alive loop; the loop's API calls take the
// process lock for the request counter. Stop must not hold the lock while
// it waits, or a call in flight when Stop runs deadlocks the harness (seen
// on shelter/hut run 0).
func TestStopWaitsForKeepAliveWithoutHoldingLock(t *testing.T) {
	inFlight := make(chan struct{}, 1)
	release := make(chan struct{})
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		select {
		case inFlight <- struct{}{}:
		default:
		}
		<-release
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"mode":"automate"}`))
	}))
	defer srv.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	p := &ServiceProcess{URL: srv.URL, client: srv.Client(), ctx: ctx, dir: t.TempDir(), entry: map[string]any{}, exited: true}
	p.keep = &AuthorityKeepAlive{Service: p, Prefix: "test"}
	p.stopKeep = p.keep.Start(ctx)
	// The loop's first call comes after its 2s tick.
	<-inFlight
	stopped := make(chan map[string]any, 1)
	go func() { stopped <- p.Stop() }()
	time.Sleep(50 * time.Millisecond)
	close(release)
	select {
	case keep := <-stopped:
		if keep == nil {
			t.Fatal("Stop returned no keep-alive counters")
		}
	case <-time.After(5 * time.Second):
		t.Fatal("Stop did not return while an API call was in flight")
	}
}

// Hold(true) returns only once the pass in flight has finished, and no pass
// requests anything after it. A checkpoint pauses and resumes the clock while
// held; a pass that straddled that would be charged the checkpoint's own
// generations (sustained/colony-stable cs10: a resume costing three).
func TestHoldWaitsForThePassInFlight(t *testing.T) {
	inFlight := make(chan struct{}, 1)
	release := make(chan struct{})
	var unblock sync.Once
	releaseAll := func() { unblock.Do(func() { close(release) }) }
	var requests atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		select {
		case inFlight <- struct{}{}:
		default:
		}
		<-release
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"mode":"automate"}`))
	}))
	defer srv.Close()
	defer releaseAll()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	p := &ServiceProcess{URL: srv.URL, client: srv.Client(), ctx: ctx, dir: t.TempDir(), entry: map[string]any{}, exited: true}
	p.keep = &AuthorityKeepAlive{Service: p, Prefix: "test"}
	p.stopKeep = p.keep.Start(ctx)
	<-inFlight
	held := make(chan struct{})
	go func() { p.keep.Hold(true); close(held) }()
	select {
	case <-held:
		t.Fatal("Hold returned while a pass was mid-request")
	case <-time.After(100 * time.Millisecond):
	}
	releaseAll()
	select {
	case <-held:
	case <-time.After(5 * time.Second):
		t.Fatal("Hold did not return after the pass finished")
	}
	seen := requests.Load()
	time.Sleep(KeepAliveInterval + 200*time.Millisecond)
	if got := requests.Load(); got != seen {
		t.Fatalf("a held keep-alive made %d more request(s)", got-seen)
	}
}
