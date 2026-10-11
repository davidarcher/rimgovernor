package httpapi

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

type accessRow struct {
	kind    string
	context map[string]any
	payload map[string]any
}

// accessSink reads back the http_access rows the handler emitted.
type accessSink struct {
	captured *telemetrytest.Rows
	rows     []accessRow
}

func newAccessSink(t *testing.T) *accessSink {
	return &accessSink{captured: telemetrytest.Install(t)}
}

func serveAccess(t *testing.T, sink *accessSink, h http.HandlerFunc, method, path string) {
	t.Helper()
	accessHandler(h, true).ServeHTTP(httptest.NewRecorder(), httptest.NewRequest(method, path, nil))
	sink.rows = nil
	for _, row := range sink.captured.Of(AccessKind) {
		sink.rows = append(sink.rows, accessRow{row.Kind, row.Context, row.Payload})
	}
}

func TestAccessStatusCapture(t *testing.T) {
	sink := newAccessSink(t)
	serveAccess(t, sink, func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(404) }, "POST", "/api/x")
	serveAccess(t, sink, func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("hello")) }, "POST", "/api/y")
	serveAccess(t, sink, func(http.ResponseWriter, *http.Request) {}, "HEAD", "/api/state")
	want := []struct {
		status int64
		bytes  int64
	}{{404, 0}, {200, 5}, {200, 0}}
	if len(sink.rows) != len(want) {
		t.Fatalf("rows = %d, want %d", len(sink.rows), len(want))
	}
	for i, w := range want {
		row := sink.rows[i]
		if row.kind != AccessKind || row.payload["status"] != w.status || row.payload["bytes"] != w.bytes {
			t.Fatalf("row %d = %+v, want status %d bytes %d", i, row, w.status, w.bytes)
		}
		if row.context["component"] != "httpapi" {
			t.Fatalf("row %d context = %v", i, row.context)
		}
	}
	if sink.rows[2].payload["method"] != "HEAD" {
		t.Fatal("HEAD is a non-GET and is kept")
	}
}

func TestAccessDenylist(t *testing.T) {
	ok := func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("{}")) }
	fail := func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(500) }
	slow := func(w http.ResponseWriter, _ *http.Request) { time.Sleep(slowAccess + 20*time.Millisecond) }
	cases := []struct {
		name         string
		h            http.HandlerFunc
		method, path string
		kept         bool
	}{
		{"fast state poll dropped", ok, "GET", "/api/state", false},
		{"presentation dropped", ok, "GET", "/api/presentation/notifications", false},
		{"poll error kept", fail, "GET", "/api/routines", true},
		{"slow poll kept", slow, "GET", "/api/state", true},
		{"post to poll path kept", ok, "POST", "/api/state", true},
		{"unlisted GET kept", ok, "GET", "/api/plan", true},
		{"pprof kept", ok, "GET", "/debug/pprof/heap", true},
	}
	for _, c := range cases {
		sink := newAccessSink(t)
		serveAccess(t, sink, c.h, c.method, c.path)
		if got := len(sink.rows) == 1; got != c.kept {
			t.Errorf("%s: kept = %v, want %v", c.name, got, c.kept)
		}
	}
	sink := newAccessSink(t)
	serveAccess(t, sink, ok, "GET", "/debug/pprof/profile")
	if sink.rows[0].payload["long_lived"] != true {
		t.Fatal("pprof row must carry long_lived")
	}
}

func TestAccessNilRecorderIsNoOp(t *testing.T) {
	next := http.HandlerFunc(func(http.ResponseWriter, *http.Request) {})
	if h := accessHandler(next, false); h == nil {
		t.Fatal("nil recorder must still serve")
	}
	rec := httptest.NewRecorder()
	accessHandler(next, false).ServeHTTP(rec, httptest.NewRequest("GET", "/api/state", nil))
	if rec.Code != 200 {
		t.Fatalf("code = %d", rec.Code)
	}
}

func TestAccessWrapperUnwrapsForResponseController(t *testing.T) {
	var deadlineErr, flushErr error
	h := func(w http.ResponseWriter, _ *http.Request) {
		rc := http.NewResponseController(w)
		deadlineErr = rc.SetWriteDeadline(time.Now().Add(time.Minute))
		flushErr = rc.Flush()
	}
	srv := httptest.NewServer(accessHandler(http.HandlerFunc(h), true))
	defer srv.Close()
	resp, err := http.Get(srv.URL + "/debug/pprof/profile")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if deadlineErr != nil || flushErr != nil {
		t.Fatalf("SetWriteDeadline = %v, Flush = %v", deadlineErr, flushErr)
	}
}
