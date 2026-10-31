# OTEL signals

[Documentation](../../README.md)

The brain's diagnostic stream is OpenTelemetry. Every decision and event
(a row) is an OTLP log record in `logs.jsonl`; spans go to `traces.jsonl` and
numbers to `metrics.jsonl` ([OTEL core](otel-core.md), [OTEL metrics](otel-metrics.md)).
These files are always written, with or without a collector. This contract
defines the row shapes, kinds, the record mapping, the readers and the retention.

Actual native takeover of an installed pawn activity uses the existing
`ObservationInvalidated` event and its clock invalidation row, naming the Pawns
family and pawn identifier. The event consumer hints the worker to re-read ownership;
telemetry itself is not the wake. This occurs once when the old handle detaches,
not on every job-driver or toil tick. The existing execution row records the
`native_job_takeover` failure reason when the attempt was still running.

A row has a `kind`, a context (`level` `INFO`/`WARN`/`ERROR`, `at`, `tick` when
the service has observed one, `component`, and the trace and span ids when it
was emitted under a span) and a payload. Oversized payloads are replaced by
`{truncated, original_bytes, sha256, preview}` plus the correlation keys
`request`, `tool`, `native_tool`, `category`, `timing` and the decision keys
`verdict`, `reason`, `target`, `dur_ms`. [Mapping](#mapping) gives the record
attributes; `bridge.RecordFromLog` rebuilds the row from a record.

## Decision rows

One row per planner run, method selection, admission outcome and action dispatch, emitted with
`telemetry.Decide(ctx, telemetry.Decision{...})`. The payload shape is fixed:

```json
{"verdict": "refused", "reason": "critical_wave_budget", "target": "step", "dur_ms": 1.5, "attrs": {}}
```

- `verdict`: the outcome word. Vocabulary: `admitted`, `refused`, `waiting`, `applied`, `failed`,
  `skipped`, `ok`, plus a kind's own words where its table says so.
- `reason`: a stable short word. No data inside it (the data goes in `attrs`): rows collapse on it.
- `target`: what it was about (planner, concern, method, plan, pawn, zone, tool).
- `dur_ms`: milliseconds, `0` when untimed.
- `attrs`: kind-specific fields; always an object. Durations render as milliseconds, errors as text.
- No free text: a decision row has no `msg`. The row's `level` is `WARN` only for a refusal or failure a
  reader should surface.

Repeat collapsing keys on `(kind, component, verdict, reason, target)`.

The helper writes the row only; nothing is rendered to stderr.

## Kinds

Register a kind and a consuming reader before adding an emission.

### Native bridge (`RimGovernor.Brain.Runtime/Client`)

| Kind | Shape and fields |
|---|---|
| `acceptance_case` | Acceptance-only rectangle evidence, component `acceptance`: `case`, `run_id`, `result` (source-generated `RectangleCaseResult` JSON: phase, success, error and accumulated evidence). Durable at start, captured observations and completion/failure; not per-hop narration. Reader: `RectanglePilotResults` via `TelemetryKinds.AcceptanceCase`, including lost-reply diagnostics. |
| `combat_order` (decision) | `target` pawn, `verdict` `applied`/`refused`, `reason` refusal, attrs `index`, `job` |
| `clock_step` (decision) | `verdict` step outcome (`planned`, `deferred`, `failed`), `reason` `step`, `deferred` or `step_error`, `target` the step's cause (`review`, `wake`, `retry`, `full`), `dur_ms` step wall; attrs also `gate_wait_ms`, `journal_ms`, `stop` and `stop_latency_ms` (a step woken by a native hold: hold stamp to step start), the pacing fields (`pacing_reason`, `paced_tps`, `tps_ceiling`, `effective_tps`) and `error`; attrs `reads`, `tools` (calls per native facade method), `running` (read tally), `hop_wait_ms`, `hop_run_ms` and `hop_alloc_kb` (the game-thread hops: queue wait, run time and allocation summed) with `hop_kb` and `hop_run` (allocation KB and run ms per hop operation; `hop_run` also lists the parts a hop reports of its own run, keyed `<operation>/<part>`, such as `facade/construction_activity/paths` and the colony-facts read's sections `facade/observations_read_colony_facts/cf.<section>`, which are not summed into `hop_run_ms`); the step profile (#3556): `stage_ms` and `stage_alloc_kb` (wall and process allocation per stage: `gate`, `read`, `select`, `refresh`, `rounds`, `extent`, `critical`, `optional`, `record`), `selection` (how many planners each selection word chose: `dirty`, `due`, `dirty+due`, `full`, `all`), `rounds` on a step that ran planners: why it ran the review coordinator first (`due`: the last colony review had stood `PlannerSchedulerConfig.ReviewEveryTicks`, or there was none; `wake`: an early review; `all`: every planner ran; `task`: a coupled Method's worker finished a task with a recipe left, #3624; `first_plan`: the colony's first layout plan holds the clock, #3652) or `last` (its planners read the last committed review), `kept`: how many ready Needs the family entries kept the last run's wait or refusal for, skipping their work (#3556; absent when none) |
| `clock_run` (decision) | one per scheduler step that changed or refused the clock, filed by `PlannerRunDriver`. `verdict` `run`, `adjust`, `hold`, `lost` (a `COLONY_LOST` hold, terminal: no Run follows, #2819), `refused` or `failed`, `reason` `resumed`, `colony_lost`, `watch_changed`, `speed_changed`, `coupled_order`, `urgent_build` (a running clock held for the review of an urgent build's placed sites, #3537), `autosave`, `combat_event`, `hunt_review` (an open hunt's review hold re-runs the defense planner, #3702), the first refused reason (a refused decision is `refused` whether the clock was running or held), or `run_error` (any other failure); attrs `running`, `unwatched`, `coupled_orders`, `held_for` (the native hold reason), `hold_at_tick` (the review tick the Run named, #3556), `combat`, `refused`, `first_plan_fact` (on a `first_plan` refusal: the station fact the first layout plan waits for, or `layout plan` once it is recorded and no review has planned with it, #3652), `error` |
| `dispatch` (decision) | one per run that acted. `target` action, `verdict` outcome (`completed`, `refused`, `failed`, `waiting`, `held`), `reason` cause or error class; attrs `attempt`, `stage`, `stage_after`, `receipt`, `refusal_class` and `refusal_reason` (native's class and reason when the run left a refused receipt), `skipped_work` (the work types an applied work_assignment skipped because the pawn could no longer do them, comma-separated; only when any), `running`, `stale`, `error`, `repeated`, `reads`, `tools`. Level `WARN` for a failure other than a dependency wait. Written for a run that reached native (carries `receipt`, `running` and the read tally; `rimgovernor phases` counts only these) or whose outcome changed (`changed` true, `repeated` the unrecorded runs that restated the previous outcome); an unchanged run that never reached native writes nothing. `reason` is the stage word for a wait, `dependency`, `stale`, `error`, the first refusal, or `completed`; `refused` (all refusal reasons) rides in attrs. The cancelled-settle of an undispatched action is `failed`/`cancelled_settled` with `attempts`. `dur_ms` is the dispatch's wall (the batch it rode in, from the native call to its results) |

### Clock and scheduler (`RimGovernor.Brain.Runtime`)

Immediate protection adds `scope: immediate` to its `planner_step` rows,
`observation_tick` on the ColonyReview row and `decision_tick` on planner rows.
`dispatch_tick` on a Worker or synchronous combat Hands dispatch records the
journal's native dispatch tick, including uncertain replies.
`clock_step.critical_wave_ms`
includes pre-wave review. The native acceptance `ResponseEvidence` reader uses
it to separate controller activity from native outcomes and never treats an accepted dispatch as
the first native protective effect.

| Kind | Shape and fields |
|---|---|
| `planner_step` (decision) | one per planner run. `target` planner, `verdict` outcome (`admitted`, `waiting`, `refused`, `unselected`, `failed`), `reason` the refusal or wait `policy.Cause` wire value (a closed set; free-text detail is not recorded); attrs `concern` (a Concern id, or the NeedId of a family entry, which writes one row per Need; read by ExplainAudit), `subject` (optional, what the cause concerns; `layout/first-bedroom-closes` fails on a `no_space` row with subject `bedroom_template`, `SleepingRules.BedroomTemplateSubject`, #3752), `proposals`, `edits`, `cause`, `admitted`, `running`, `reconciled`, `cleaned`, `deferred`, `retaken`, `combat`, `repeated`, `error`. Every selected planner's row carries `selected`, why the step's selection chose it (`dirty`, `due`, `dirty+due`, `full`, `all`). A family Need's row carries `kept` (true) when the run kept the Need's last wait or refusal and skipped its work: a `due` selection on a step whose `rounds` word is `last` or `due` (#3556). ColonyReview targets `rounds` (`ok`/`reviewed`, `ok`/`cancelled` when an authority change cancelled the round, or `failed`/`error`; attrs `partial`, and `stage_ms` / `stage_alloc_kb` per review stage: `journal`, `observe`, `layout`, then the derivation in order `d_owed` (the owed-facts block after the layout), `d_reads` (shell targets, sale art, surgery parts), `d_stock` (stockpiles, consumption, surface ore, meals, reserve), `d_planned` (planned sites, routes, thermal and room predictions, the firebreak), `d_file` (filing the sections, the emergency and dash facts, drafts), `d_work` (goals, research needs, the bench census and work coverage), `d_clear` (the clearance census) and `derive` (the ledger, open bills, incumbents and the colony model), then `acquisition`, `review`, `admit`, `record`; `fine_ms` / `fine_alloc_kb` split two of those further (#3566): `d_stock/zones` (the journal's zone claims and shelf plans), `d_stock/stockpile` (the stockpile review and the consumption window, which run together), `d_stock/ore` (the surface-ore reads), `d_stock/meals` (drug users, meals, the reserve, baby feeding, flooring), under it `d_stock/stockpile/census` (the patches and the mirrored bench census), `d_stock/stockpile/build` (the request: zones, stores, demand), `d_stock/stockpile/shelves` (the shelf stacks and rows), `d_stock/stockpile/occupancy` (the occupancy ring read) and `d_stock/stockpile/plan` (the maintenance review); `d_planned/sites`, `d_planned/shelter` (the thermal and room predictions) and `d_planned/firebreak` (the wait for the firebreak review, which overlaps the two); `derive/ledger`, `derive/bills`, `derive/incumbents`, `derive/model`, and under the ledger `derive/ledger/benches` (the mirrored bench read), `derive/ledger/declare` (the declarers, which run together), `derive/ledger/<declarer>` (each declarer's own wall, overlapping the others', allocation 0: `tradeexport`, `bill`, `art`, `armory`, `gear`, `foodstores`, `joiners`, `carebill`, `mechbill`, `babyfood`, `medical`, `resource`) and `derive/ledger/reconcile` (the diff under the ledger lock)); the worker targets `worker_step`, one per Worker cycle that dispatched (`ok`/`dispatched`, `dur_ms` the cycle's wall; attrs `candidates`, `dispatched`, `stage_ms` / `stage_alloc_kb` for `gate`, `read`, `dispatch`, `flush`). Step journal failures target `reasons`, `waits` or `proposals` with `failed`/`journal_error`. |
| `required_fact` (decision) | a planner run skipped because a fact it needs was not read (#3753 D3: Policy's `RequiredFactException`, the one way a planner says so). Level `ERROR`, `verdict` `skipped`, `reason` `required_fact`, `target` the planner (a wave planner's name, or the review coordinator's layout step: `layout_stations`, `layout_replan_tiled`), attr `fact` the unread fact and attr `site` the throw that found it (`file:line member`, the compiler's caller info on `RequiredFactException`, #3822). Written at the planner boundary in place of the run's own row (a wave planner's `planner_step` row; the planner is not merged or filed, is no failure and runs again next step); the colony keeps running. Readers: the acceptance runner fails every case whose log slice holds one (`RequiredFactRows` via `TelemetryKinds.RequiredFact`), and the xUnit test sink (`RequiredFactSink`) throws on one. Constants in `RequiredFactTelemetry` |
| `admission` (decision) | `target` method or plan, `verdict` `admitted`/`refused`/`held`/`released`, `reason` the refusal (a method refusal; a held building method `claim_conflict` or `method_continued`; a released hunt fight `hunt_formation_empty` or `hunt_prey_gone`, #3702); attrs `concern`, `refused` (all reasons), `held_by`, `mode`, `work`, `combat_plan`, `hostiles`, `actions`, `error` |
| `clock_stop` | event row, one per combat hold the brain resumed from while a fight was open. `event` (the armed combat event), `tick`, `resume_latency_ms` (hold to the resuming `Run`), `over_budget` and `resume_budget_ms` when the resume-latency gate tripped, `ticks_since_stop` |
| `combat_summary` | event row at combat end. `stops`, `by_event`, `resume_latency_p50_ms`, `resume_latency_p95_ms`, `resume_budget_ms`, `gate_tripped`, `ticks_between_stops_p50`, `ticks_between_stops_p95` |
| `authority` | event row, the brain's view of its grant: `change` (`lost`, `retaken`; absent when the colony event consumer saw authority change under it), `reason` and the detail attrs; `WARN` for `lost`. Native's own generation moves are `clock_authority` rows ([below](#clock-and-colony-events)) |
| `colony_events_lost` | event row, component `clock-scheduler`: the brain did not receive some colony events and its next step plans Full. `lost` (native's loss counter since the open: writes that found the queue full). Read in the OTLP timeline. |
| `colony_events_delayed` | event row, component `clock-scheduler`: the consumer's context read for a batch failed; the batch is held and read again after `retry_ms`, and no step plans Full for it. `events`, `err`, `retry_ms`. Read in the OTLP timeline. |

### Clock and colony events

Native emits one row per colony event it produces (`ColonyEventRows`, written from
`NativeClockRuntime.Publish` through the brain's emitter, attached once
`BrainHost` has started), component `clock`; the brain does not republish
them. Kind constants: `TelemetryKinds.Clock*`, keys in `ColonyEventTelemetry`.
Every row carries `msg` (the event's detail), `tick`, `observed_at_unix_ms`
(native's clock), `colony`, `load` and `map`. Enum values
are their C# names (`LetterPause`, `Fast`, `Research`).

| Kind | Shape and fields |
|---|---|
| `clock_hold` | the clock entered a hold: a native hazard, a game pause, an armed combat event or a brain hold. `reason` (the `HoldReason`), `stop_at_ms` (the stamp `Status` reports as held-at), the legs `detected_tick`, `occurrence_tick`, `hold_ticks`, `detect_ticks`, the run it ended (`run_ticks`, `run_ms`, `run_tps` = ticks per wall second over that run, `run_frames` and `run_frames_over_budget` from the frame account, `ceiling_tps` the one ceiling at the hold), and `evidence` (`pawn`, `injury`, `health`, `pause`, `notifications`, `unavailable`, `combat`) with its fields: `pawn`, `pawn_name`, `x`, `z`, `pawn_reason`; `before`/`after` health maps (`severity`); `min_health_fraction`, `health_drop_fraction`; `actual_paused`, `pause_verified`, `letter_id`, `letter_label`, `letter_def`, `dialog_title`; `letters`; `combat_event`, `thing`, `combat_reason`. Readers: `StopSummaries` (speed matrix), the launcher Observe tab, and the acceptance cases (`combatlab/stops` and `hazard/*` through `TypedSession.HoldRowAsync`) |
| `clock_resume` | a Run resumed a held clock. `from` (the hold reason it ended), `speed`; `observed_at_unix_ms` is the resume time. Readers: `StopSummaries`, the launcher Observe tab |
| `clock_authority` | the native authority generation moved. `generation`, `reason`, `active`. Readers: the postmortem's authority generations, the launcher Observe tab |
| `clock_alert` | a new game alert. `key`, `label`, `priority` |
| `clock_notification` | a new letter or transient message. `source` (`letter`, `message`), `id`, `label`, `def` (letter), `text` (message) |
| `clock_injury` | a colonist injury observed without a hold. The `injury` evidence fields above |
| `clock_hostiles_cleared` | no conscious hostiles remain (the row carries the common fields only) |
| `clock_invalidation` | observed fact families changed under a running clock. `families` (`FactFamily` names), `reason`, `entity_ids` and `cells` (`min_x`, `min_z`, `max_x`, `max_z`) when native could narrow the change. Readers: `zone/delete`, `hazard/injury` |
| `clock_long_event` | a long event or transient force pause: `state` `waiting` or `cleared` |
| `clock_pause_failed` | a pending hold's pause did not take and stays armed. The pending hold's `reason` |
| `clock_speed_request` | the player asked for a speed in game (`RequestSpeed`). `speed`; `msg` names the request serial. Read in the OTLP timeline |

The other kinds are read in the OTLP timeline and the launcher's Log view.


### Colony review, layout and colony (`RimGovernor.Brain.Runtime/ReviewState`, `ColonyPlan`, `Layout*`)

| Kind | Shape and fields |
|---|---|
| `rounds_review` | event row. `revision`, `previous_revision`, `inspect_ms` (the policy inspection's wall inside the review, which the coupled search dominates), the search's accounting (#3722, keys in `ReviewTelemetry`, each -1 when no alternatives were searched: `coupled_search_expansions`, the scheduler expansions the whole comparison spent, against `coupled_traversal_bound`; `coupled_search_evaluations`, the outcome evaluations it started, one per complete plan handed to the evaluator, against `coupled_evaluation_budget`; `coupled_baseline_expansions` and `coupled_baseline_evaluations`, the mandatory baseline's share of each, counted in the totals and charged to neither allowance; the discretionary spend, total less baseline, never exceeds the allowance, and its end is the finding `outcome_evaluations_exhausted` or `traversal_bound_exhausted` in the selection's `uncertainty`), `search_phases` (the search's expansions per phase it reached: `baseline`, `warm`, `seed`, `unserved`, `extend`, `confirm`, `sweep`, and after a planned-unplaced reselection the second comparison's phases as `reselect/<phase>`; #3563), `search_profile` (per phase, #3556: `ms`, its wall; `schedule_searches`, the schedule searches it ran, a subset whose schedule was already searched at the bound runs none; `outcome_evaluations`, the outcome evaluations it started (#3722; the phases sum to `coupled_search_evaluations`); `schedule_ms`, their wall, the rest of `ms` being keying, feasibility and ranking; `infeasible`, the evaluations whose every outcome was infeasible or unassessed; `unsupported`, those whose construction lacked supported service relief; `swaps`, the extension evaluations that swapped a root exclusive with a leader member in for it rather than adding one; `improved`, the evaluations that made a new leader; `pruned`, the subsets a proven bound skipped before their schedule was searched, and `unbounded`, those evaluated because no bound was available, #3724), `selected_phase` (the phase in which the chosen subset first led; `baseline` when nothing beat it) and `warm_start` (`seeded` when the previous selection resolved to current roots and was screened first, `missing` when none of its ids resolved, empty without one) and `warm_translated` (#3556: how many ids of the previous selection named a planned room a tiled replan has since re-tiled under a new id and were pointed at the current room of the same role on its ground before the search, `Rules.WarmStartOf`), `work_shortfall` (an unassessed labor estimate carries `{unassessed: [reason, ...]}` and leaves `pawn_values` null; otherwise #3825: the work the selection left unscheduled, per `<work type>/<stat>` its ticks a day at speed 1; null when tier 2 did not run) and `pawn_values` (#3825: per colonist `per_day`, its value in capability-days a day, its parts `work`, `food` (of which `food_haul` is hauling it, #3849), `breaks` and `bed`, `shares` (the shortfall it takes on, per `<work type>/<stat>` its ticks a day) and `not_modelled`; null when tier 2 did not run or the values await a plan; [pawn value](../architecture/forecast-value.md#pawn-value)), `pawn_values_awaiting_plan` (#3857: `layout_plan` when tier 2 ran but the colony has no layout plan yet, so no colonist was valued; null otherwise), `laps_changed`, `laps_same` and `laps_compare_ms` (#3564; from the second review of a world: per review lap after `observe`, the properties of the reading it entered on, the one the lap before it left, that differ from the previous review's, as the projection's property names and `Facts.<name>`, at most 24 then `+n`; a lap absent from `laps_changed` entered on an identical reading and is named in `laps_same`; a property whose type has no value equality, such as a catalog table the review rebuilds, is not compared: `LapInputs.Uncompared` names them), `concerns`, `emergency`, the food attrs of `reviewFoodAttrs`, `hunt_sources`, `hunt_holds` (each held hunt row as `id:reason`), `hunt_read` (the native hunt read: known, hunter and prey counts, findings) and `hunt_choice` (the roster chosen, why the others were unusable, the comparison's findings, and `released:<reason>` when the review released a held formation). Reader: `food/hunt-roster` (`TelemetryKinds.ReviewStep`, keys in `ReviewTelemetry`: `hunt_choice`, `hunt_read` and the chosen roster's `hunt_*` terms on its `food_plan` line) |
| `colony_model` | event row, once per colony review that has coupled candidates, emitted by the ColonyReview before the comparison. The tier-2 colony model's run (`ReviewColonyModel.Review`, #3531): `horizon_days`, `events` (the engine's budget counts these), `truncated`, `runway_day` (absent when nutrition does not fall to the harvest-gap stock within the run), `value` (the run's capability-days, absent while unassessed), `nutrition_end`, `completions` (each project or harvest finished, as `day:id`), `arrivals` (#3622: each resource entering the run's ledger from a harvest, hunt or craft, as `day:node:resource=count`), `dvdt` (capability-days an hour of each research and sowing node's work adds, as `node=value` joined by `;`), `skill_per_xp` (#3663: capability-days one XP of each priced skill stock adds, as `skill/<pawn>/<skill>=value`), `findings` (the run's gaps, the value's unassessed parts and the input sections it could not start from as `input:<section>`) and `elapsed_ms` (wall time of the baseline run and one run per valued project), and `goals` (#3505: each unmet goal as `goal=day:N`, `goal=open` when its terminal completes in no run, or `goal=unmodelled:<reason>`). Readers: the `goals/research-goal` and `production/skill-marginal` acceptance cases (`TelemetryKinds.ColonyModel`, keys in `ColonyModelTelemetry`) |
| `coupled_selection.predicted_tasks.productive_learning` (nested attrs, same decision kind) | Conditional productive learning (recipes, research and sowing): `skill`, `observed_rate_work_ticks`, `conditional_work_ticks`, total callback `raw_xp`, projected `before_level`/`after_level`, cumulative `applied_gain`/`decay_loss`, native `provenance`. Absent mechanics are also named in selection uncertainty. Reader: OTLP timeline; `production/productive-cooking` separately observes native learning and output without requiring an exact Method. Native callbacks/output, not these predictions, establish progress. |
| `coupled_selection.handoff` (nested attrs, same decision kind) | Compact selected-plan terminal coverage: review-relative `day`, common `end_day`, `available`, named `gaps`, known nutrition/stock/hold/medicine coverage, need counts, completed project dates, graph nodes, active outfits, completed shelters, retained shelter mechanics and closed rooms. No per-candidate trajectory. Typed reader: `ForecastHandoffReader`; `forecastresiduals` includes decoded `Handoffs` in its retained-window report. |
| `coupled_selection.service_investments` (nested attrs, same decision kind) | Derived food/shelter investment explanations: existing Concern `owner` and `predicate`, `horizon_ticks`, `beneficiaries`, physical `tasks`, `metric` (`food_threshold_ticks` or `stationary_thermal_degree_ticks`), `avoided_exposure`, and `retirement`. No second goal or material ledger. Readers: OTLP timeline and Method-correlated `shelter/service-investment`; native completion remains separate. |
| `coupled_selection.baseline_thermal_exposure` / `selected_thermal_exposure` (nested attrs, same decision kind) | Compact physiological inputs, one row per pawn: `pawn`, `assessed_ticks`, `unknown_ticks`, `cold_degree_ticks`, first-span `initial_temperature` and `initial_comfortable_min`, last-span `final_temperature`. Empty spans leave endpoints null; endpoints do not describe intermediate temperature or apparel changes. Reader: `coordination/construction-delegated` through `CoupledSelections.Of`, comparing native comfort minima with the inputs and asserting reduced positive `value.thermal` (hypothermia/heatstroke capability loss). `value.harm` is raid capability loss. |
| `coupled_selection` (decision) | Normal shared portfolio comparison. Target Method ID (or `continue`), verdict `selected`/`continue`/`deferred`, reason `shared_consequences`/`best_found_bounded`/`protected_method_ownership`/`method_not_admitted`. A refused proposal records `admission_failure` after journal admission rejects it; refusal does not revoke authority or interrupt installed work. A native-operation winner refused its intervention justification records target `intervention_not_justified` with the refusal as reason (`native_service_response_without_intervention_premise`, `premise_unassessed:<reason>`, `no_modeled_service_rescue`, `forecast_benefit_without_premise`, `marginal_gain_without_premise`, `settings_response_supported`; [justification](forecast-contracts.md#current-native-need-response)). Attrs `justification` (null when no gate ran: `verdict` `justified`/`refused`, `reason`, `premise` with `kind` `observed_shortfall`/`credible_risk`/`capability_opportunity`/`unassessed`/`none` and its fields, `response` `supported`/`known_too_late`/`unassessed` with `response_basis`, comparator `stage`, `displaced` count with `displaced_service`/`displaced_work`/`displaced_unread` pawns, `uncertainty`), `observed_tick`, `horizon_ticks`, `selected_ids`, `baseline`, `selected`, `rejected` consequence vectors, `beneficiaries` (need and mood consequences), `protected_continuation`, `uncertainty`, `predicted_tasks`, `planned_rooms` (per planned room with a site finished inside the horizon: `room`, `completed_sites`, `complete` alternatives; #3276), `composition_expansions`, `search_expansions`, `search_evaluations`, `baseline_expansions`, `baseline_evaluations`, `evaluation_budget`, `traversal_bound` (the search's accounting, #3722: as `coupled_search_expansions`, `coupled_search_evaluations`, `coupled_baseline_expansions`, `coupled_baseline_evaluations`, `coupled_evaluation_budget` and `coupled_traversal_bound` on `rounds_review`; read back by `CoupledSelections.Row` as `Search`), `search_phases`, `selected_phase`, `warm_start` (as on `rounds_review`; #3563), `search_pruned` (the subsets a proven bound skipped, summed over the phases) and `bounds_unavailable` (the distinct `urgent_zero_unserved:<reason>` reasons a bound was unavailable, each occurrence counted `unbounded` in its phase's `search_profile` on `rounds_review`; #3724; read back by `CoupledSelections.Row` as `Search.Pruned` and `Search.BoundsUnavailable`), generation/search/explanation truncation (`search_truncated`, with `outcome_evaluations_exhausted` or `traversal_bound_exhausted` in `uncertainty` when an allowance ended the search). A selection whose only new work is planned sites is compared again without them for its orders (finding `planned_unplaced_reselected`, #3557); one that then starts no order records target `planned_unplaced`, followed by its `planned_room_selection` companion (#3828); the room reconcile places the selected sites ([planned rooms](../architecture/portfolio-selection.md#planned-rooms)), and refuses an assessed unselected room with an `AwaitingPlan` verdict, subject `portfolio_selection`, and an unassessed unstarted ringed room with subject `portfolio_unassessed` (#3754). Each `rejected` row carries `ids`, `reason` (`infeasible_or_unassessed`, `construction_without_supported_service_relief:<ids>`, or `lost_to_leader:<stage>` naming the comparator stage that decided it: `urgent_zero`, `death_risk_floor`, `break_risk_cap`, `ledger`, an unconverted term, `incumbent`, `labor`; #3723; or `pruned:<bound>:<pawn>` and `pruned_branch:<bound>:<pawn>` for a subset or a cardinality-sweep prefix the proven bound `urgent_zero_unserved` skipped before its schedule was searched, naming the pawn it leaves starving past the leader; #3724) and `vector`, null for an infeasible subset, for one whose ledger was never priced (decided at a floor, or held by the relief gate before any comparison) and for a pruned one. Each vector (`baseline`, `selected`, every `rejected` row with one) carries a nested `value` attr (#3522, [comparator](../architecture/forecast-value.md#comparator)): what the outcome was ranked by. `total`, `hunger`, `rest`, `raw_ingredients` (meal nutrition forgone by eating cookable ingredients raw, #3626), `food_poisoning`, `thermal`, `mood_breaks`, `wellbeing`, `harm`, `investment` (tier-2 credit of project progress and applied skill XP, less medicine consumed, with `investment_contributions` rows `activity`, `pawn`, `key`, `units`, `value`; #3636; a closed planned bedroom's credit is a row with an empty `activity` under `bedroom/<room>/<pawn>`, #3655; a kitchen's under `kitchen/<room>/food_poisoning` and a butchery's under `butchery/<room>/<kitchen>`, #3802), `goals` (negative: the player's goals the outcome progresses, with `goal_contributions` rows `goal`, `progress`, `worth`, `value`; #3503) are capability-days lost (lower is better; null when unassessed, and a comparison uses only the terms both sides assess); `harm_detail` (#3546; null while `harm` is) gives `raids_per_day`, `exposure_range` (`[lo, hi]` hits per colonist per raid over the pawns), `down_probability` and `death_probability` (expected pawns downed and dead per raid, summed over the colony) and `cvar` (the summed per-pawn CVaR of one raid's loss), `raid_loss` (the raids' part of `harm`) and `illness` (#3824; null while unassessed: `incidents_per_day`, `loss`, `patient_days_per_day`, `tends_per_day`, `deaths_per_day`, and those three per pawn in `pawns` rows with `pawn`) `escape` (#3811: the held entities' `escapes_per_day`, their `loss` and `deaths_per_day`, zeros without a held entity) and `creepjoiner` (#3844: the unshown downsides' `loss` and `deaths_per_day`, and per creepjoiner in `pawns` its `pawn` and the `agony`, `aggressive`, `leaves` and `hediffs` parts of its loss; zeros and no rows without one); `unknown` lists the ledger's findings; `floors` holds `urgent_zero`, `break_risk_cap` and `death_risk_floor`, each with `assessed`, `passed` and `excess` (`death_risk_floor` is assessed when the outcome's and the baseline's harm are); `policy` lists the constants used. Selection `uncertainty` names `harm_capability_unassessed` (with the harm term's specific finding: `threat_schedule_unknown`, `raid_composition_unknown`, `exposure_cell_uncalibrated:<bin>/<role>`, `outfit_unobserved:<pawn>`) only while the chosen outcome's harm is unknown, `death_floor_unassessed` only while its death floor is, and `break_floor_unassessed`. Vectors also expose support first-care latency, pending care batches, unsupplied demand, production/care identities, conditional room effects and tend quality; see [support evidence](support-work.md#execution-and-evidence). Rejected explanations cap at 16 to retain selection and observation keys within the telemetry payload limit; omitted explanations set `explanation_truncated`. Readers: OTLP log timeline, prepared `production/coupled-needs`, `production/development-lifecycle` and `production/devilstrand-investment-starving` (`TelemetryKinds.CoupledSelection` through `CoupledSelections.Of`: the selected tasks and the urgent-zero floor of the baseline and the selection) and `apparel/crafted_armor` (through `CoupledSelections.Of`: the selected ids, whose clothing alternatives name their stuff, and the harm of the baseline and the selection; #3621) and `sleeping/bedroom-waits-for-value` (through `CoupledSelections.Of`: the investment rows and goals of the selected and rejected vectors; #3655) and `layout/barracks-splits` (through `CoupledSelections.Of`: a selected planned bedroom's root and its `bedroom/` credit; #3661). Predictions are not completion evidence. |
| `planned_room_selection` (decision) | The compact companion of a `coupled_selection` row with target `planned_unplaced` (#3828), whose vectors can pass the 64 KiB payload cap and keep only the correlation keys. Target `planned_unplaced`, verdict `selected`, reason `shared_consequences`/`best_found_bounded`. Its ids and findings spend at most `ColonyReview.PlannedRoomSelectionBudget` (48 KiB) of characters, so the row is never truncated. Attrs `observed_tick`, `selected_ids`, `planned_rooms` (as on `coupled_selection`), `rooms` (per planned room with a valued alternative or a root among the candidates: `room`, `assessed` (it has a valued room alternative), `alternatives` (their ids), `roots` (the candidate roots whose completions reach its planned sites), `selected`), `baseline` and `selected` (`total` and `investment` of the ledger), `uncertainty` (the selection findings naming `room` or `planned`, then the truncations) with `uncertainty_omitted`, `rejected` (each distinct subset and reason once: the earliest touching each room first, then the rest in search order; `ids`, `reason`, and the ledger `total` when priced) with `rejected_distinct` and `rejected_omitted` (left out by the budget), `generation_truncated`, `search_truncated`. Reader: OTLP log timeline (`TelemetryKinds.PlannedRoomSelection`); `PlannedRoomSelectionTelemetryTests` emits a capped selection through the production emitter and reads both rows back. |
| `coupled_observed` (decision) | Existing Method continuation evidence. Target Method ID, verdict continue/transition after admission, or deferred with reason work_resource_conflict_or_unknown when shared resources cannot admit the productive continuation; ordinary evidence uses reason native_task_evidence; attrs `observed_tick`, `satisfied_tasks`, `remaining_tasks`, `native_states`. Readers: OTLP log timeline, Method-correlated `production/coupled-needs` and prepared support cases. Native completion and subsequent physical need changes remain separate from accepted orders. A worker read `completed` while it has a recipe left is a finished task: verdict deferred, reason `awaiting_task_review`, attrs `observed_tick` and `finished` (the task keys), and the woken scheduler step runs the colony review (`rounds` `task` on `clock_step`, #3624) before the Method continues. |
| `gear_search` (decision) | `verdict` `ok`, `reason` `best_found_within_budget`, `target` the pawn, level `WARN`; attr `budget_nodes` (`policy.GearSearchBudget`). One per pawn per colony review whose loadout search spent its node budget and kept the best ensemble found. Reader: the launcher Observe tab's Problems and Log views (every WARN) and `rimgovernor log` |
| `tech_tier` | event row. `tier`, `evidence` |
| `layout_plan` (decision) | target `plan`: `verdict` `planned` (a plan derived; a colony's first plan is derived at the end of the review, after the ledger and the resource demand, and holds the clock until it is, #3652) or `replanned` (the tiled replan changed the plan; `reason` the triggers that fired, comma-joined: `daily`, `pawns`, `research`, `terrain`, or empty for another key drift), attrs `colonists`, `summary`; `skipped`/`no_core` when the survey holds no core (an unread fact writes the chain's `required_fact` row instead); `failed`/`chain_error` (attr `error`). Target `perimeter` (#2922): one row per defense node PlanPerimeter traced, when a plan is derived or a replan changes the ground (attr `node`): `place`/`ring_traced` the wall ring, `place`/`opening_most_routes` the killbox opening where the most raider routes cross the ring (a replan keeps it, and the reason), `unplaceable`/`opening_none` no route reached a ring position that opens. Target `perimeter_cut` (#2973, advisory): rows for the routing cut `PlanPerimeterCut` advises, on a derive or a perimeter-changing replan (attr `colonists`): `place`/`cut_traced` every route but the killbox opening is closed (attrs `cut` cells, `walls` cells thickened, `unclosable`, `cost`), `unplaceable`/`cut_unclosable` the cut crosses ground nothing can wall or a source touches the base (attr `why`); then one row per segment, `place` with reason `thickness_sapper_detour`, `thickness_capped_at_budget`, `thickness_no_route_to_opening` or `thickness_hp_unknown` (attrs `segment`, `cells`, `thickness`, `detour_ticks`, `hp`). Target `defense_tiers` (#2973, advisory): one row per defense tier node `PlanDefenseTiers` cut from the defense round's census, when the cut changes (attrs `node`, `nodes`): `place`/`tier_sited` the tier is sited from the census, `place`/`tier_pinned` it is built and stays where it stands, `place`/`tier_displaced_by_built` a tier still to build gave up a cell a built tier holds, `unplaceable`/`tier_layout_failed` (attr `why`) the census holds no corridor (only built tiers stand as nodes). Target `defense_tier_block` (#2973, advisory): one row per pinned tier a tree, chunk or new building blocks, when the price changes (attrs `tier`, `blocked`, `clear_w`, `clear_known`, `move_w`, `move_known`, `downtime` `unpriced`): `clear`/`tier_clear_cheaper` removing the blocker (work from the defs at the work-day price, net of yield) costs no more than moving, `move`/`tier_move_cheaper_downtime_unpriced` moving is cheaper (a lower bound: the downtime is not priced), `unpriced`/`tier_block_unpriced` neither could be priced. |
| `layout_interior` (decision) | the interior search (`SearchInterior`) of a derive or replan, component `layout`, target `interior`: one row per kept move, `verdict` `place`, `reason` `interior_search_move`; attrs `group`, `move` (slot:kind:terms improved). Written by `logInterior` after the `layout_stations` row (derive) or the `layout_replan_tiled` row (replan). |
| `layout_conduit` (decision) | the power conduit net's decisions (`TileResult.ConduitDecisions`, #2966, #3036), component `layout`, one row per node `LogConduits` journals after a derive's `layout_stations` row and a replan's `layout_replan_tiled` row. `target` the node routed (`room:<id>` or `site:<node>`); attr `to` the node it joined (empty when it found no net). `place`/`conduit_kept` the held link still serves it, `place`/`conduit_routed` it routed anew, `unsatisfied`/`conduit_no_route` no path reaches the net, `unsatisfied`/`conduit_no_source` the plan has no power site. Reader: `rimgovernor log` |
| `layout_edit` (decision) | `target` zone, role, item or building, `verdict` `admitted`/`refused`/`abandoned`/`closed`/`proposed`, `reason` refusal code or detail; attrs `family` (`stockpile`, `field`, `building`), `kind`, `role` (stockpile create/delete),  `plan`, `crop`, `cells`, `moves`, `gain`, `owner`, `x`, `z`. Reader: `acceptance colony`'s stockpile audit (`TelemetryKinds.LayoutEdit`, admitted stockpile creates and deletes) |
| `layout_regroup` (decision) | the regrouping search (`policy.Regroup`, #2878), component `layout`, target `groups`, written with each `layout_stations` run (skipped, and no rows, while its inputs are unchanged). One row per move applied: `verdict` `applied`, `reason` `regroup_merge_total_falls`, `regroup_split_conflict_priced`, `regroup_split_role_guard`, `regroup_move_need_total_falls`, `regroup_filth_merge_walk_beats_poison` or `regroup_bed_station_merge_walk_beats_loss` (#3750); attrs `move` (`merge`/`split`/`move`), `a`, `b` (the nodes of the pair rule), `before`, `after` (the priced total in score points, lower is better), `walk`, `poison` and `clean` (the move's change of the embedding score, the food-poisoning price it adds and the change of the cleaning-labor price, in the same points; `clean` is zero for a filth merge because both groupings pay the same cleaning labor, #2993) `lost_effects` and `effects` (the sorted names of the lost effects of the room the pair shares after the move, such as `sleep_disturbed` and `impressiveness`, #3750). A butcher kept in its closet because the poisoning outweighs the walk saved is a `kept` row, `regroup_filth_merge_poison_beats_walk`, and beds kept apart from a kitchen, dining, butchery or freezer station because the sleepers' loss outweighs the walk saved a `kept` row `regroup_bed_station_merge_loss_beats_walk`, each with the same attrs. With no move applied one row: `kept`/`regroup_no_move_improves`, or `skipped`/`regroup_work_price_unknown` while the work-day price is unknown. Reader: `rimgovernor log` |
| `layout_progress` | event row, once per colony review that holds a layout plan, emitted by the ColonyReview beside the colony model (#3751; constants in `LayoutProgressTelemetry`, kind `TelemetryKinds.LayoutProgress`): `planned` (the held plan's rooms), `started` (rooms with anything of ours on them, done ones included), `built` (rooms done by their latest reconcile, `ColonyReview.DoneRooms`), `open` (the open rooms' ids: started and not done, the rings the ring-closure projection closes first, #3744, #3754) and `builders` (the colonists the coupled comparison plans construction for). Reader: colonyreview's layout section (planned, built and open rings per day; the `layout_open_rings` threshold, open <= max(1, builders)) |
| `layout_replan_tiled` (decision) | the replan (`ColonyReview.replanLayout`, `policy.ReplanLayout`, #3005, J4 of #2965): the tiled plan is the next plan. Component `layout`, target `plan`, one row per replan, `dur_ms` the run's wall time (the catalog rows, `ReplanFacts`, the chain and the tiler). The run is off the review's hold path (#3565): the hourly check that finds the inputs drifted starts it on a worker, the review that finds it finished adopts it (attr `adopted`, the ticks between), and no second replan starts while one runs. `verdict` `replanned`/`reason` `tiled_plan`, or, when the held plan had unbuilt rooms a group could stand on (#3740, `EmbedRun.HoldPrior`), the hysteresis verdict: `kept`/`replan_gain_below` (the new embedding gains less than `ReplanGain` over the held arrangement, so every unbuilt room that still fits keeps its site and id), `kept`/`replan_held_fails_fewer`, `replanned`/`replan_gain_above` or `replanned`/`replan_held_fails_more`, with attrs `held_score` and `tiled_score` (the two arrangements' embedding objectives, cost, lower is better), `held_rooms` (groups stood on a held room's site) and `replan_gain`; attrs `changed` (the plan was recorded), `inputs_changed` (the replan inputs that drifted since the last replan, from `LayoutInputs.Changed`: `plan_tick`, `survey` (the terrain view, #3739), `grown`, `pawns`, `geysers`, `growth`, `load` or a `StationFacts.InputsKeyParts` name such as `population`, `demand`, `plan`, `built`, `pinned`; `daily` alone when nothing drifted and `LayoutPlanning.ReplanDaily` passed since the last replan), `same`, `moved`, `new`, `dropped` (room counts of the tiled plan against the plan the colony held), `rooms` (each `room` the old plan's id, 0 for a new one, `tiled` the tiled plan's id, `role`, `verdict`, `reason`, `built`, `id` on a `tile_sited_again` room (`kept_id` when the tiled room kept the held room's id, its interior unchanged, `new_id` otherwise), and `short` when the room's group needs more floor than its standing rooms give, #3004; `started` (#3751) whether the held room had started, from the growth census (`RoomGrowth.Fixed`), absent while that census is unknown), `unmatched_built` (each standing room the chain has no node to pin: `room`, `role`, `reason` `tile_outdoor`, `tile_no_native_role` (both skipped by `BuiltRegions` unless the room is a room node's, #3003), `tile_not_a_block` or `tile_no_node`), `built` (standing rooms) and `tiled_rooms`. A room `reason` is `tile_kept_built` (same), `tile_successor` (moved), `tile_sited_again` (an unbuilt room matched by role), `tile_unpinned_built` (an unmatched standing room the tiler builds its own room of), `tile_superseded` (dropped, its standing successor is the room), `tile_built_retired_by_old` (new: a tiled block continues a standing or replaced room the plan no longer holds), `tile_no_old_room` (new) or an unmatched reason (dropped). `skipped`: `station_advice_unavailable` (the run's chain made no advice; its `layout_stations` row says why), `superseded` (the world or the saved plan changed under the run; attrs `started_tick`, `adopted`) or `room_held` (#3537: an open journal plan took up rooms the run did not hold, such as a shell admitted in the review that started it, and the run may have moved them; attrs `started_tick`, `adopted`, `held` the room ids). `failed`/`tile_error`, `WARN`, attr `error` (the chain or the tiler could not plan; the colony keeps its plan and the next trigger tries again); a fact the tiler needs left unread writes the `required_fact` row (target `layout_replan_tiled`) instead. Readers: `rimgovernor log` and `layout/first-bedroom-closes` (`TelemetryKinds.LayoutReplanTiled`: no `rooms` entry `tile_sited_again` names a room whose ring had started, #3752) and colonyreview's layout section (replans per day, re-sited rooms split by `started`) |
| `layout_role_guard` (decision) | `verdict` `mismatch`, `reason` `native_role_differs_from_predicted`, `target` the planned room id; attrs `native` (the census role), `predicted` (PredictRole of the room's observed stations), `scores` (role to score). One row per standing planned room whose native role differs, on each rounds building step that reads the plan, rooms, construction and sleeping census; a room with an unknown bed state or an ideo altar is not compared. The regrouping reads the rows as a cost input and a split move (`layout_regroup`). A guard error is `failed`/`guard_error`, `WARN`, attr `error`. |
| `layout_stations` (decision) | the station chain, one row per replan's chain run, written when the replan is adopted (#3565; the chain runs only inside a replan, so an hourly check whose inputs are unchanged writes none), component `layout`, target `plan`, `dur_ms` the chain's wall time. `verdict` `advised`/`reason` `station_chain`: attrs `nodes`, `edges`, `groups` (each `group`, `role` predicted, `stations` {node, count}, `requires`, `footprint`, `clearance`, `area`, `cost_ticks_per_day`, `lost_effects`, `rooms` matched built room ids, `built_area`, `outgrown`, `climate` {cells, max_c, best, options {opening, mean_w, peak_w, heating_w, cooling_w, build_w, total_w}} on a cold group when the curve is known, `block` when placed), `conflicts` ({a, b, conflict}), `bed_merge` (the free-bed pool's priced barracks size, #3658: {node, verdict `merge`/`split`, reason `cluster_bed_merge_priced`; an unread price fact skips the chain with the `required_fact` row instead, #3747}), `embed` ({level, placed, issues, built {group, room, verdict `keep`/`move`, reason `embed_built_*`}, cells, total, soil, edge, centre, walk, unmet_links, switch, enclosure (the rooms' union wall ring and footprint, negative, in `total`, #3746), seed}), `sites` (each power site `node`, `x`, `z`, `anchored`, and for a turbine `rotation`, `blocked` wind-path cells, `output` the fraction of rated output left), `site_issues` ({node, verdict, reason}), `vents` (the tiled plan's vent sites {x, z, rotation, reason}) and `vent_issues` ({group, verdict, reason} for a `tile_vent*` decision that placed none), `dropped` ({group, verdict `dropped`, reason `tile_unfit`, why} for a room the tiler left out because the placer could not furnish it behind its doors, #3745; the group stays owed and the next replan tries it again) and `comfort` (the advised comfort tier of the sleep node and each worked bench, {node, def, stuff, comfort, priced, gain, benefit_w, cost_w}; empty while the mood or comfort model or the colonists' mood is unknown, `priced` false for a node that could not be priced). An unread fact writes the `required_fact` row (target `layout_stations`) instead. `failed`/`chain_error`, `WARN`, attr `error`. A derive writes the same row with attr `derive` true; `embed` also carries `fields` (each field's placed rectangle). The replan tiles this advice (`layout_replan_tiled`). Reader: `rimgovernor log` |
| `snapshot_skip` | decision, `WARN`. `target` the snapshot (`colony`, `combat`, `defense`, `layout`, `firebreak`, `shelter`, `section`, `step`, `planner`), `verdict` `skipped`, `reason` `not_recorded`; attrs `error`, `tick` |
| `goal_review` (decision) | the goals family's review ([goals contracts](goals-contracts.md)), component `goals`: one row per player goal per review. `target` the goal id, `verdict` the state (`met`, `pursuing`, `blocked`, `infeasible`, `no_method`), `reason` the owning Need's blocked reason, the broken floor (`urgent_zero` or `break_risk_cap`) of an `infeasible` goal, `no_pursuit`, `not fluid` or `missing_dlc` (empty while met or pursuing); attrs `kind`, `need` (the goal's NeedId), `owner` (the Need that pursues it, empty when none), `deficit` (in the kind's unit, absent while unread), `eta_day` (the tier-2 estimated completion day, #3505; absent when unmodelled or met) and, for a goal in a theme, `theme`, `theme_met`, `theme_total`. Readers: `rimgovernor log` and the goal acceptance cases `goals/research-goal` and `goals/theme-reform` (`TelemetryKinds.GoalReview` through `GoalReviews.Of`) |
| `routine_skip` (decision) | `target` the subject, `verdict` `skipped`/`waiting`, `reason`; attrs `quest`, `script`, `detail`, `findings` |
| `placement_refused`, `foreign_held` | event rows, component `building-planner` (`RoomReconcile.cs`). `msg` only: `furniture cell refused: <def>@<x>,<z> blocked by <thing, ...>` (`ring cell refused:` for a wall or door cell) (the native preview's blockers, `unreported` when it names none) and `foreign thing held: <def>@<x>,<z>:<reason>` (`casket`, `ancient_danger`, `not_deconstructible`). The same text is the room's wait key (`<room>_reconcile:blocked:...`, `<room>_reconcile:held:...`). Read by the shelter climate cases (`ServiceLogs.LogMessages`) |

### Defense, population and pawns

| Kind | Shape and fields |
|---|---|
| `defense_action` (decision) | `target` pawn, plan, incident, cell or tier, `verdict` `applied`/`refused`/`failed`/`waiting`, `reason` the refusal or issue; attrs `incident`, `plan`, `outcome`, `x`, `z`, `error`. An unbuilt tier emits `waiting` / `tier_unbuilt` when its census changes, targeting the tier with attrs `buildings`, `census`. A turret, mortar or IED tier cut short by a gate emits `refused` with the gate as `reason` (`turret_unavailable`, `turret_threat_budget`, `turret_power`, `turret_stock`, `turret_no_conduit_route`, `mortar_*`, `ied_*`; see [defense tier counts](spatial-contracts.md#defense-tier-counts)), targeting the tier. |

### Other event families

| Kind | Shape and fields |
|---|---|
| `http_access` | event row, one per HTTP request. `method`, `path`, `status`, `dur_ms`, `bytes`, `long_lived`; 2xx GETs of `/api/state`, `/api/routines`, `/api/health` and `/api/presentation/*` under 250 ms are dropped; errors, slow calls and every non-GET are kept. Pprof rows are written at finish with `long_lived: true` (`dur_ms` is the capture, not latency). Written by `httpapi.Config.Access` (the brain) or `HttpChannelServer` (C#, [HTTP channel](../architecture/http-channel.md)); component `httpapi`; level `WARN` for a 5xx |
| `fields_select` | decision, one per farm site-type selection (`FieldPlanner`). `target` the winning crop, `verdict` `selected`, `reason` the winning site kind (`outdoor`, `greenhouse-new`, `greenhouse-reuse`, `hydroponics`); attrs `cells`, `buildings`, `needed`, `urgent`, `candidates` (each `kind`, `crop`, `needed`, `cells`, `score`, `terms` {name: value}, `reason` for an unplantable one). Read by the `Farm` acceptance cases. Component `clock-scheduler` |
| `recovery_batch` | decision, one per home-upkeep family read with queued recovery removals (`HomeUpkeepReader`); it journals the batch the planner executes. `target` `clearance`, `verdict` `planned`/`held`/`failed`, `reason` the stage (`roof`, `removal`, `none`, or `roof_rules` for a failed row); attrs `targets`, `roof_cells`, `batch` (load ids), `held` (each `id`, `reason` a `RemoteHoldReason` word), `blocker` (the first joint roof-check word), `error`. Component `clock-scheduler` |
| `food_credit` | decision, one per ledger counter group when the credit factor moves by 0.1 or more or the group's state changes. `target` the group (`crop:<zone>`, `fish:<x,z>`, `forage:<def>`, `animal_product:<race>`), `verdict` `credited`, `reason` `window` (the trailing window moved it) or `rebaseline` (a load held it); attrs `expected`, `observed` (nutrition over the window), `factor`, `window_days`, `state` (`designated`, `delivering`). Component `routine`. Written by the food review (`FoodPlan.cs`); `logview` prints the factor and state |
| `build_stalled` | decision, level `WARN`, component `building-planner` (`RoomStall.cs`): a room whose blueprints or frames have stood unfinished for `roomBuildStallTicks` (30000), repeated every window while it lasts. `target` the room, `verdict` `waiting`, `reason` the most common census reason (`awaiting_materials`, `no_eligible_builder`, a native `FinishingBlocker` word, `unreported`); attrs `blueprints`, `frames`, `reasons` {reason: count}, `tiers` {tier: count}, `eligible_finishers`, `max_min_skill`, `stalled_ticks`, `sites` (up to 8 `<def>@<x>,<z>`), `obstructions` (`<op>:<def>(<count>)@<x>,<z>` for pending claim, cut and haul targets; with no site standing `reason` is `obstructed`). The room wait key meanwhile reads `<room>_reconcile:obstructed:<first obstruction>` or `<room>_reconcile:awaiting_build:<n>_blueprints,<n>_frames:<reason>`. Reader: `rimgovernor log` (`logview.Entry.Words`) and the launcher Log panel (WARN) |
| `telemetry_drop` | event row, level `WARN`, component `otel`: the OTEL pipeline lost items (at most one per pipeline per 10 s). `signal` (`trace`/`log`/`metric`), `sink` (`file`/`collector`), `reason` (`queue_full`/`export_failed`), `dropped_since_last_warning`. The cumulative count is the `rimgovernor.telemetry.dropped` metric ([OTEL core](otel-core.md)). Reader: the launcher Observe tab's Problems and Log views (every WARN) and `rimgovernor log` |
| `log_overflow` | event row, level `WARN`: one summary of dropped diagnostics. `dropped`, `side` `mod` (the mod's ring overflowed while nothing was subscribed or the publisher lagged; `seq` is where the lines were) or `controller` (the reader goroutine's hand-off queue of 1024 was full) |

### Session and reader events

| Kind | Notes |
|---|---|
| `state_reset` | event row, level `WARN`, component `brain-host`: the state database came from another schema version and was moved aside (`path`, `aside`). Written once at startup. |
| `control_failed` | event row, level `WARN`, component of the player host: a player Resume or Pause failed (`request`, `kind`, `phase` the journaled record's phase or `""` when none began, `error` the exception with its stack; a resume names the step that threw, `resume <step>: ...`). The route's 503 detail carries the same message. Reader: diagnosis of a 503 control reply (#3549). |

The `planner_step` reader also accepts `worker_step` with `failed`/`error`
and an `error` attribute for background cycle faults, and `layout_overlay` with
`failed`/`output_error` for optional drawing failures. Both are visible through
`rimgovernor log`; a drawing failure never marks the overlay delivered. Expected
lifetime cancellation emits neither failure. `http_access` status 499 with zero
bytes means the client disconnected before the response was delivered, rather
than the response's planned status and size. `snapshot_refresh_failed` is a host
warning read by `rimgovernor log`: a changed background refresh failure is emitted
once, and a successful read resets that suppression.

The OTLP log ring is the only log: the telemetry handler writes a row for a record that names a `kind`
and drops one that does not, there is no Debug level, and `telemetry.Event` takes the `kind` as a required
parameter so code logs through it, not `slog` directly. stderr carries only the startup banner, parse errors,
`the brain service:` failures, the clock fault-injection notice and the brain panics. A new emission adds its kind here with
a reader, or it is not added.

## Mapping

Every decision and event row is an OTEL log record in `logs.jsonl` and,
when the emitter runs under a recording span, an event on that span. The code is
`TelemetryEmitter` (`Telemetry/TelemetryEmitter.cs`): a `Decision`, every
host log call with a `kind`, and the direct emitters (`log_overflow`,
`http_access`, `ReadTally.Publish`) all reach it, so the kinds registry and the
consuming-reader rule above apply unchanged: a kind is the record's `eventName`.

This attribute schema is what log readers parse. Changing it is a contract change.

| OTEL field | Content |
| --- | --- |
| `eventName` | the row kind |
| `timeUnixNano` | the row's `at` |
| `severityNumber` / `severityText` | `INFO` 9, `WARN` 13, `ERROR` 17, from the row's `level` |
| `body` | a decision: `"<kind> <verdict> <reason> <target>"` (derived, never parse it); an event: its `msg`; absent when an event has none |
| `traceId`, `spanId` | the span the emitter ran under |
| `rimgovernor.kind`, `rimgovernor.component` | strings, as the row context |
| `rimgovernor.tick` | int, the last observed game tick; absent before one was observed |
| `rimgovernor.row` | `decision` or `event` |
| `rimgovernor.verdict`, `.reason`, `.target` | strings, decisions only, always present (possibly `""`) |
| `rimgovernor.dur_ms` | double, decisions only, `0` when untimed |
| `rimgovernor.attr.<key>` | one attribute per decision `attrs` key, or per event payload key except `msg`. Native types: string, bool, int, double (durations in ms); slices and maps as OTLP `arrayValue` and `kvlistValue`; errors as text. Absent for a null value |
| `rimgovernor.truncated`, `.original_bytes`, `.sha256`, `.preview` | payload cap: when the encoded fields exceed 64 KiB (`telemetry.PayloadCapBytes`) they are dropped, the record carries these four (a 4 KiB preview) and only the small correlation fields `request`, `tool`, `native_tool`, `category`, `timing`. The fixed decision attributes always survive |

A span event is named after the kind and carries the same attributes. Logs are
emitted whether or not the trace is sampled, so a decision stays searchable when
its span is dropped.

Vocabulary, the no-free-text rule for decisions and the collapse key
`(kind, component, verdict, reason, target)` are unchanged; collapsing stays a
reader operation, now over the `rimgovernor.*` attributes.

## Readers

The launcher's Observe tab (`tools/launcher/Observe.cs`, which tails `logs.jsonl`)
and acceptance diagnostics consume these kinds; `RimGovernor.cmd launch-phases`
reads the launch spans (see [Launch spans](#launch-spans)).
`bridge.IsWorkerStep` and `bridge.StepFields` centralize
clock/worker interpretation. Combat `clock_stop` rows carry `event`; native `clock_hold` rows
carry each hold's reason, evidence and latency legs. Acceptance readers use the case's OTLP log ring
(`<output>/otlp/logs.jsonl*`, `<output>/service-N/otlp/...` per relaunch):
`OtlpTimeline.Read` rebuilds the row shape these
helpers expect from the typed log reader (`OtlpReader.ReadLogs`, see
[OTEL core](otel-core.md)); `ServiceLogs.ReadLogs`, `LogTail` and
`LogMessages` sit on top. Cases that assert on a live game's rows (native's
clock and colony events, the pawn-activity rows) read the game's own ring in
its channel directory through `Harness.Telemetry` or `TypedSession.Telemetry`.
A null attribute is absent in a record, not present as nil. Postmortem evidence is `otlp/logs.jsonl.2#77`: the segment and its
1-based line (what `grep -n` prints).

## Retention

The host writes the three files into `RIMGOVERNOR_OTLP_DIR` (default
`<save-data>/RimGovernor/otlp`; an acceptance launch passes the game's channel
directory, and at case end the runner copies the case's slice of the log and
trace rings to `<output>/otlp`, see [remote acceptance](remote-acceptance.md)). Each signal is its own rotating
ring: `traces.jsonl` 16 MiB x 8, `logs.jsonl` 32 MiB x 16 (512 MiB),
`metrics.jsonl` 8 MiB x 4. When the active file would pass its segment size it
becomes `name.1`, older segments shift up and the one beyond the count is
deleted; retention is by count only. Under overload the sink drops items
and counts them (`telemetry_drop` row, `rimgovernor.telemetry.dropped`
metric) rather than blocking the brain. The acceptance and postmortem readers use them.
[OTEL core](otel-core.md) has the file layout and line format.

Add a kind only with its reader. Keep stable reason words and structured attrs
so filtering, repeat collapse and diagnostics do not depend on prose parsing.

## Launch spans

Every in-process run writes the game-side launch waterfall to `traces.jsonl`,
regardless of DevMode. The span listener exists only once the brain starts, so
the host mod and the bridge buffer each phase as plain data
(`RimGovernor.Host.Sdk.LaunchPhases`: id, parent id, name, UTC start and end,
262144 phases at most, grown on demand). `BrainBootstrap` replays them through
`LaunchSpans.Replay` (`Telemetry/LaunchSpans.cs`) right after
`BrainHost.StartAsync`, with the recorded start and end times. Scope
`.../launch`.

```text
launch                              root; earliest phase start → brain started
  launch.pre_mods                   process start → host mod constructor (inferred: nothing can be patched earlier)
  rimworld/<DeepProfiler label>     a RimWorld load stage, e.g. rimworld/LoadModXML(), rimworld/Resolve references.,
    rimworld/<label>                rimworld/Static constructor calls; one nesting level below it
  mod-constructor.total             the host mod constructor
    main-thread.initialize, harmony.startup-patches, startup.on-mod-constructed
  bridge-start.total                bridge services after play data loaded
    capabilities.initialize, logs.initialize, application.run-in-background, debug.playback-defaults
  brain.defs_wait                   brain bootstrap waiting for loaded Defs
  brain.routes_wait                 brain bootstrap waiting for the HTTP channel's routes
  brain.host_start                  BrainHost.StartAsync; ends when the root ends
```

RimWorld stages come from a Harmony prefix on `Verse.DeepProfiler.Start`/`End`
installed in the mod constructor, so stages already open then (`LoadAllPlayData`,
`Load all active mods.`, `CreateModClasses()`) are not seen. Only the first two
levels of the stages opened afterwards on non-pool threads are recorded; the
prefixes are removed once play data has loaded. RimWorld's per-def and per-patch
stages (`Register*WantsCrossRef*`, `<PatchOperation type> Worker`, and queued
def-hook lambdas such as `Verse.ThingDef -> Void <PostLoad>b__…()`; thousands
per boot) are skipped with everything nested in them, by label pattern, so a new
coarse stage still appears. Spans may overlap (host phases
run inside RimWorld stages). A phase whose parent was not recorded sits under
the root. Phases past the buffer are dropped and the count is logged as a
warning.

The acceptance harness and the launcher time their side in their own process
(`RimGovernor.Brain.Acceptance.LaunchTrace`) and post it to the brain over
`POST /api/telemetry/spans` ([player API](player-api.md)), which replays each
post as another `launch` tree in the same ring; every span carries
`rimgovernor.launch.source` (`harness` or `launcher`). The harness posts once at
the end of a session open (`Session.OpenAsync`, also when it fails), the
launcher after Play connects (after the load when Play loads a save) and after a
New colony generation.

```text
launch.prepare          harness: profile prepare and package hashes
launch.process_start    harness, launcher: the detached game launch (not run for a kept game)
launch.connect          harness: endpoint, /api/health and a first native read; launcher: endpoint health
launch.to_main_menu     harness: a reused game unloaded to the main menu; launcher: New colony on the running game
start.load              harness: a save or cached-start load; launcher: Play's save load
start.generate          harness: the new-colony op of a debug, lab or scenario start; launcher: New colony
start.first_action      harness: started world → the case body can act (pause, quiet world, fixture op, frozen needs)
```

Acceptance `result.json` carries `boot_phases`, name → ms, read at Finalize
from the game's `traces.jsonl` (`BootPhases.Read`): every non-root span of each
`launch` tree whose root starts at or after the report's `started_at`, so a kept
game's earlier boot is not counted. A repeated name sums. Phases overlap, so the
map does not add up to `boot_ms`. A ring that cannot be read leaves
`boot_phases_error`; spans the game did not take leave `launch_spans_error`.

`RimGovernor.cmd launch-phases [-json] [<run> [<run>]]` (`tools/rg`, model
`LaunchWaterfall`) prints one run's trees as a waterfall: one line per span,
each tree depth first with siblings in start order, with its offset from the
game's own `launch` root (process start; the harness's and launcher's earlier
phases are negative), its duration and a bar, then the total and the three
largest phases. A run is an OTLP directory or its `traces.jsonl` (every tree of
the latest root's run id, `service.instance.id`), or an acceptance case output,
its `result.json` or an output directory holding one (the trees rooted between
`started_at` and `finished_at`, read from `launch_dir`, the game's channel
directory, which Finalize records beside `boot_phases`). With no run it
reads the newer of the launcher's latest game (`.rimgovernor/bridge/config/<game>/`)
and the latest `.rimgovernor/ev*` acceptance output. A second run prints the
per-phase delta (phases summed by name as in `boot_phases`, the total first,
then by largest change). `-json` prints the same as JSON.

## Pawn activity

The [pawn activity contract](../architecture/pawn-activity.md) owns these event kinds:

| Kind | Payload | Reader |
| --- | --- | --- |
| pawn_activity_execution | pawn, assignment, method, revision, step, state, reason, job_id; authorization/offer/start/termination are distinct. State `released` is a Hands release, or native's own with reason `home_fire` ([home-area fire](pawn-activities.md#home-area-fire), #3672) | OTLP timeline; pawn observations ActivityExecution; control/pawn-activities; speed matrix `completed_tasks` (`SpeedMatrix.CompletedTasks`); `control/home-fire-release` (`PawnActivityTelemetry.HomeFireReason`) |
| pawn_activity_interval | sequence, world (colony/load/map), pawn, start_tick, end_tick, job, job_id, phase, activity, purpose, targets, start_location, end_location, end_reason, nullable method/assignment | RimGovernor.cmd pawnactivity; observations/pawn-activity |
| pawn_activity_output | colony, load, nullable map/pawn/job_id, source, output_kind, resource, units, tick; observed production-site output, not job success or storage delivery | OTLP timeline; observations/pawn-activity |
| pawn_activity_fault | error; subsequent observations stop and report as missing time | OTLP timeline; pawnactivity gaps |
| pawn_activity_capacity | refused_observations; open interval cap reached | OTLP timeline; pawnactivity gaps |

These events use the existing bounded telemetry queue and rotating logs. Interval
boundaries are non-overlapping and captured by native transition hooks; there is no periodic phase sampling. Open intervals export only when changed or closed. Lost or rotated records are unknown time, not zero work.

The existing `coupled_selection` vectors also carry `maintenance` rows: actual requirement identity, conditional native XP shortfall and findings. The OTLP timeline reads these attrs; native `production/maintenance-range` and `production/maintenance-sparring` correlate the selected Method with real skills and restoration.

The existing `coupled_selection` vectors label `support_projection` as
`conditional_scenario` or `native_unprojected`. Native vectors expose
`support_observed_demands` (id/kind/target) and `support_observed_deliveries`
(demand/bill/required), while future support fields are null and
`unassessed_terms` includes `native_support_response_unprojected`.
Conditional vectors retain their numeric service/delivery consequences. The
OTLP timeline reads these attributes; current demand, a selected what-if and
actual completion remain separate evidence.

The same vectors carry `native_food`: an explicit unassessed reason, no-consumers
state, or reviewed `runway_days`, `minimum_days`, `risk`, `needs_response`,
`recovery_timing`, nullable `conditional_recovery_days` and `reserve_release`.
These are current stock/reserve and conditional recovery evidence, not projected
native output. Conditional investment vectors name `conditional_food_scenario`
instead. The OTLP timeline reads these attrs through the existing vector payload.

The existing `coupled_selection` vectors withhold deprivation attributed solely
to omitting a captured native response. `uncertainty` names
`native_response_unprojected:<pawn>:<kind>`; affected need/mood/break values are
unassessed, rather than zero risk or predicted failure. `beneficiaries` retains
the conditional activity scenario used by detailed investment mechanics. Those
trajectories are what-if evidence, not future native intake or job completion.
The OTLP timeline and `CoupledSelections` retain their existing vector/uncertainty
readers; there is no new signal kind or emission path.

## Forecast residual evidence

`RimGovernor.cmd forecastresiduals -logs <absolute-path-to-logs.jsonl>` reads
retained prediction/observation records (including rotated segments) and emits
JSON with raw pairs, gaps and grouped residual summaries. The command is usable
with synthetic OTLP evidence; it does not require live instrumentation or change
planning. Historical evidence belongs to OTLP, with no save or journal additions.

| Kind | Reader and payload |
| --- | --- |
| `forecast_prediction` | `ForecastEvidenceReader`: one selected forecast sample, common keys below plus `origin_tick`, `target_tick`, `value`, `unknown_reason`, `admission`. |
| `forecast_observation` | `ForecastEvidenceReader`: later native evidence correlated to a sample, common keys plus `observation_tick`, `value`, `unknown_reason`, `execution`, `completion`, `lower_tick`, `upper_tick`, `provenance`. |

Both kinds are decision rows emitted through `TelemetryEmitter.Decide`; the following fields live in the decision `attrs` object. The reader rejects rows without that envelope.

Common keys are `run`, `load`, `map`, `review`, `sample`, `metric`, `subject`,
`scope`, `unit`, `method`, `task`, `revision`. The first five identify a sample.
Run/load/map must identify the actual observed timeline. `sample` is unique within
its review, not a new Method identity. Method/task/revision reuse execution
identities; empty method/task and revision zero denote an inapplicable execution
identity. Revision is encoded as decimal text to retain the entire native long
identity through generic JSON-number readers. The enum spelling below is exact.

- `metric`: `FoodStock`, `FoodLevel`, `RestLevel`, `Mood`,
  `ShelterTemperature`, `TaskCompletion`.
- `admission`: `Selected`, `Admitted`, `Continued`, `Refused`, `Counterfactual`.
- `execution`: `Unconfirmed`, `Consistent`, `Interrupted`, `Replaced`, `NotExecuted`.
- `completion`: `NotApplicable` for state metrics; `Exact`, `Interval`,
  `CompletedBy` (completed with no earlier unfinished read; `upper_tick` only),
  `Unfinished`, or `Unobserved` for tasks.

`value` is a finite number or absent/null with a nonempty `unknown_reason`.
Unknown never means zero. For a task, predicted/exact observed values are absolute
completion game ticks; `observation_tick` remains the tick of the read. Interval
completion uses `(lower_tick, upper_tick]`, not a midpoint. `provenance` names the
native evidence source; an admission receipt alone cannot establish completion.
Other metrics compare matching units and physical scope as specified in
[forecast contracts](forecast-contracts.md#residual-matching).

`ForecastResiduals` in Policy computes observed minus predicted. Endpoint state
values at different ticks retain the raw pair and lag, with `tick_mismatch` and no
scalar error. Exact native completion timestamps may be learned later; their
scalar error uses the timestamp, never the read tick. Interval residual bounds
remain intervals. A task prediction outside its supported origin/target horizon
is refused. Rejected counterfactuals never receive a score.

Summaries group by metric, unit, scope, food-resource identity, admission and
execution cohort. They include sample/matched/scalar/interval counts, signed mean,
MAE, population standard deviation, mean observation lag and reason counts.
Empty scalar groups have null statistics; one scalar has zero population
standard deviation. Refused admission always belongs to `NotExecuted`, even if
contradictory observation evidence claims consistency. Other divergence cohorts
retain operational residuals separately. `Consistent` means sufficient execution
evidence with no detected inconsistency, not causal isolation. Unchanged work
across a new review can remain consistent. Overlapping reviews are forecast
samples, not independent experiments.

The reader deduplicates identical predictions and observations; conflicting
predictions are gaps. Matching is independent of arrival order, chooses the
closest read (earlier on a tie), and prefers exact, then interval completion over
unfinished task reads. Conflicting observations at the chosen tick do not enter
statistics. Wrong timeline, geometry/census/unit, or execution identity remains
an explicit unmatched reason. Raw paired values and their unknown reasons are
always retained in the report.

The report identifies itself as `retained_log_window_only`. Missing files,
missing predictions/observations, truncated payloads, malformed records, torn
final lines and rotation races are visible gaps; duplicate records are reported.
A fully evicted prediction/observation pair cannot be reconstructed or counted.
An empty ring yields `no_predictions`, never an accuracy claim. Capture complete
measurement logs before rotation when a complete experimental sample is needed.

Live capture emits the registered prediction/observation kinds from Colony review and
existing task readback. Each sample resolves once, with lifecycle/capacity/expiry
loss represented as an unknown observation. Summary reason counts retain the
specific `unknown_reason` suffix for unknown prediction, observation and task
completion rows, making coverage/retention gaps countable. See
[residual matching](forecast-contracts.md#residual-matching) for current coverage,
execution evidence and deterministic pending bounds.
