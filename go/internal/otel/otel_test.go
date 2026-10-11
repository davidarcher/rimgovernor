package otel

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"go.opentelemetry.io/otel/attribute"
	otellog "go.opentelemetry.io/otel/log"
	"go.opentelemetry.io/otel/metric"
)

// guard is the repo's hang-guard convention: a generous deadline that only
// fails a stuck test, never asserts latency.
const guard = 30 * time.Second

func within(t *testing.T, what string, fn func()) {
	t.Helper()
	done := make(chan struct{})
	go func() { defer close(done); fn() }()
	select {
	case <-done:
	case <-time.After(guard):
		t.Fatalf("%s did not return within %s", what, guard)
	}
}

func shutdown(t *testing.T, tel *Telemetry) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), guard)
	defer cancel()
	if err := tel.Shutdown(ctx); err != nil {
		t.Fatalf("shutdown: %v", err)
	}
}

func readAll(t *testing.T, path string) []map[string]any {
	t.Helper()
	var out []map[string]any
	if err := ReadLines(path, func(line []byte) error {
		var m map[string]any
		if err := json.Unmarshal(line, &m); err != nil {
			return err
		}
		out = append(out, m)
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	return out
}

func emitSpans(tel *Telemetry, n int) {
	tr := tel.Tracer("test")
	for i := 0; i < n; i++ {
		_, sp := tr.Start(context.Background(), "step", traceAttr(i))
		sp.End()
	}
}

func emitLog(tel *Telemetry, body string, attrs ...attribute.KeyValue) {
	var r otellog.Record
	r.SetBody(attribute.StringValue(body))
	r.SetSeverity(otellog.SeverityInfo)
	r.AddAttributes(attrs...)
	tel.Logger("test").Emit(context.Background(), r)
}

func TestFileSinkWorksWithNoBackend(t *testing.T) {
	t.Setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "")
	dir := t.TempDir()
	tel, err := Start(context.Background(), Config{Dir: dir, Service: "rimgovernor-test", RunID: "run1", MetricInterval: time.Hour})
	if err != nil {
		t.Fatal(err)
	}
	for _, d := range tel.Drops() {
		if d.Sink == "collector" {
			t.Fatalf("collector pipeline exists without an endpoint: %+v", d)
		}
	}
	emitSpans(tel, 3)
	emitLog(tel, "decision", attribute.String("reason", "ok"))
	c, _ := tel.Meter("test").Int64Counter("things")
	c.Add(context.Background(), 7, metric.WithAttributes(attribute.String("kind", "a")))
	shutdown(t, tel)

	spans := readAll(t, tel.FilePath("traces"))
	if len(spans) != 3 {
		t.Fatalf("spans = %d, want 3", len(spans))
	}
	rs := spans[0]["resourceSpans"].([]any)[0].(map[string]any)
	span := rs["scopeSpans"].([]any)[0].(map[string]any)["spans"].([]any)[0].(map[string]any)
	if id := span["traceId"].(string); len(id) != 32 {
		t.Fatalf("traceId %q is not 32 hex", id)
	}
	if span["startTimeUnixNano"].(string) == "" || span["name"] != "step" {
		t.Fatalf("span = %v", span)
	}
	res := fmt.Sprint(rs["resource"])
	if !strings.Contains(res, "rimgovernor-test") || !strings.Contains(res, "run1") {
		t.Fatalf("resource = %s", res)
	}

	logs := readAll(t, tel.FilePath("logs"))
	if len(logs) != 1 || !strings.Contains(fmt.Sprint(logs[0]), "decision") {
		t.Fatalf("logs = %v", logs)
	}

	metrics := readAll(t, tel.FilePath("metrics"))
	var sawThings, sawDropped bool
	for _, m := range metrics {
		s := fmt.Sprint(m)
		sawThings = sawThings || strings.Contains(s, "things")
		sawDropped = sawDropped || strings.Contains(s, DroppedMetric)
	}
	if !sawThings || !sawDropped {
		t.Fatalf("metrics lines = %d, things=%v dropped=%v", len(metrics), sawThings, sawDropped)
	}
}

func TestDroppedItemsAreCountedAndNeverBlock(t *testing.T) {
	release := make(chan struct{})
	var exported atomic.Int64
	stats := &dropStats{signal: "trace", sink: "test", warnEvery: time.Hour}
	q := newQueue(stats, 4, 2, time.Hour, time.Minute, nil, func(ctx context.Context, b []int, _ bool) error {
		select {
		case <-release:
		case <-ctx.Done():
		}
		exported.Add(int64(len(b)))
		return nil
	})
	var kept int
	within(t, "offering to a stalled exporter", func() {
		for i := 0; i < 1000; i++ {
			if q.offer(i) {
				kept++
			}
		}
	})
	dropped := stats.queueFull.Load()
	if dropped == 0 || int(dropped)+kept != 1000 {
		t.Fatalf("kept %d dropped %d, want them to sum to 1000 with drops", kept, dropped)
	}
	// What the stalled exporter holds plus the queue bounds what was kept.
	if kept > 4+2+1 {
		t.Fatalf("kept %d items past a stalled exporter, queue is bounded at 4 plus a batch of 2", kept)
	}
	close(release)
	within(t, "shutdown", func() {
		ctx, cancel := context.WithTimeout(context.Background(), guard)
		defer cancel()
		if err := q.shutdown(ctx); err != nil {
			t.Error(err)
		}
	})
	if int(exported.Load()) != kept {
		t.Fatalf("exported %d, kept %d", exported.Load(), kept)
	}
}

func TestFailedExportIsCountedAsDropped(t *testing.T) {
	stats := &dropStats{signal: "log", sink: "test", warnEvery: time.Hour}
	q := newQueue(stats, 16, 4, time.Hour, time.Minute, nil, func(context.Context, []int, bool) error { return fmt.Errorf("down") })
	for i := 0; i < 8; i++ {
		q.offer(i)
	}
	if err := q.forceFlush(context.Background()); err != nil {
		t.Fatal(err)
	}
	if got := stats.exportFailed.Load(); got != 8 {
		t.Fatalf("export_failed = %d, want 8", got)
	}
}

func TestDropWarningIsRateLimited(t *testing.T) {
	var warns atomic.Int64
	stats := &dropStats{signal: "trace", sink: "file", warnEvery: time.Hour, warn: func(string, ...any) { warns.Add(1) }}
	for i := 0; i < 1000; i++ {
		stats.add(ReasonQueueFull, 1)
	}
	if warns.Load() != 1 {
		t.Fatalf("warnings = %d, want exactly 1 within the interval", warns.Load())
	}
	if stats.queueFull.Load() != 1000 {
		t.Fatalf("count = %d", stats.queueFull.Load())
	}
}

func TestCollectorOutageDoesNotAffectCallerOrFileSink(t *testing.T) {
	// A port that was open and is now closed: connections are refused.
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	addr := l.Addr().String()
	l.Close()
	for _, protocol := range []string{"grpc", "http/protobuf"} {
		t.Run(protocol, func(t *testing.T) {
			dir := t.TempDir()
			tel, err := Start(context.Background(), Config{Dir: dir, CollectorEndpoint: "http://" + addr, CollectorProtocol: protocol,
				ExportTimeout: 300 * time.Millisecond, MetricInterval: time.Hour, QueueSize: 4096, Warn: func(string, ...any) {}})
			if err != nil {
				t.Fatalf("an unreachable collector must not fail Start: %v", err)
			}
			within(t, "emitting with the collector down", func() { emitSpans(tel, 1000) })
			ctx, cancel := context.WithTimeout(context.Background(), guard)
			defer cancel()
			if err := tel.ForceFlush(ctx); err != nil {
				t.Fatalf("flush: %v", err)
			}
			if got := len(readAll(t, tel.FilePath("traces"))); got != 1000 {
				t.Fatalf("file sink has %d spans, want 1000", got)
			}
			if failed := sumDrops(tel, "collector", ReasonExportFailed); failed == 0 {
				t.Fatal("collector failures were not counted")
			}
			// A bounded shutdown abandons the collector flush; the error is expected.
			sctx, scancel := context.WithTimeout(context.Background(), time.Second)
			defer scancel()
			within(t, "shutdown with the collector down", func() { _ = tel.Shutdown(sctx) })
		})
	}
}

func TestStalledCollectorDoesNotBlockCaller(t *testing.T) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	var held []net.Conn
	var mu sync.Mutex
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			mu.Lock()
			held = append(held, c) // accept and never answer
			mu.Unlock()
		}
	}()
	defer func() {
		mu.Lock()
		for _, c := range held {
			c.Close()
		}
		mu.Unlock()
	}()

	dir := t.TempDir()
	tel, err := Start(context.Background(), Config{Dir: dir, CollectorEndpoint: "http://" + l.Addr().String(), CollectorProtocol: "http/protobuf",
		QueueSize: 64, BatchSize: 16, CollectorFlushInterval: 10 * time.Millisecond, ExportTimeout: time.Minute,
		MetricInterval: time.Hour, Warn: func(string, ...any) {}})
	if err != nil {
		t.Fatal(err)
	}
	within(t, "emitting 20000 spans and logs against a stalled collector", func() {
		for i := 0; i < 20000; i++ {
			emitSpans(tel, 1)
			emitLog(tel, "x")
		}
	})
	if got := sumDrops(tel, "collector", ReasonQueueFull); got == 0 {
		t.Fatal("expected the stalled collector's queue to overflow and count drops")
	}
	// Shutdown must abandon the stuck export when its context ends.
	ctx, cancel := context.WithTimeout(context.Background(), 500*time.Millisecond)
	defer cancel()
	within(t, "shutdown against a stalled collector", func() { _ = tel.Shutdown(ctx) })
}

