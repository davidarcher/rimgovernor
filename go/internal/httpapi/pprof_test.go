package httpapi

import (
	"context"
	"io"
	"net"
	"net/http"
	"testing"
	"time"
)

// A CPU profile started for longer than the server's read/write timeouts
// survives them and ends, with its data, on DELETE.
func TestPprofProfileOutlivesTimeoutsAndStopsOnDelete(t *testing.T) {
	s, err := New(Config{Pprof: true, ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- s.Serve(ctx, listener) }()
	base := "http://" + listener.Addr().String()

	type result struct {
		status int
		body   []byte
		err    error
	}
	got := make(chan result, 1)
	go func() {
		resp, err := http.Get(base + "/debug/pprof/profile?seconds=60")
		if err != nil {
			got <- result{err: err}
			return
		}
		defer resp.Body.Close()
		body, err := io.ReadAll(resp.Body)
		got <- result{resp.StatusCode, body, err}
	}()
	// Past the server's WriteTimeout the capture is still running: the
	// write deadline it lifted would otherwise have dropped the response.
	time.Sleep(1500 * time.Millisecond)
	select {
	case r := <-got:
		t.Fatalf("profile ended before DELETE: status %d err %v", r.status, r.err)
	default:
	}
	req, _ := http.NewRequest(http.MethodDelete, base+"/debug/pprof/profile", nil)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("DELETE did not find the profile in flight: %d", resp.StatusCode)
	}
	select {
	case r := <-got:
		if r.err != nil || r.status != 200 || len(r.body) < 2 || r.body[0] != 0x1f || r.body[1] != 0x8b {
			t.Fatalf("profile status %d err %v body %d bytes", r.status, r.err, len(r.body))
		}
	case <-time.After(10 * time.Second):
		t.Fatal("profile did not end on DELETE")
	}
	resp, err = http.Get(base + "/debug/pprof/heap")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("heap status %d", resp.StatusCode)
	}
	cancel()
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

func TestPprofRoutesAnswer404WhenOff(t *testing.T) {
	s, err := New(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = s.Serve(ctx, listener) }()
	resp, err := http.Get("http://" + listener.Addr().String() + "/debug/pprof/heap")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != 404 {
		t.Fatalf("status %d", resp.StatusCode)
	}
}
