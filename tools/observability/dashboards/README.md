# RimGovernor Grafana dashboards

Provisioned read-only into the `RimGovernor` folder by `../dashboards.yaml`
(restart the `lgtm` service to pick up edits). Source instruments are defined in
[OTEL metrics](../../../docs/developers/contracts/otel-metrics.md). All panels
query the Prometheus data source; no panel uses the Tempo or Loki data sources
yet. Colony quality is deliberately not charted. The JSON files are checked by
`go/internal/telemetry/dashboards_test.go` (valid JSON, unique ids, known metric
names, every panel listed here).

## Prometheus names (verified vs assumed)

Instrument `rimgovernor.native.phase.duration` (unit `ms`) is queried as
`rimgovernor_native_phase_duration_milliseconds_{bucket,sum,count}`; a counter such as
`rimgovernor.native.calls` (unit `{call}`) as `rimgovernor_native_calls_total`.

- Verified (Prometheus OTLP guide): the default OTLP translation strategy is
  `UnderscoreEscapingWithSuffixes`, which escapes dots to underscores and
  appends type and unit suffixes.
- Assumed, not verified against a running stack (none was available when this was
  written): that `grafana/otel-lgtm:0.36.0` uses that default; the `ms` ->
  `_milliseconds` unit word; `_total` on counters; `{call}`-style annotation units
  being dropped; the gauge `rimgovernor.game.ticks_per_second` (unit `{tick}/s`),
  whose final name may or may not gain a unit suffix (its panel therefore matches
  `{__name__=~"rimgovernor_game_ticks_per_second.*"}`); and the Prometheus data
  source uid `prometheus` (the lgtm image's provisioned default).
- Not verified: that every panel renders data. First check after starting the
  stack: Explore > Prometheus > metric browser, search `rimgovernor_`. If names
  differ, fix the constants in the panel queries and this section.

## Dashboards and panels

### governor-efficiency.json: "RimGovernor: governor efficiency"

| Panel | Source instrument |
| --- | --- |
| Game ticks per second | `game.ticks_per_second` (gauge) |
| Controller pause share of wall time | `scheduler.controller_pause` (rate of sum) |
| Stop latency (native clock stop to step start) | `scheduler.stop_latency` p50/p95 |
| Controller pause per step | `scheduler.controller_pause` p50/p95 |
| Window stop to resume (stop pause) | `scheduler.stop_pause` p50/p95 |
| Critical wave duration - controller leg only, NOT hazard-to-effect | `scheduler.critical_wave.duration` p50/p95 |
| Telemetry dropped | `telemetry.dropped` |

There is no end-to-end hazard-to-effect metric. The critical-wave panel covers only
the controller leg (wave start to protective plans ready); dispatch and the game
carrying out the order are not in it. The `class=critical` spans in Tempo and the
`action.outcomes` panels below cover the rest piecewise.

### scheduler-native-bottlenecks.json: "RimGovernor: scheduler and native bottlenecks"

Variable `phase` (default `call`) is read from the `phase` label values.

| Panel | Source instrument |
| --- | --- |
| Native phase latency p50 by phase | `native.phase.duration` |
| Native phase latency p95 by phase | `native.phase.duration` |
| Slowest native tools (p95 of selected phase) | `native.phase.duration` by `native_tool`, filtered by `$phase` |
| Native call rate by outcome | `native.calls` |
| Scheduler step duration p50 by cause | `scheduler.step.duration` by `step_cause` |
| Scheduler step duration p95 by cause | `scheduler.step.duration` by `step_cause` |
| Scheduler steps per second by cause | `scheduler.step.duration` (count rate) |

### refusals-and-outcomes.json: "RimGovernor: refusals and action outcomes"

| Panel | Source instrument |
| --- | --- |
| Action refusals by reason | `action.outcomes`, `verdict=refused` |
| Action outcomes by verdict | `action.outcomes` |
| Action outcomes by kind and verdict (dashboard range) | `action.outcomes` |
| Admission decisions by verdict | `admission.decisions` |
| Admission refusals by reason | `admission.decisions`, `verdict=refused` |
| Native refusals by tool | `native.calls`, `outcome=refused` |

Quantiles are estimated from the explicit histogram buckets in the contract, so
they are bucket-resolution values.