func TestCollectorReceivesOverHTTPWhenEnabledByEnv(t *testing.T) {
	var mu sync.Mutex
	paths := map[string]int{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		paths[r.URL.Path]++
		mu.Unlock()
		w.Header().Set("Content-Type", "application/x-protobuf")
		w.WriteHeader(http.StatusOK)
	}))
	defer srv.Close()
	t.Setenv("OTEL_EXPORTER_OTLP_ENDPOINT", srv.URL)
	t.Setenv("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
	tel, err := Start(context.Background(), Config{Dir: t.TempDir(), MetricInterval: time.Hour})
	if err != nil {
		t.Fatal(err)
	}
	emitSpans(tel, 2)
	emitLog(tel, "hello")
	ctx, cancel := context.WithTimeout(context.Background(), guard)
	defer cancel()
	if err := tel.ForceFlush(ctx); err != nil {
		t.Fatal(err)
	}
	shutdown(t, tel)
	mu.Lock()
	defer mu.Unlock()
	for _, p := range []string{"/v1/traces", "/v1/logs", "/v1/metrics"} {
		if paths[p] == 0 {
			t.Errorf("collector never received %s; got %v", p, paths)
		}
	}
}

func TestExplicitEndpointWithPathReachesSignalPaths(t *testing.T) {
	var mu sync.Mutex
	paths := map[string]int{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		paths[r.URL.Path]++
		mu.Unlock()
	}))
	defer srv.Close()
	tel, err := Start(context.Background(), Config{Dir: t.TempDir(), CollectorEndpoint: srv.URL + "/otlp", CollectorProtocol: "http", MetricInterval: time.Hour})
	if err != nil {
		t.Fatal(err)
	}
	emitSpans(tel, 1)
	ctx, cancel := context.WithTimeout(context.Background(), guard)
	defer cancel()
	_ = tel.ForceFlush(ctx)
	shutdown(t, tel)
	mu.Lock()
	defer mu.Unlock()
	if paths["/otlp/v1/traces"] == 0 {
		t.Fatalf("paths = %v", paths)
	}
}

