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
| Dashboards | JSON files in `tools/observability/dashboards/` (panel list: its README) are provisioned read-only into the `RimGovernor` folder by `dashboards.yaml`; restart the service to pick up new files |

| SQLite data source | plugin `frser-sqlite-datasource@4.0.6` (`GF_PLUGINS_PREINSTALL`), data source uid `rimgovernor-state` provisioned from `datasources.yaml`, reading `/rimgovernor-state/current.sqlite` with `immutable=1` |
| Snapshot mount | host `<profile>/observe` (default `../../.rimgovernor/go/profile/observe` relative to the compose file; override with `RIMGOVERNOR_OBSERVE_DIR`) mounted read-only at `/rimgovernor-state` |

Ports bind to loopback only.

## SQLite panels

Grafana can chart the session store (for example `concern_transitions`) next to
traces and metrics. It never opens the live store: `serve --observe-snapshot
<absolute path>` (off by default; the launcher passes
`<profile>/observe/current.sqlite` when the Settings telemetry toggle is on)
copies the whole store every 5 s through SQLite's online backup into
`<path>.tmp` and renames it over `<path>`. The copy is derived and read-only:
nothing in the controller reads it, and it is never authoritative
([persistence contracts](contracts/persistence-contracts.md)). Because the name is
fixed, it survives the per-launch `state-*.sqlite` files and the schema-change
rename, and it stays queryable after the controller exits. A failed copy is logged
and retried.

Cost: the backup holds the store's single connection for its duration, so
journal writes wait that long once per 5 s. Measured on a synthetic 30 MB store
(about the largest real state file seen): 90-140 ms per copy, so about 30 ms for
a typical 9 MB store; it grows linearly with the store.

Why a copy and not a bind mount of the live file: a read-only mount of a WAL
database works only while a writer holds the WAL (the reader cannot create
`-shm`), and a writable mount would let a Linux reader touch a file a Windows
process holds. The plugin installs after Grafana reports up, so queries return
`Plugin not registered` for a short while after `up`. The `GF_INSTALL_PLUGINS`
variable is ignored by this image.

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
