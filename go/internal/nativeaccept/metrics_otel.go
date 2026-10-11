package nativeaccept

import (
	"path/filepath"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/otel"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// The run's native-call and pause costs come from the service's exported OTEL
// metrics (docs/developers/contracts/otel-metrics.md), not from the log ring:
// every metrics.jsonl ring beside a service's logs.jsonl (the first launch's
// under output/otlp, each restart's under service-N/otlp) holds cumulative
// instruments, so the last export of each process is its total and the run's
// value is the sum over processes.

// otelTotals is the sum, over every process of the run, of the instruments the
// series block reads.
type otelTotals struct {
	calls, errs           float64
	queueSum, execSum     float64
	queueCount, execCount float64
	controllerPauseMs     float64
}

// metricsRings lists the run's metric rings: the first launch's under output
// and each restart's under its service-N directory.
func metricsRings(output string) []string {
	paths := []string{otel.SiblingRing(ServiceLogsPath(output), otel.MetricsFile)}
	matches, _ := filepath.Glob(filepath.Join(output, "service-*", "otlp", otel.MetricsFile))
	sort.Strings(matches)
	return append(paths, matches...)
}

// readOTELTotals folds the run's metric rings. A missing or unreadable ring
// contributes nothing.
func readOTELTotals(output string) otelTotals {
	type key struct{ instance, name, attrs string }
	latest := map[key]otel.Point{}
	for _, ring := range metricsRings(output) {
		_ = otel.ReadMetrics(ring, func(m otel.Metric) error {
			if !strings.HasPrefix(m.Name, "rimgovernor.") {
				return nil
			}
			for _, p := range m.Points {
				k := key{m.Resource.Instance, m.Name, attrKey(p.Attrs)}
				if old, ok := latest[k]; !ok || !p.Time.Before(old.Time) {
					latest[k] = p
				}
			}
			return nil
		})
	}
	var t otelTotals
	for k, p := range latest {
		switch k.name {
		case telemetry.NativeCallsMetric:
			t.calls += p.Value
			if p.Attrs.Str("outcome") == "error" {
				t.errs += p.Value
			}
		case telemetry.NativePhaseMetric:
			switch p.Attrs.Str("phase") {
			case "native_queue":
				t.queueSum += p.Sum
				t.queueCount += float64(p.Count)
			case "native_execute":
				t.execSum += p.Sum
				t.execCount += float64(p.Count)
			}
		case telemetry.ControllerPauseMetric:
			t.controllerPauseMs += p.Sum
		}
	}
	return t
}

func attrKey(a otel.Attrs) string {
	keys := make([]string, 0, len(a))
	for k := range a {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	var b strings.Builder
	for _, k := range keys {
		b.WriteString(k + "=")
		if s, ok := a[k].(string); ok {
			b.WriteString(s)
		}
		b.WriteByte(';')
	}
	return b.String()
}

// fill writes the OTEL-derived metrics into m. wallMs is the run's wall time.
func (t otelTotals) fill(m Metrics, wallMs float64) {
	m["native_calls"] = t.calls
	m["native_errors"] = t.errs
	if t.queueCount > 0 {
		m["native_queue_ms_mean"] = t.queueSum / t.queueCount
	}
	if t.execCount > 0 {
		m["native_exec_ms_mean"] = t.execSum / t.execCount
	}
	if wallMs > 0 {
		m["paused_fraction"] = t.controllerPauseMs / wallMs
	}
}
