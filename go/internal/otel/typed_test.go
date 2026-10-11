package otel

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"

	"go.opentelemetry.io/otel/attribute"
	otellog "go.opentelemetry.io/otel/log"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"
)

func TestTypedReadersDecodeEverySignal(t *testing.T) {
	t.Setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "")
	dir := t.TempDir()
	tel, err := Start(context.Background(), Config{Dir: dir, Service: "rimgovernor-test", RunID: "run7", MetricInterval: time.Hour})
	if err != nil {
		t.Fatal(err)
	}
	ctx, parent := tel.Tracer("t").Start(context.Background(), "parent")
	_, child := tel.Tracer("t").Start(ctx, "child", trace.WithAttributes(attribute.Int("n", 5), attribute.Float64("ms", 1.5), attribute.Bool("ok", true)))
	child.AddEvent("note", trace.WithAttributes(attribute.String("k", "v")))
	child.End()
	parent.End()

	var rec otellog.Record
	rec.SetTimestamp(time.Unix(100, 5))
	rec.SetEventName("dispatch")
	rec.SetSeverity(otellog.SeverityWarn)
	rec.SetBody(attribute.StringValue("body"))
	rec.AddAttributes(
		attribute.String("rimgovernor.kind", "dispatch"),
		attribute.Int64("rimgovernor.tick", 42),
		attribute.KeyValue{Key: "rimgovernor.attr.list", Value: attribute.SliceValue(attribute.StringValue("a"), attribute.Int64Value(2))},
		attribute.KeyValue{Key: "rimgovernor.attr.map", Value: attribute.MapValue(attribute.Bool("x", true), attribute.Float64("y", 0.25))},
	)
	tel.Logger("t").Emit(context.Background(), rec)

	counter, _ := tel.Meter("t").Int64Counter("things")
	counter.Add(context.Background(), 7, metric.WithAttributes(attribute.String("kind", "a")))
	hist, _ := tel.Meter("t").Float64Histogram("lat", metric.WithUnit("ms"))
	hist.Record(context.Background(), 3)
	hist.Record(context.Background(), 30)
	shutdown(t, tel)

	var spans []Span
	if err := ReadSpans(tel.FilePath("traces"), func(s Span) error { spans = append(spans, s); return nil }); err != nil {
		t.Fatal(err)
	}
	if len(spans) != 2 || spans[0].Name != "child" || spans[1].Name != "parent" {
		t.Fatalf("spans = %+v", spans)
	}
	child2 := spans[0]
	if child2.ParentSpanID != spans[1].SpanID || child2.TraceID != spans[1].TraceID || spans[1].ParentSpanID != "" {
		t.Fatalf("parentage: %+v", spans)
	}
	if n, _ := child2.Attrs.Int("n"); n != 5 || !child2.Attrs.Bool("ok") {
		t.Fatalf("attrs = %v", child2.Attrs)
	}
	if ms, _ := child2.Attrs.Float("ms"); ms != 1.5 {
		t.Fatalf("ms = %v", ms)
	}
	if len(child2.Events) != 1 || child2.Events[0].Attrs.Str("k") != "v" || child2.Resource.Instance != "run7" || child2.Resource.Service != "rimgovernor-test" {
		t.Fatalf("event/resource = %+v", child2)
	}
	if child2.Duration() < 0 || child2.Pos.Line != 1 {
		t.Fatalf("pos/duration = %+v", child2)
	}

	var logs []Log
	if err := ReadLogs(tel.FilePath("logs"), func(l Log) error { logs = append(logs, l); return nil }); err != nil {
		t.Fatal(err)
	}
	if len(logs) != 1 {
		t.Fatalf("logs = %+v", logs)
	}
	l := logs[0]
	if l.EventName != "dispatch" || l.Severity != int(otellog.SeverityWarn) || l.Body != "body" || !l.Time.Equal(time.Unix(100, 5)) {
		t.Fatalf("log = %+v", l)
	}
	if tick, _ := l.Attrs.Int("rimgovernor.tick"); tick != 42 {
		t.Fatalf("tick = %v", l.Attrs["rimgovernor.tick"])
	}
	list, _ := l.Attrs["rimgovernor.attr.list"].([]any)
	if len(list) != 2 || list[0] != "a" || list[1] != int64(2) {
		t.Fatalf("list = %#v", l.Attrs["rimgovernor.attr.list"])
	}
	m, _ := l.Attrs["rimgovernor.attr.map"].(map[string]any)
	if m["x"] != true || m["y"] != 0.25 {
		t.Fatalf("map = %#v", l.Attrs["rimgovernor.attr.map"])
	}

	var sawSum, sawHist bool
	if err := ReadMetrics(tel.FilePath("metrics"), func(m Metric) error {
		switch m.Name {
		case "things":
			sawSum = m.Kind == Sum && m.Monotonic && len(m.Points) == 1 && m.Points[0].Value == 7 && m.Points[0].Attrs.Str("kind") == "a"
		case "lat":
			p := m.Points[0]
			sawHist = m.Kind == Histogram && p.Count == 2 && p.Sum == 33 && len(p.Bounds) > 0 && len(p.Buckets) == len(p.Bounds)+1
		}
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if !sawSum || !sawHist {
		t.Fatalf("sum=%v histogram=%v", sawSum, sawHist)
	}
}

func TestScanNumbersLinesAcrossSegmentsAndSkipsTornLines(t *testing.T) {
	dir := t.TempDir()
	ring := filepath.Join(dir, "logs.jsonl")
	if err := os.WriteFile(ring+".1", []byte("{\"a\":1}\nnot json\n{\"a\":2}\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(ring, []byte("{\"a\":3}\n{\"a\":4"), 0o644); err != nil {
		t.Fatal(err)
	}
	var got []string
	if err := Scan(ring, func(pos Position, line []byte) error {
		got = append(got, pos.String()+"="+string(line))
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	want := []string{`logs.jsonl.1#1={"a":1}`, `logs.jsonl.1#3={"a":2}`, `logs.jsonl#1={"a":3}`}
	if len(got) != len(want) {
		t.Fatalf("got %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("got %v, want %v", got, want)
		}
	}
}
