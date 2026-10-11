# RimGovernor Grafana dashboards

Provisioned read-only into the `RimGovernor` folder by `../dashboards.yaml`
(restart the `lgtm` service to pick up edits). Source instruments are defined in
[OTEL metrics](../../../docs/developers/contracts/otel-metrics.md). The first
three dashboards query Prometheus; the observation dashboards (last sections)
also use Loki and SQLite. No panel uses Tempo. Colony quality is deliberately not charted. The JSON files are checked by
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

## Observation dashboards (replaced the launcher tabs, #2803)

Grafana is the read-only observation surface; the launcher only controls. These
five dashboards answer what the launcher's Now, Explain, Problems, Log and Ledger tabs
answered. They need the launcher's telemetry toggle on (serve then sends OTLP
logs to Loki and writes the 5 s `--observe-snapshot` copy). Every dashboard has
a `Run` variable (Loki label `service_instance_id`, one launch of `serve`) and a
`RimGovernor dashboards` link menu.

| Launcher tab | Dashboard | uid |
| --- | --- | --- |
| Now | `now.json` | `rg-now` |
| Explain | `concern-transitions.json` | `rg-concern-transitions` |
| Problems | `problems.json` | `rg-problems` |
| Log | `log.json` | `rg-log` |
| Ledger | `ledger.json` | `rg-ledger` |

The launcher's observability section links to these by exact URL
(`go/cmd/launcher/links.go`; a test ties each uid to a dashboard file).

### Loki names (verified vs assumed)

- Verified against a running `grafana/otel-lgtm:0.36.0` with synthetic OTLP
  records: the index labels are only `service_name` and `service_instance_id`;
  every other OTLP attribute is structured metadata with dots turned into
  underscores (`rimgovernor.kind` is `rimgovernor_kind`) and is filtered after
  the selector with `| name=...`. Body-less event rows print through
  `line_format`. `label_values` does not list structured metadata, so the Log
  dashboard's Kind is a regex textbox.
- Verified: the SQLite data source reads the snapshot copy; the panel queries
  returned rows for a hand-built snapshot.
- Assumed, not verified: that the real controller's rows carry exactly the
  attribute names in `docs/developers/contracts/flight-rows.md` (the panels use
  `rimgovernor_kind`, `_component`, `_verdict`, `_reason`, `_target`, `_tick`),
  and that the Prometheus panels render on real data. No real controller was
  run against the stack for this change.

### now.json: "RimGovernor: now"

| Panel | Source |
| --- | --- |
| Game tick | Loki, newest `rimgovernor_tick` |
| Ticks per second now | Prometheus `game.ticks_per_second` |
| Concerns waiting | SQLite `concern_transitions`, latest row per concern that has a cause |
| Concerns progressing | SQLite `concern_transitions`, latest row per concern without a cause |
| Colony stage | Loki `colony_stage` rows |
| Latest rounds review | Loki `rounds_review` rows |
| Concerns: current state | SQLite `concern_transitions`, latest row per concern |
| Pace: newest clock steps | Loki `clock_step` rows |
| Last window stops | Loki `clock_stop` rows (reason, evidence, benign, detect and stop ticks) |
| Window stops by reason | Loki `clock_stop` rows counted by reason |
| Authority changes | Loki `authority` rows |

### concern-transitions.json: "RimGovernor: concern transitions"

Queries the `RimGovernor state` SQLite data source (`../datasources.yaml`,
plugin `frser-sqlite-datasource`). It reads `<profile>/observe/current.sqlite`,
the derived read-only copy of the session store that `serve --observe-snapshot`
rewrites every 5 s. It is empty until a controller has run with the flag, and
the plugin installs a short while after Grafana reports up. Variable `concern`
filters the timeline. Times are UTC (the plugin container has no local zone).

| Panel | Source |
| --- | --- |
| Concern timeline | `concern_transitions`, every change of the selected concern |
| Concerns by latest change | `concern_transitions`, newest row per concern |
| Concern transitions by cause | `concern_transitions` grouped by concern, verdict and cause |

### problems.json: "RimGovernor: problems"

| Panel | Source |
| --- | --- |
| WARN rows | Loki, WARN row count over the range |
| ERROR rows | Loki, ERROR row count over the range |
| Native errors | Prometheus `native.calls`, `outcome=error` |
| Native calls | Prometheus `native.calls` |
| Native calls per scheduler step | Prometheus `native.calls` over the `scheduler.step.duration` count |
| WARN and ERROR rows by kind | Loki, WARN and ERROR counts by kind |
| Problem rows collapsed | Loki, WARN and ERROR rows grouped by kind, component, verdict, reason and target |
| WARN and ERROR rows | Loki, the rows themselves |
| Refusals | Loki, rows with `verdict=refused` |
| Authority changes | Loki `authority` rows |

### log.json: "RimGovernor: log"

Variables: `Level` (default WARN and ERROR), `Kind` (regex), `Search`
(case-insensitive match on the printed line).

| Panel | Source |
| --- | --- |
| Rows by kind | Loki, row counts by kind for the selected filters |
| Log | Loki, every decision and event row, filtered |
| Player events | Loki, `combat_summary`, `defense_action`, `colony_stage`, `tech_tier`, `authority` and `rule` rows |

### ledger.json: "RimGovernor: ledger"

Queries the `RimGovernor state` SQLite data source. The controller replaces the
`ledger_rows` table after every ledger review (#2805): a derived copy of the
latest `policy.LedgerView`, one row per row of each former Ledger tab table, with
the row's fields as a JSON object in `cells`. No variables; times are game
ticks. Every question of the removed Ledger tab maps to one panel.

| Panel | Source |
| --- | --- |
| Latest review | `ledger_rows` section `round`: status, tick, counts, whether a planner abstained |
| Declared orders | section `order`: owners, recipe and product, target, mode, bench kind, worker, ingredients, state, benches, placing, unplaced reason, shortfall, latest placement outcome |
| Orphan bills | section `orphan`: grace Rounds left, removing, held by an abstain |
| Unmet throughput by bench kind | section `unmet`: units a day short, why, further bench requested |
| Calibration windows | section `window`: predicted against observed stock gain |
| Never-removed bills | section `excluded` |
| Silver gap | section `export_gap`: gap, in flight, remaining |
| Export candidates | section `export_candidate`: best scored candidates and the ordered ones |
| Export candidates dropped | section `export_drop`: dropped counts by reason |
| Planners declaring | section `declarer`: orders and abstain reasons |

The table is empty before the first review and keeps the last review after the
controller exits. The placement outcome is the one known when the review ran, so
it trails a plan committed in the same Round by one review. Verified: the
provisioned dashboard loads in grafana/otel-lgtm:0.36.0 and every panel query
returns rows from a synthetic snapshot built from the sample view; not run
against a real controller's rows.
### Not carried over

The old tabs also showed a few things these dashboards do not answer: the
resource deficit percentage and the review deadline on Now, the cache hit ratio
on Problems, and the plain-English wording of each cause on Explain (the
dashboards show the cause identifier).
