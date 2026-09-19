package httpapi

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func telemetryServer(t *testing.T, ring string) *httptest.Server {
	t.Helper()
	snapshot := Snapshot{Connected: true, Tick: domain.Known(domain.Tick(4200)), Generation: domain.Known(domain.GenerationSnapshot{Colony: "colony", Load: "load", Plan: "plan", Revision: 3, Native: 7})}
	api, err := New(Config{FlightRecorder: ring, ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, snapshotFunc(func(context.Context) (Snapshot, error) { return snapshot, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(api.Handler())
	t.Cleanup(server.Close)
	return server
}

func telemetryGet(t *testing.T, server *httptest.Server, path string, into any) int {
	t.Helper()
	response, err := http.Get(server.URL + path)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatal(err)
	}
	if into != nil && response.StatusCode == 200 {
		if err := json.Unmarshal(body, into); err != nil {
			t.Fatalf("%s: %v: %s", path, err, body)
		}
	}
	return response.StatusCode
}

// The events route pages the ring by sequence and kind; the metrics route
// computes the #297 block over the current launch beside tick and authority.
func TestTelemetryRoutesReadTheRing(t *testing.T) {
	ring := filepath.Join(t.TempDir(), "flight", "flight.jsonl")
	recorder, err := bridge.NewFlightRecorder(ring, bridge.FlightRunID("run-a"))
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 3; i++ {
		if _, err := recorder.Event("scheduler_step", map[string]any{"tick": 100 + i}, false, map[string]any{"cause": "timer"}); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := recorder.Event("clock_step", nil, false, map[string]any{"reads": 4, "tools": map[string]any{}, "elapsed_ms": 12.5}); err != nil {
		t.Fatal(err)
	}
	if err := recorder.Close(); err != nil {
		t.Fatal(err)
	}
	server := telemetryServer(t, ring)

	var page TelemetryEvents
	if code := telemetryGet(t, server, "/api/telemetry/events", &page); code != 200 {
		t.Fatalf("events: %d", code)
	}
	// coverage + 3 scheduler_step + clock_step
	if len(page.Events) != 5 || page.LastSequence != 5 || page.NextSince != 5 || page.More {
		t.Fatalf("whole ring: %+v", page)
	}
	if page.Events[0].Kind != "coverage" || page.Events[0].Run != "run-a" || page.Events[1].Context["tick"] != float64(100) {
		t.Fatalf("rows: %+v", page.Events[:2])
	}
	if code := telemetryGet(t, server, "/api/telemetry/events?since=3&kind=scheduler_step,clock_step&limit=1", &page); code != 200 {
		t.Fatalf("paged: %d", code)
	}
	if len(page.Events) != 1 || *page.Events[0].Sequence != 4 || page.Events[0].Kind != "scheduler_step" || page.NextSince != 4 || !page.More || page.LastSequence != 5 {
		t.Fatalf("page after 3: %+v", page)
	}
	if code := telemetryGet(t, server, "/api/telemetry/events?since=4&kind=scheduler_step", &page); code != 200 || len(page.Events) != 0 || page.More || page.NextSince != 4 {
		t.Fatalf("exhausted page: %d %+v", code, page)
	}
	for _, bad := range []string{"?since=-1", "?limit=0", "?limit=1001", "?kind=", "?page=2", "?since=1&since=2"} {
		if code := telemetryGet(t, server, "/api/telemetry/events"+bad, nil); code != 400 {
			t.Errorf("%s: %d", bad, code)
		}
	}

	var metrics TelemetryMetrics
	if code := telemetryGet(t, server, "/api/telemetry/metrics", &metrics); code != 200 {
		t.Fatalf("metrics: %d", code)
	}
	if metrics.Tick == nil || *metrics.Tick != 4200 || metrics.Authority == nil || metrics.Authority.Native != 7 || metrics.LastStepMs != 12.5 || metrics.Run != "run-a" {
		t.Fatalf("metrics head: %+v", metrics)
	}
	for _, name := range telemetryMetricNames {
		if _, ok := metrics.Metrics[name]; !ok {
			t.Errorf("metric %s missing", name)
		}
	}
	if metrics.Metrics["service_launches"] != 1 || metrics.Metrics["evidence_bytes"] <= 0 || metrics.Metrics["wall_ms"] <= 0 {
		t.Fatalf("metrics block: %+v", metrics.Metrics)
	}
	if code := telemetryGet(t, server, "/api/telemetry/metrics?x=1", nil); code != 400 {
		t.Fatalf("metrics query: %d", code)
	}
	request, _ := http.NewRequest(http.MethodPost, server.URL+"/api/telemetry/metrics", strings.NewReader(""))
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != 405 {
		t.Fatalf("POST: %d", response.StatusCode)
	}
}

// A second launch appends to the same ring: the metrics block covers only
// its rows, and a page spanning both launches carries each row's run.
func TestTelemetryMetricsCoverTheCurrentLaunch(t *testing.T) {
	ring := filepath.Join(t.TempDir(), "flight.jsonl")
	first, err := bridge.NewFlightRecorder(ring, bridge.FlightRunID("run-a"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := first.Event("clock_step", nil, false, map[string]any{"reads": 4, "tools": map[string]any{}, "elapsed_ms": 99.0}); err != nil {
		t.Fatal(err)
	}
	if err := first.Close(); err != nil {
		t.Fatal(err)
	}
	second, err := bridge.NewFlightRecorder(ring, bridge.FlightRunID("run-b"))
	if err != nil {
		t.Fatal(err)
	}
	if err := second.Close(); err != nil {
		t.Fatal(err)
	}
	server := telemetryServer(t, ring)
	var page TelemetryEvents
	if code := telemetryGet(t, server, "/api/telemetry/events", &page); code != 200 {
		t.Fatalf("events: %d", code)
	}
	if len(page.Events) != 3 || page.Events[0].Run != "run-a" || page.Events[2].Run != "run-b" || *page.Events[2].Sequence != 3 {
		t.Fatalf("two launches: %+v", page.Events)
	}
	var metrics TelemetryMetrics
	if code := telemetryGet(t, server, "/api/telemetry/metrics", &metrics); code != 200 {
		t.Fatalf("metrics: %d", code)
	}
	if metrics.Run != "run-b" || metrics.LastStepMs != 0 || metrics.Metrics["reads_per_step_mean"] != 0 {
		t.Fatalf("block leaked the earlier launch: %+v", metrics)
	}
}

// Without a recorder the routes are absent, and an empty ring is an empty page.
func TestTelemetryRoutesWithoutARecorderOrRows(t *testing.T) {
	server := telemetryServer(t, "")
	if code := telemetryGet(t, server, "/api/telemetry/events", nil); code != 404 {
		t.Fatalf("no recorder: %d", code)
	}
	server = telemetryServer(t, filepath.Join(t.TempDir(), "missing", "flight.jsonl"))
	var page TelemetryEvents
	if code := telemetryGet(t, server, "/api/telemetry/events", &page); code != 200 || len(page.Events) != 0 || page.More {
		t.Fatalf("missing ring: %d %+v", code, page)
	}
	var metrics TelemetryMetrics
	if code := telemetryGet(t, server, "/api/telemetry/metrics", &metrics); code != 200 || metrics.Tick == nil || metrics.Metrics["service_launches"] != 0 {
		t.Fatalf("missing ring metrics: %d %+v", code, metrics)
	}
}

// A page stops short of the response bound rather than failing it.
func TestTelemetryEventsStayUnderTheResponseBound(t *testing.T) {
	rows := make([]bridge.TimelineRecord, 0, 50)
	for i := 1; i <= 50; i++ {
		rows = append(rows, bridge.TimelineRecord{Kind: "native_response", Sequence: uint64(i), HasSeq: true, Payload: map[string]any{"blob": strings.Repeat("x", 1000)}})
	}
	page := telemetryEvents(rows, 0, nil, 1000, 5000)
	if len(page.Events) != 4 || !page.More || page.NextSince != 4 || page.LastSequence != 50 {
		t.Fatalf("bounded page: %d events more=%v next=%d last=%d", len(page.Events), page.More, page.NextSince, page.LastSequence)
	}
}
