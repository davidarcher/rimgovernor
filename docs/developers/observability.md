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

Serve exports to a collector only when told where it is, by either of:

- the flag `serve --otlp-endpoint http://localhost:4317` (the launcher passes
  this; `--otlp-protocol http/protobuf` selects OTLP/HTTP, default gRPC), or
- the standard environment variable `OTEL_EXPORTER_OTLP_ENDPOINT` (also
  `OTEL_EXPORTER_OTLP_PROTOCOL` and the per-signal `*_TRACES_/_METRICS_/_LOGS_`
  endpoints) in the environment of `serve`. The flag wins over the environment.

With neither, serve exports nothing to a backend and still writes its local
OTLP/JSON files under `<profile>/flight/otlp/`. An unreachable collector never
slows or stops serve; the loss is counted (see the
[OTEL core contract](contracts/otel-core.md)).

## Smoke test

With the stack up, send one span with the OpenTelemetry collector contrib
`telemetrygen`:

```
docker run --rm --network host ghcr.io/open-telemetry/opentelemetry-collector-contrib/telemetrygen:latest traces --otlp-insecure --otlp-endpoint localhost:4317 --traces 1
```

Open Grafana, Explore, select the Tempo data source, and search for service
`telemetrygen`. A serve run with `--otlp-endpoint` also sends its metrics (the
`rimgovernor.telemetry.dropped` counter, every 10 s) and any spans and logs the
instrumentation children add.

`--network host` is unreliable on Docker Desktop for Windows; if the span does not
arrive, use `--otlp-endpoint host.docker.internal:4317`.
