package telemetry_test

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
	"time"

	"go.opentelemetry.io/otel/attribute"
	sdklog "go.opentelemetry.io/otel/sdk/log"

	rgotel "github.com/davidarcher/RimGovernor/go/internal/otel"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

func attrsOf(r sdklog.Record) map[string]attribute.Value {
	out := map[string]attribute.Value{}
	r.WalkAttributes(func(kv attribute.KeyValue) bool {
		out[string(kv.Key)] = kv.Value
		return true
	})
	return out
}

// A decision is an OTEL log record with the fixed searchable attributes, its
// attrs under rimgovernor.attr.*, the active span's ids, and a span event.
func TestDecideEmitsLogRecordAndSpanEvent(t *testing.T) {
	spans := telemetrytest.Spans(t)
	telemetry.ObserveTick(1234)
	rows := telemetrytest.Install(t)
	ctx, span := telemetry.StartSpan(context.Background(), "test", "step")
	telemetry.Decide(ctx, telemetry.Decision{
		Kind: "admission", Component: "clock-scheduler", Level: slog.LevelWarn,
		Verdict: "refused", Reason: "critical_wave_budget", Target: "window", Dur: 1500 * time.Microsecond,
		Attrs: map[string]any{"err": errors.New("held"), "wait": 2 * time.Second, "n": 3, "refused": []string{"a", "b"}, "tally": map[string]int{"x": 1}},
	})
	telemetry.EndSpan(span, nil)

	records := rows.Records()
	if len(records) != 1 {
		t.Fatalf("records %d", len(records))
	}
	r := records[0]
	a := attrsOf(r)
	str := func(k string) string { return a[k].AsString() }
	if r.EventName() != "admission" || r.SeverityText() != "WARN" || r.Body().AsString() != "admission refused critical_wave_budget window" {
		t.Fatalf("record: %q %q %q", r.EventName(), r.SeverityText(), r.Body().AsString())
	}
	if str(telemetry.LogKind) != "admission" || str(telemetry.LogComponent) != "clock-scheduler" || str(telemetry.LogRow) != "decision" ||
		str(telemetry.LogVerdict) != "refused" || str(telemetry.LogReason) != "critical_wave_budget" || str(telemetry.LogTarget) != "window" ||
		a[telemetry.LogDurMs].AsFloat64() != 1.5 || a[telemetry.LogTick].AsInt64() != 1234 {
		t.Fatalf("attributes: %v", a)
	}
	f := telemetry.FieldPrefix
	if str(f+"err") != "held" || a[f+"wait"].AsFloat64() != 2000 || a[f+"n"].AsInt64() != 3 {
		t.Fatalf("attrs: %v", a)
	}
	if got := a[f+"refused"].AsSlice(); len(got) != 2 || got[1].AsString() != "b" {
		t.Fatalf("slice attr: %v", a[f+"refused"])
	}
	if got := a[f+"tally"].AsMap(); len(got) != 1 || got[0].Key != "x" || got[0].Value.AsInt64() != 1 {
		t.Fatalf("map attr: %v", a[f+"tally"])
	}
	if r.TraceID() != span.SpanContext().TraceID() || r.SpanID() != span.SpanContext().SpanID() {
		t.Fatalf("record is not under the span: %v %v", r.TraceID(), r.SpanID())
	}
	ended := spans.GetSpans()
	if len(ended) != 1 || len(ended[0].Events) != 1 || ended[0].Events[0].Name != "admission" {
		t.Fatalf("span events: %+v", ended)
	}
	for _, kv := range ended[0].Events[0].Attributes {
		if string(kv.Key) == telemetry.LogVerdict && kv.Value.AsString() == "refused" {
			return
		}
	}
	t.Fatalf("span event lacks the verdict: %+v", ended[0].Events[0].Attributes)
}

// An event row keeps its message as the body and its fields as attributes.
func TestEventRowMapping(t *testing.T) {
	rows := telemetrytest.Install(t)
	telemetry.ObserveTick(-1)
	slog.Info("step done", telemetry.KindKey, "authority", telemetry.ComponentKey, "worker", "change", "lost", "generation", 7)
	r := rows.Records()[0]
	a := attrsOf(r)
	if r.Body().AsString() != "step done" || a[telemetry.LogRow].AsString() != telemetry.RowEvent || a[telemetry.FieldPrefix+"change"].AsString() != "lost" || a[telemetry.FieldPrefix+"generation"].AsInt64() != 7 {
		t.Fatalf("%q %v", r.Body().AsString(), a)
	}
	if _, has := a[telemetry.LogTick]; has {
		t.Fatalf("no tick observed: %v", a)
	}
	if _, has := a[telemetry.FieldPrefix+"msg"]; has {
		t.Fatalf("msg is the body: %v", a)
	}
}

