package nativeaccept

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// otlpMetricLine is one metrics.jsonl line: a single metric of process
// instance with the given data-point JSON.
func otlpMetricLine(instance, name, kind, points string) string {
	body := map[string]string{
		"sum":       `"sum":{"aggregationTemporality":2,"isMonotonic":true,"dataPoints":[%s]}`,
		"histogram": `"histogram":{"aggregationTemporality":2,"dataPoints":[%s]}`,
	}[kind]
	return fmt.Sprintf(`{"resourceMetrics":[{"resource":{"attributes":[{"key":"service.instance.id","value":{"stringValue":%q}}]},"scopeMetrics":[{"scope":{"name":"t"},"metrics":[{"name":%q,%s}]}]}]}`,
		instance, name, fmt.Sprintf(body, points))
}

func sumPoint(tool, outcome string, at int, value int) string {
	return fmt.Sprintf(`{"attributes":[{"key":"native_tool","value":{"stringValue":%q}},{"key":"outcome","value":{"stringValue":%q}}],"timeUnixNano":"%d","asInt":"%d"}`, tool, outcome, at, value)
}

func histPoint(phase string, at, count int, sum float64) string {
	return fmt.Sprintf(`{"attributes":[{"key":"phase","value":{"stringValue":%q}}],"timeUnixNano":"%d","count":"%d","sum":%v,"bucketCounts":["%d","0"],"explicitBounds":[1]}`, phase, at, count, sum, count)
}

func writeMetricRing(t *testing.T, dir string, lines ...string) {
	t.Helper()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "metrics.jsonl"), []byte(strings.Join(lines, "\n")+"\n"), 0o644); err != nil {
		t.Fatal(err)
	}
}

// The block's native costs are the sum, over processes, of each process's
// last cumulative export.
func TestComputeMetricsFromOTELMetricRings(t *testing.T) {
	output := t.TempDir()
	writeMetricRing(t, filepath.Join(output, "otlp"),
		// An earlier export of process a (superseded), then its final one.
		otlpMetricLine("a", "rimgovernor.native.calls", "sum", sumPoint("x/read", "ok", 10, 3)),
		otlpMetricLine("a", "rimgovernor.native.calls", "sum", sumPoint("x/read", "ok", 20, 5)+","+sumPoint("x/read", "error", 20, 1)),
		otlpMetricLine("a", "rimgovernor.native.phase.duration", "histogram", histPoint("native_queue", 20, 2, 8)+","+histPoint("native_execute", 20, 2, 12)),
		otlpMetricLine("a", "rimgovernor.scheduler.controller_pause", "histogram", `{"timeUnixNano":"20","count":"2","sum":3000,"bucketCounts":["2","0"],"explicitBounds":[1]}`),
	)
	writeMetricRing(t, filepath.Join(output, "service-2", "otlp"),
		otlpMetricLine("b", "rimgovernor.native.calls", "sum", sumPoint("x/read", "ok", 5, 2)),
		otlpMetricLine("b", "rimgovernor.native.phase.duration", "histogram", histPoint("native_queue", 5, 2, 4)),
	)
	m := ComputeMetrics(Report{WallMsKey: int64(12000)}, output)
	want := Metrics{"native_calls": 8, "native_errors": 1, "native_queue_ms_mean": 3, "native_exec_ms_mean": 6, "paused_fraction": 0.25}
	for name, value := range want {
		if m[name] != value {
			t.Errorf("%s = %v, want %v", name, m[name], value)
		}
	}
	if len(m) != len(MetricNames) {
		t.Errorf("block has %d metrics, want %d", len(m), len(MetricNames))
	}
}