func TestBadCollectorConfigIsAnError(t *testing.T) {
	if _, err := Start(context.Background(), Config{CollectorEndpoint: "http://x:1", CollectorProtocol: "carrier-pigeon"}); err == nil {
		t.Fatal("unknown protocol accepted")
	}
}

func TestDurableLogIsFlushedWithoutWaitingForTheInterval(t *testing.T) {
	tel, err := Start(context.Background(), Config{Dir: t.TempDir(), FileFlushInterval: time.Hour, MetricInterval: time.Hour})
	if err != nil {
		t.Fatal(err)
	}
	defer shutdown(t, tel)
	emitLog(tel, "lazy")
	emitLog(tel, "critical", DurableAttr)
	deadline := time.Now().Add(guard)
	for len(readAll(t, tel.FilePath("logs"))) < 2 {
		if time.Now().After(deadline) {
			t.Fatal("durable record was not flushed promptly")
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func TestRotationAndRetentionAreBounded(t *testing.T) {
	path := filepath.Join(t.TempDir(), "ring.jsonl")
	f, err := openRotatingFile(path, 1000, 3)
	if err != nil {
		t.Fatal(err)
	}
	const rows = 300
	for i := 0; i < rows; i++ {
		line := fmt.Sprintf(`{"n":%d,"pad":"%s"}`+"\n", i, strings.Repeat("x", 80))
		if err := f.write([]byte(line), false); err != nil {
			t.Fatal(err)
		}
	}
	if err := f.close(); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(path + ".3"); err == nil {
		t.Fatal("a fourth file exists; retention is 3 files in total")
	}
	segs := Segments(path)
	if len(segs) != 3 {
		t.Fatalf("segments = %v", segs)
	}
	var total int64
	for _, s := range segs {
		info, _ := os.Stat(s)
		total += info.Size()
		if info.Size() > 1000+120 {
			t.Fatalf("%s is %d bytes, bound is 1000 plus one row", s, info.Size())
		}
	}
	if total > 3*(1000+120) {
		t.Fatalf("ring holds %d bytes", total)
	}
	// Newest rows survive, oldest are gone, order is oldest to newest, no gaps.
	var ns []int
	_ = ReadLines(path, func(line []byte) error {
		var m struct{ N int }
		_ = json.Unmarshal(line, &m)
		ns = append(ns, m.N)
		return nil
	})
	if len(ns) == 0 || ns[len(ns)-1] != rows-1 || ns[0] == 0 {
		t.Fatalf("retained rows %v..%v", ns[0], ns[len(ns)-1])
	}
	for i := 1; i < len(ns); i++ {
		if ns[i] != ns[i-1]+1 {
			t.Fatalf("gap or reorder at %d: %d then %d", i, ns[i-1], ns[i])
		}
	}
}

func TestReopenAfterTornWriteKeepsEarlierRowsAndStartsOnANewLine(t *testing.T) {
	path := filepath.Join(t.TempDir(), "ring.jsonl")
	if err := os.WriteFile(path, []byte(`{"n":1}`+"\n"+`{"n":2}`+"\n"+`{"n":3,"torn`), 0o644); err != nil {
		t.Fatal(err)
	}
	f, err := openRotatingFile(path, 1<<20, 2)
	if err != nil {
		t.Fatal(err)
	}
	if err := f.write([]byte(`{"n":4}`+"\n"), false); err != nil {
		t.Fatal(err)
	}
	f.close()
	var got []string
	_ = ReadLines(path, func(line []byte) error { got = append(got, string(line)); return nil })
	if strings.Join(got, ",") != `{"n":1},{"n":2},{"n":4}` {
		t.Fatalf("got %v", got)
	}
}

// TestRecordsSurviveAnUncleanKill kills a child process after a flush, with no
// Shutdown, then reads and extends what it left.
func TestRecordsSurviveAnUncleanKill(t *testing.T) {
	if dir := os.Getenv("OTEL_TEST_CHILD_DIR"); dir != "" {
		tel, err := Start(context.Background(), Config{Dir: dir, MetricInterval: time.Hour})
		if err != nil {
			os.Exit(4)
		}
		emitSpans(tel, 100)
		emitLog(tel, "before the crash", DurableAttr)
		if err := tel.ForceFlush(context.Background()); err != nil {
			os.Exit(5)
		}
		// The process dies mid-record: a torn trailing line, no Shutdown, no Close.
		f, _ := os.OpenFile(filepath.Join(dir, TracesFile), os.O_APPEND|os.O_WRONLY, 0o644)
		f.WriteString(`{"resourceSpans":[{"resource":{"attr`)
		os.Exit(3)
	}
	dir := t.TempDir()
	cmd := exec.Command(os.Args[0], "-test.run=^TestRecordsSurviveAnUncleanKill$")
	cmd.Env = append(os.Environ(), "OTEL_TEST_CHILD_DIR="+dir)
	err := cmd.Run()
	if ee, ok := err.(*exec.ExitError); !ok || ee.ExitCode() != 3 {
		t.Fatalf("child = %v, want exit 3", err)
	}
	traces := filepath.Join(dir, TracesFile)
	if got := len(readAll(t, traces)); got != 100 {
		t.Fatalf("after the kill: %d spans readable, want 100 (the torn line skipped)", got)
	}
	if got := len(readAll(t, filepath.Join(dir, LogsFile))); got != 1 {
		t.Fatalf("logs readable = %d, want 1", got)
	}
	tel, err := Start(context.Background(), Config{Dir: dir, MetricInterval: time.Hour})
	if err != nil {
		t.Fatal(err)
	}
	emitSpans(tel, 5)
	shutdown(t, tel)
	if got := len(readAll(t, traces)); got != 105 {
		t.Fatalf("after restart: %d spans, want 105", got)
	}
}

func sumDrops(tel *Telemetry, sink, reason string) uint64 {
	var n uint64
	for _, d := range tel.Drops() {
		if d.Sink == sink && d.Reason == reason {
			n += d.Count
		}
	}
	return n
}

func BenchmarkFileSinkSpans(b *testing.B) {
	tel, err := Start(context.Background(), Config{Dir: b.TempDir(), MetricInterval: time.Hour, QueueSize: 1 << 20})
	if err != nil {
		b.Fatal(err)
	}
	tr := tel.Tracer("bench")
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		_, sp := tr.Start(context.Background(), "step", traceAttr(i))
		sp.End()
	}
	_ = tel.ForceFlush(context.Background())
	b.StopTimer()
	b.ReportMetric(float64(sumDrops(tel, "file", ReasonQueueFull)), "dropped")
	_ = tel.Shutdown(context.Background())
}
