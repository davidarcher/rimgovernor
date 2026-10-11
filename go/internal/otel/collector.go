package otel

import (
	"context"
	"fmt"
	"net/url"
	"os"
	"strings"

	"go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploggrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp"
	"go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetricgrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetrichttp"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracehttp"
	sdklog "go.opentelemetry.io/otel/sdk/log"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
)

// DefaultCollectorEndpoint is the address the compose stack publishes
// (tools/observability/compose.yml); pass it to enable export to that stack.
const DefaultCollectorEndpoint = "http://localhost:4317"

// collector holds the stock OTLP exporters to a backend.
type collector struct {
	traces  sdktrace.SpanExporter
	logs    sdklog.Exporter
	metrics sdkmetric.Exporter
}

// collectorEnabled reports whether any collector endpoint is configured.
func collectorEnabled(cfg Config) bool {
	if cfg.CollectorEndpoint != "" {
		return true
	}
	for _, k := range []string{"OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"} {
		if os.Getenv(k) != "" {
			return true
		}
	}
	return false
}

// newCollector builds the exporters, or returns nil when none is configured.
// The exporters connect lazily, so an unreachable collector is not an error
// here: it shows up as export_failed drops.
func newCollector(ctx context.Context, cfg Config) (*collector, error) {
	if !collectorEnabled(cfg) {
		return nil, nil
	}
	protocol := strings.ToLower(envOr("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc"))
	if cfg.CollectorProtocol != "" {
		protocol = strings.ToLower(cfg.CollectorProtocol)
	}
	var (
		host, path string
		insecure   bool
	)
	if cfg.CollectorEndpoint != "" {
		raw := cfg.CollectorEndpoint
		if !strings.Contains(raw, "://") {
			raw = "http://" + raw
		}
		u, err := url.Parse(raw)
		if err != nil || u.Host == "" {
			return nil, fmt.Errorf("otel: invalid collector endpoint %q", cfg.CollectorEndpoint)
		}
		host, path, insecure = u.Host, strings.TrimRight(u.Path, "/"), u.Scheme == "http"
	}
	c := &collector{}
	var err error
	switch protocol {
	case "grpc":
		c.traces, err = newGRPCTraces(ctx, host, insecure)
		if err == nil {
			c.logs, err = newGRPCLogs(ctx, host, insecure)
		}
		if err == nil {
			c.metrics, err = newGRPCMetrics(ctx, host, insecure)
		}
	case "http/protobuf", "http":
		c.traces, err = newHTTPTraces(ctx, host, path, insecure)
		if err == nil {
			c.logs, err = newHTTPLogs(ctx, host, path, insecure)
		}
		if err == nil {
			c.metrics, err = newHTTPMetrics(ctx, host, path, insecure)
		}
	default:
		return nil, fmt.Errorf("otel: unsupported OTLP protocol %q (use grpc or http/protobuf)", protocol)
	}
	if err != nil {
		return nil, fmt.Errorf("otel: collector exporter: %w", err)
	}
	return c, nil
}

func newGRPCTraces(ctx context.Context, host string, insecure bool) (sdktrace.SpanExporter, error) {
	var o []otlptracegrpc.Option
	if host != "" {
		o = append(o, otlptracegrpc.WithEndpoint(host))
		if insecure {
			o = append(o, otlptracegrpc.WithInsecure())
		}
	}
	return otlptracegrpc.New(ctx, o...)
}

func newGRPCLogs(ctx context.Context, host string, insecure bool) (sdklog.Exporter, error) {
	var o []otlploggrpc.Option
	if host != "" {
		o = append(o, otlploggrpc.WithEndpoint(host))
		if insecure {
			o = append(o, otlploggrpc.WithInsecure())
		}
	}
	return otlploggrpc.New(ctx, o...)
}

func newGRPCMetrics(ctx context.Context, host string, insecure bool) (sdkmetric.Exporter, error) {
	var o []otlpmetricgrpc.Option
	if host != "" {
		o = append(o, otlpmetricgrpc.WithEndpoint(host))
		if insecure {
			o = append(o, otlpmetricgrpc.WithInsecure())
		}
	}
	return otlpmetricgrpc.New(ctx, o...)
}

func newHTTPTraces(ctx context.Context, host, path string, insecure bool) (sdktrace.SpanExporter, error) {
	var o []otlptracehttp.Option
	if host != "" {
		o = append(o, otlptracehttp.WithEndpoint(host), otlptracehttp.WithURLPath(path+"/v1/traces"))
		if insecure {
			o = append(o, otlptracehttp.WithInsecure())
		}
	}
	return otlptracehttp.New(ctx, o...)
}

func newHTTPLogs(ctx context.Context, host, path string, insecure bool) (sdklog.Exporter, error) {
	var o []otlploghttp.Option
	if host != "" {
		o = append(o, otlploghttp.WithEndpoint(host), otlploghttp.WithURLPath(path+"/v1/logs"))
		if insecure {
			o = append(o, otlploghttp.WithInsecure())
		}
	}
	return otlploghttp.New(ctx, o...)
}

func newHTTPMetrics(ctx context.Context, host, path string, insecure bool) (sdkmetric.Exporter, error) {
	var o []otlpmetrichttp.Option
	if host != "" {
		o = append(o, otlpmetrichttp.WithEndpoint(host), otlpmetrichttp.WithURLPath(path+"/v1/metrics"))
		if insecure {
			o = append(o, otlpmetrichttp.WithInsecure())
		}
	}
	return otlpmetrichttp.New(ctx, o...)
}