// An oversized payload keeps the decision keys and is replaced by a preview.
func TestEmitRowCapsPayload(t *testing.T) {
	logs := telemetrytest.Logs(t)
	telemetry.EmitRow(context.Background(), "planner_step", map[string]any{"component": "worker"}, telemetry.Decision{
		Kind: "planner_step", Verdict: "waiting", Reason: "no_work", Target: "Fields", Dur: time.Millisecond,
		Attrs: map[string]any{"big": strings.Repeat("x", telemetry.PayloadCapBytes+10)},
	}.Payload())
	a := attrsOf(logs.All()[0])
	if !a[telemetry.LogTruncated].AsBool() || a[telemetry.LogVerdict].AsString() != "waiting" || a[telemetry.LogDurMs].AsFloat64() != 1 ||
		a[telemetry.LogOriginalBytes].AsInt64() <= telemetry.PayloadCapBytes || len(a[telemetry.LogPreview].AsString()) == 0 || len(a[telemetry.LogSHA256].AsString()) != 64 {
		t.Fatalf("attributes: %v", a)
	}
	if _, has := a[telemetry.FieldPrefix+"big"]; has {
		t.Fatalf("the oversized field must not ride along")
	}
}

// Every kind registered in otel-signals.md round-trips through the OTLP/JSON
// file sink with the same searchable fields, as a decision and as an event.
func TestEveryRegisteredKindRoundTripsThroughTheFileSink(t *testing.T) {
	doc, err := os.ReadFile(filepath.Join("..", "..", "..", "docs", "developers", "contracts", "otel-signals.md"))
	if err != nil {
		t.Fatal(err)
	}
	text := string(doc)
	text = text[strings.Index(text, "\n## Kinds"):strings.Index(text, "\n## Mapping")]
	var kinds []string
	for _, m := range regexp.MustCompile("(?m)^\\| `([a-z_]+)`").FindAllStringSubmatch(text, -1) {
		kinds = append(kinds, m[1])
	}
	if len(kinds) < 20 {
		t.Fatalf("kind registry parse found %d kinds", len(kinds))
	}
	dir := t.TempDir()
	tel, err := rgotel.Start(t.Context(), rgotel.Config{Dir: dir, Service: "test", SetGlobal: true})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = tel.Shutdown(context.Background()) })
	telemetry.ObserveTick(99)
	logger := telemetry.New()
	for i, kind := range kinds {
		if i%2 == 0 {
			telemetry.Decision{Kind: kind, Component: "c", Verdict: "ok", Reason: "r", Target: "t", Dur: time.Millisecond, Attrs: map[string]any{"n": i}}.LogTo(context.Background(), logger)
		} else {
			telemetry.EmitRow(context.Background(), kind, map[string]any{"component": "c", "level": "WARN"}, map[string]any{"msg": "m", "n": i})
		}
	}
	if err := tel.ForceFlush(t.Context()); err != nil {
		t.Fatal(err)
	}
	type kv struct {
		Key   string                     `json:"key"`
		Value map[string]json.RawMessage `json:"value"`
	}
	type record struct {
		EventName  string `json:"eventName"`
		Attributes []kv   `json:"attributes"`
	}
	var envelope struct {
		ResourceLogs []struct {
			ScopeLogs []struct {
				LogRecords []record `json:"logRecords"`
			} `json:"scopeLogs"`
		} `json:"resourceLogs"`
	}
	seen := map[string]map[string]string{}
	err = rgotel.ReadLines(tel.FilePath("logs"), func(line []byte) error {
		envelope.ResourceLogs = nil
		if err := json.Unmarshal(line, &envelope); err != nil {
			return err
		}
		for _, rl := range envelope.ResourceLogs {
			for _, sl := range rl.ScopeLogs {
				for _, r := range sl.LogRecords {
					fields := map[string]string{}
					for _, a := range r.Attributes {
						for _, raw := range a.Value {
							fields[a.Key] = strings.Trim(string(raw), `"`)
						}
					}
					seen[r.EventName] = fields
				}
			}
		}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	for i, kind := range kinds {
		f := seen[kind]
		if f == nil {
			t.Errorf("kind %s never reached logs.jsonl", kind)
			continue
		}
		if f[telemetry.LogKind] != kind || f[telemetry.LogComponent] != "c" || f[telemetry.LogTick] != "99" || f[telemetry.FieldPrefix+"n"] != itoa(i) {
			t.Errorf("kind %s fields: %v", kind, f)
		}
		if i%2 == 0 && (f[telemetry.LogRow] != "decision" || f[telemetry.LogVerdict] != "ok" || f[telemetry.LogReason] != "r" || f[telemetry.LogTarget] != "t") {
			t.Errorf("decision %s fields: %v", kind, f)
		}
		if i%2 == 1 && f[telemetry.LogRow] != "event" {
			t.Errorf("event %s fields: %v", kind, f)
		}
	}
}

func itoa(i int) string {
	b, _ := json.Marshal(i)
	return string(b)
}
