# Local observability stack

[Documentation](../README.md) · [Developer guide](README.md)

An optional Grafana LGTM backend (Grafana, Tempo, Loki, Prometheus and an OTLP
collector in one container) for inspecting RimGovernor telemetry. Nothing
requires it: the brain runs and keeps local postmortem evidence with no
backend. See epic #2778.

Current state: the in-process brain (`BrainHost`) writes only local OTLP/JSON
files under `RIMGOVERNOR_OTLP_DIR` (default `<save-data>/RimGovernor/otlp`). It
has no endpoint to send to and writes no SQLite observation copy, so the
dashboards below are empty until something else feeds the collector (see the
smoke test).

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
| Snapshot mount | host directory `../../.rimgovernor/profile/observe` relative to the compose file (override with `RIMGOVERNOR_OBSERVE_DIR`) mounted read-only at `/rimgovernor-state` |

Ports bind to loopback only.

## Observation dashboards replace the launcher tabs

Grafana is the read-only observation surface; the launcher is control only
(#2803). The launcher's former Now, Explain, Problems, Log and Ledger tabs are the
dashboards `rg-now`, `rg-concern-transitions`, `rg-problems`, `rg-log` and `rg-ledger`; the
tab-to-panel map and which Loki and Prometheus names were verified is in the
[dashboards README](../../tools/observability/dashboards/README.md). Their
sources are Loki (decision and event rows, as OTLP logs),
Prometheus (native call and pace instruments) and the SQLite snapshot
(`concern_transitions`, `ledger_rows`). They are empty until a sender exports to the collector. The launcher's Observability section links to them by exact URL
(`tools/launcher/Links.cs`) and shows a one-line hint while the stack is down.

Loki detail: only `service_name` and `service_instance_id` are index labels;
the other OTLP attributes are structured metadata named with underscores
(`rimgovernor_kind`) and are filtered with `| name=...` after the selector.

## SQLite panels

Grafana can chart a derived read-only copy of the session store (for example
`concern_transitions`) next to traces and metrics. The copy is never
authoritative ([persistence contracts](contracts/persistence-contracts.md)) and
nothing in the brain reads it. No component writes
`<profile>/observe/current.sqlite` today, so these panels are empty.

The mount is read-only on a copy, not on the live file: a read-only mount of a
WAL database works only while a writer holds the WAL (the reader cannot create
`-shm`), and a writable mount would let a Linux reader touch a file a Windows
process holds. The plugin installs after Grafana reports up, so queries return
`Plugin not registered` for a short while after `up`. The `GF_INSTALL_PLUGINS`
variable is ignored by this image.

## Smoke test

With the stack up, send one span with the OpenTelemetry collector contrib
`telemetrygen`:

```
docker run --rm --network host ghcr.io/open-telemetry/opentelemetry-collector-contrib/telemetrygen:latest traces --otlp-insecure --otlp-endpoint localhost:4317 --traces 1
```

Open Grafana, Explore, select the Tempo data source, and search for service
`telemetrygen`.

`--network host` is unreliable on Docker Desktop for Windows; if the span does not
arrive, use `--otlp-endpoint host.docker.internal:4317`.
