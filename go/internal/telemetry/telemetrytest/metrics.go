package telemetrytest

import (
	"testing"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/metric/metricdata"
)

// Series is one data point of a collected metric: its attributes by key and,
// for a counter or gauge, its value or, for a histogram, its count.
type Series struct {
	Attrs map[string]string
	Value float64
	// Bounds are a histogram's explicit bucket boundaries; nil for others.
	Bounds []float64
}

// SeriesOf is every data point of the named metric (nil when it has none) and
// its unit.
func SeriesOf(all []metricdata.Metrics, name string) ([]Series, string) {
	for _, m := range all {
		if m.Name != name {
			continue
		}
		var out []Series
		attrs := func(set attribute.Set) map[string]string {
			a := map[string]string{}
			for _, kv := range set.ToSlice() {
				a[string(kv.Key)] = kv.Value.String()
			}
			return a
		}
		switch data := m.Data.(type) {
		case metricdata.Sum[int64]:
			for _, p := range data.DataPoints {
				out = append(out, Series{Attrs: attrs(p.Attributes), Value: float64(p.Value)})
			}
		case metricdata.Gauge[float64]:
			for _, p := range data.DataPoints {
				out = append(out, Series{Attrs: attrs(p.Attributes), Value: p.Value})
			}
		case metricdata.Histogram[float64]:
			for _, p := range data.DataPoints {
				out = append(out, Series{Attrs: attrs(p.Attributes), Value: float64(p.Count), Bounds: p.Bounds})
			}
		}
		return out, m.Unit
	}
	return nil, ""
}

// Metrics installs a manual-reader MeterProvider as the global one for the
// test. The returned function collects what has been recorded so far
// (cumulative). The global is restored afterwards, so tests using it must not
// run in parallel.
func Metrics(t testing.TB) func() []metricdata.Metrics {
	t.Helper()
	reader := sdkmetric.NewManualReader()
	provider := sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader))
	previous := otel.GetMeterProvider()
	otel.SetMeterProvider(provider)
	t.Cleanup(func() {
		otel.SetMeterProvider(previous)
		_ = provider.Shutdown(t.Context())
	})
	return func() []metricdata.Metrics {
		var rm metricdata.ResourceMetrics
		if err := reader.Collect(t.Context(), &rm); err != nil {
			t.Fatal(err)
		}
		var out []metricdata.Metrics
		for _, scope := range rm.ScopeMetrics {
			out = append(out, scope.Metrics...)
		}
		return out
	}
}
