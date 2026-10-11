# Local observability stack

[Documentation](../README.md) · [Developer guide](README.md)

An optional Grafana LGTM backend (Grafana, Tempo, Loki, Prometheus and an OTLP
collector in one container) for inspecting RimGovernor telemetry. Nothing
requires it: the controller runs and keeps local postmortem evidence with no
backend. See epic #2778.

## Requirements

Docker with Compose v2: Docker Desktop on Windows (WSL 2 backend) or Docker
Engine on Linux/macOS. Native-Windows LGTM binaries are not supported; use the
container.

## Start and stop

From the repository root:

```
docker compose -f tools/observability/compose.yml up -d
docker compose -f tools/observability/compose.yml down      # keep data
docker compose -f tools/observability/compose.yml down -v   # also delete data
```

The first start pulls the image (about 1 GB) and takes about a minute to become
ready.

## Stable interface (consumed by the launcher)

| Item | Value |
| --- | --- |
| Compose file | `tools/observability/compose.yml` |
| Compose project / service | `rimgovernor-observability` / `lgtm` |
| Image | `grafana/otel-lgtm:0.36.0` (pinned; bump deliberately) |
| OTLP gRPC / HTTP | `127.0.0.1:4317` / `127.0.0.1:4318` |
| Grafana | `http://127.0.0.1:3000` (default login `admin` / `admin`) |
| Data | named volume `lgtm-data`, mounted at `/data`; survives `down` |
| Dashboards | JSON files in `tools/observability/dashboards/` are provisioned read-only into the `RimGovernor` folder by `dashboards.yaml`; restart the service to pick up new files |

Ports bind to loopback only.

## Pointing serve at it

Export through the standard OTLP environment variable, for example
`OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` (gRPC) in the environment of
`serve`. The exporter setting itself belongs to the OTEL core work (#2782); this
page will name the exact switch once it lands. Without it serve exports nothing
to a backend and still writes its local file sink.

## Smoke test

With the stack up, send one span with the OpenTelemetry collector contrib
`telemetrygen`:

```
docker run --rm --network host ghcr.io/open-telemetry/opentelemetry-collector-contrib/telemetrygen:latest traces --otlp-insecure --otlp-endpoint localhost:4317 --traces 1
```

Open Grafana, Explore, select the Tempo data source, and search for service
`telemetrygen`. Once the core exporter exists, a serve run replaces this step.

`--network host` is unreliable on Docker Desktop for Windows; if the span does not
arrive, use `--otlp-endpoint host.docker.internal:4317`.
