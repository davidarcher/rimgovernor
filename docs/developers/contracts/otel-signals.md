# OTEL signals

[Documentation](../../README.md)

The brain's diagnostic stream is OpenTelemetry. Every decision and event
(a row) is an OTLP log record in `logs.jsonl`; spans go to `traces.jsonl` and
numbers to `metrics.jsonl` ([OTEL core](otel-core.md), [OTEL metrics](otel-metrics.md)).
These files are always written, with or without a collector. This contract
defines the row shapes, kinds, the record mapping, the readers and the retention.

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

### Native bridge (`RimGovernor.Brain.BuildingRuntime/Client`)

| Kind | Shape and fields |
|---|---|
| `combat_order` (decision) | `target` pawn, `verdict` `applied`/`refused`, `reason` refusal, attrs `index`, `job` |
| `clock_step` (decision) | `verdict` step outcome (`planned`, `deferred`, `failed`), `reason` `step`, `deferred` or `step_error`, `target` the step's cause (`review`, `wake`, `retry`, `full`), `dur_ms` step wall; attrs also `gate_wait_ms`, `journal_ms`, `stop` and `stop_latency_ms` (a step woken by a native hold: hold stamp to step start), the pacing fields (`pacing_reason`, `paced_tps`, `tps_ceiling`, `effective_tps`) and `error`; attrs `reads`, `tools` (calls per native facade method), `running` (read tally), `hop_wait_ms`, `hop_run_ms` and `hop_alloc_kb` (the game-thread hops: queue wait, run time and allocation summed) with `hop_kb` and `hop_run` (allocation KB and run ms per hop operation; `hop_run` also lists the parts a hop reports of its own run, keyed `<operation>/<part>`, such as `facade/construction_activity/paths` and the colony-facts read's sections `facade/observations_read_colony_facts/cf.<section>`, which are not summed into `hop_run_ms`); the step profile (#3556): `stage_ms` and `stage_alloc_kb` (wall and process allocation per stage: `gate`, `read`, `select`, `refresh`, `rounds`, `extent`, `critical`, `optional`, `record`), `selection` (how many planners each selection word chose: `dirty`, `due`, `dirty+due`, `full`, `all`), `rounds` on a step that ran planners: why it ran the rounder first (`due`: the last rounds review had stood `ClockSchedulerConfig.RoundsEveryTicks`, or there was none; `wake`: an early review; `all`: every planner ran; `freed`: a colonist lost its activity Method since the last review, #3599; `task`: a coupled Method's worker finished a task with a recipe left, #3624; `first_plan`: the colony's first layout plan holds the clock, #3652) or `last` (its planners read the last committed review), `kept`: how many ready Needs the family entries kept the last run's wait or refusal for, skipping their work (#3556; absent when none) |
| `clock_run` (decision) | one per scheduler step that changed or refused the clock, filed by `ClockRunDriver`. `verdict` `run`, `adjust`, `hold`, `refused` or `failed`, `reason` `resumed`, `watch_changed`, `speed_changed`, `coupled_order`, `autosave`, `combat_event`, the first refused reason (a refused decision is `refused` whether the clock was running or held), or `run_error` (any other failure); attrs `running`, `unwatched`, `coupled_orders`, `held_for` (the native hold reason), `hold_at_tick` (the review tick the Run named, #3556), `combat`, `refused`, `first_plan_fact` (on a `first_plan` refusal: the station fact the first layout plan waits for, or `layout plan` once it is recorded and no review has planned with it, #3652), `error` |
| `dispatch` (decision) | one per run that acted. `target` action, `verdict` outcome (`completed`, `refused`, `failed`, `waiting`, `held`), `reason` cause or error class; attrs `attempt`, `stage`, `stage_after`, `receipt`, `refusal_class` and `refusal_reason` (native's class and reason when the run left a refused receipt), `running`, `stale`, `error`, `repeated`, `reads`, `tools`. Level `WARN` for a failure other than a dependency wait. Written for a run that reached native (carries `receipt`, `running` and the read tally; `rimgovernor phases` counts only these) or whose outcome changed (`changed` true, `repeated` the unrecorded runs that restated the previous outcome); an unchanged run that never reached native writes nothing. `reason` is the stage word for a wait, `dependency`, `stale`, `error`, the first refusal, or `completed`; `refused` (all refusal reasons) rides in attrs. The cancelled-settle of an undispatched action is `failed`/`cancelled_settled` with `attempts`. `dur_ms` is the dispatch's wall (the batch it rode in, from the native call to its results) |

### Clock and scheduler (`RimGovernor.Brain.BuildingRuntime`)

Immediate protection adds `scope: immediate` to its `planner_step` rows,
`observation_tick` on the Rounder row and `decision_tick` on planner rows.
`dispatch_tick` on a Worker or synchronous combat Hands dispatch records the
journal's native dispatch tick, including uncertain replies.
`clock_step.critical_wave_ms`
includes pre-wave review. The native acceptance `ResponseEvidence` reader uses
it to separate controller activity from native outcomes and never treats an accepted dispatch as
the first native protective effect.

| Kind | Shape and fields |
|---|---|
| `planner_step` (decision) | one per planner run. `target` planner, `verdict` outcome (`admitted`, `waiting`, `refused`, `unselected`, `failed`), `reason` the refusal or wait `policy.Cause` wire value (a closed set; free-text detail is not recorded); attrs `concern` (a Concern id, or the NeedId of a family entry, which writes one row per Need; read by ExplainAudit), `subject` (optional, what the cause concerns), `proposals`, `edits`, `cause`, `admitted`, `running`, `reconciled`, `cleaned`, `deferred`, `retaken`, `combat`, `repeated`, `error`. Every selected planner's row carries `selected`, why the step's selection chose it (`dirty`, `due`, `dirty+due`, `full`, `all`). A family Need's row carries `kept` (true) when the run kept the Need's last wait or refusal and skipped its work: a `due` selection on a step whose `rounds` word is `last` or `due` (#3556). Rounder targets `rounds` (`ok`/`reviewed`, `ok`/`cancelled` when an authority change cancelled the round, or `failed`/`error`; attrs `partial`, and `stage_ms` / `stage_alloc_kb` per review stage: `journal`, `observe`, `layout`, then the derivation in order `d_owed` (the owed-facts block after the layout), `d_reads` (shell targets, sale art, surgery parts), `d_stock` (stockpiles, consumption, surface ore, meals, reserve), `d_planned` (planned sites, routes, thermal and room predictions, the firebreak), `d_file` (filing the sections, the emergency and dash facts, drafts), `d_work` (goals, research needs, the bench census and work coverage), `d_clear` (the clearance census) and `derive` (the ledger, open bills, incumbents and the colony model), then `acquisition`, `review`, `admit`, `record`; `fine_ms` / `fine_alloc_kb` split two of those further (#3566): `d_stock/zones` (the journal's zone claims and shelf plans), `d_stock/stockpile` (the stockpile review and the consumption window, which run together), `d_stock/ore` (the surface-ore reads), `d_stock/meals` (drug users, meals, the reserve, baby feeding, flooring), under it `d_stock/stockpile/census` (the patches and the mirrored bench census), `d_stock/stockpile/build` (the request: zones, stores, demand), `d_stock/stockpile/shelves` (the shelf stacks and rows), `d_stock/stockpile/occupancy` (the occupancy ring read) and `d_stock/stockpile/plan` (the maintenance review); `d_planned/sites`, `d_planned/shelter` (the thermal and room predictions) and `d_planned/firebreak` (the wait for the firebreak review, which overlaps the two); `derive/ledger`, `derive/bills`, `derive/incumbents`, `derive/model`, and under the ledger `derive/ledger/benches` (the mirrored bench read), `derive/ledger/declare` (the declarers, which run together), `derive/ledger/<declarer>` (each declarer's own wall, overlapping the others', allocation 0: `tradeexport`, `bill`, `art`, `armory`, `gear`, `foodstores`, `joiners`, `carebill`, `mechbill`, `babyfood`, `medical`, `resource`) and `derive/ledger/reconcile` (the diff under the ledger lock)); the worker targets `worker_step`, one per Worker cycle that dispatched (`ok`/`dispatched`, `dur_ms` the cycle's wall; attrs `candidates`, `dispatched`, `stage_ms` / `stage_alloc_kb` for `gate`, `read`, `dispatch`, `flush`). Step journal failures target `reasons`, `waits` or `proposals` with `failed`/`journal_error`. |
| `admission` (decision) | `target` method or plan, `verdict` `admitted`/`refused`/`held`, `reason` the refusal (a method refusal; a held building method `claim_conflict` or `method_continued`); attrs `concern`, `refused` (all reasons), `held_by`, `mode`, `work`, `combat_plan`, `hostiles`, `actions`, `error` |
| `clock_stop` | event row, one per combat hold the brain resumed from while a fight was open. `event` (the armed combat event), `tick`, `resume_latency_ms` (hold to the resuming `Run`), `over_budget` and `resume_budget_ms` when the resume-latency gate tripped, `ticks_since_stop` |
| `combat_summary` | event row at combat end. `stops`, `by_event`, `resume_latency_p50_ms`, `resume_latency_p95_ms`, `resume_budget_ms`, `gate_tripped`, `ticks_between_stops_p50`, `ticks_between_stops_p95` |
| `authority` | event row, the brain's view of its grant: `change` (`lost`, `retaken`; absent when the colony event consumer saw authority change under it), `reason` and the detail attrs; `WARN` for `lost`. Native's own generation moves are `clock_authority` rows ([below](#clock-and-colony-events)) |
| `colony_events_lost` | event row, component `clock-scheduler`: the brain did not receive some colony events and its next step plans Full. `lost` (native's loss counter since the open: writes that found the queue full), or `events` and `err` (a batch the consumer could not read against the current context). Read in the OTLP timeline. |

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


### Rounds, layout and colony (`RimGovernor.Brain.BuildingRuntime/Rounds`, `ColonyPlan`, `Layout*`)

| Kind | Shape and fields |
|---|---|
| `rounds_review` | event row. `revision`, `previous_revision`, `inspect_ms` and `coupled_search_expansions` (the policy inspection's wall inside the review, which the coupled search dominates, and the search's expansions against its bound; -1 when no alternatives were searched), `search_phases` (the search's expansions per phase it reached: `baseline`, `warm`, `seed`, `unserved`, `extend`, `confirm`, `sweep`; #3563), `search_profile` (per phase, #3556: `ms`, its wall; `evaluations`, the schedule searches it ran, a subset whose schedule was already searched at the bound runs none; `schedule_ms`, their wall, the rest of `ms` being keying, feasibility and ranking; `infeasible`, the evaluations whose every outcome was infeasible or unassessed; `unsupported`, those whose construction lacked supported service relief; `swaps`, the extension evaluations that swapped a root exclusive with a leader member in for it rather than adding one; `improved`, the evaluations that made a new leader), `selected_phase` (the phase in which the chosen subset first led; `baseline` when nothing beat it) and `warm_start` (`seeded` when the previous selection resolved to current roots and was screened first, `missing` when none of its ids resolved, empty without one) and `warm_translated` (#3556: how many ids of the previous selection named a planned room a tiled replan has since re-tiled under a new id and were pointed at the current room of the same role on its ground before the search, `RoundsRules.WarmStartOf`), `laps_changed`, `laps_same` and `laps_compare_ms` (#3564; from the second review of a world: per review lap after `observe`, the properties of the reading it entered on, the one the lap before it left, that differ from the previous review's, as the projection's property names and `Facts.<name>`, at most 24 then `+n`; a lap absent from `laps_changed` entered on an identical reading and is named in `laps_same`; a property whose type has no value equality, such as a catalog table the review rebuilds, is not compared: `RoundsLapInputs.Uncompared` names them), `concerns`, `emergency`, the food attrs of `roundsFoodAttrs`, `hunt_sources`, `hunt_holds` (each held hunt row as `id:reason`), `hunt_read` (the native hunt read: known, hunter and prey counts, findings) and `hunt_estimates` (rosters answered, pending, refused with reasons), `hunt_choice` (the roster chosen, why the others were unusable, the comparison's findings, and `released:<reason>` when the review released a held formation) and `hunt_restart` (once a comparison restarted: its tick and the `hunt_anchor_drift` finding that named what drifted from the anchor). Reader: `food/hunt-roster` (`TelemetryKinds.RoundsReview`, keys in `RoundsReviewTelemetry`: `hunt_choice`, `hunt_estimates`, `hunt_read` and the chosen roster's `hunt_*` terms on its `food_plan` line) |
| `colony_model` | event row, once per rounds review that has coupled candidates, emitted by the Rounder before the comparison. The tier-2 colony model's run (`RoundsColonyModel.Review`, #3531): `horizon_days`, `events` (the engine's budget counts these), `truncated`, `runway_day` (absent when nutrition does not fall to the harvest-gap stock within the run), `value` (the run's capability-days, absent while unassessed), `nutrition_end`, `completions` (each project or harvest finished, as `day:id`), `arrivals` (#3622: each resource entering the run's ledger from a harvest, hunt or craft, as `day:node:resource=count`), `dvdt` (capability-days an hour of each research and sowing node's work adds, as `node=value` joined by `;`), `findings` (the run's gaps, the value's unassessed parts and the input sections it could not start from as `input:<section>`) and `elapsed_ms` (wall time of the baseline run and one run per valued project), and `goals` (#3505: each unmet goal as `goal=day:N`, `goal=open` when its terminal completes in no run, or `goal=unmodelled:<reason>`). Reader: the `goals/research-goal` acceptance case (`TelemetryKinds.ColonyModel`, keys in `ColonyModelTelemetry`) |
| `coupled_selection.predicted_tasks.productive_learning` (nested attrs, same decision kind) | Conditional recipe learning: `skill`, `observed_rate_work_ticks`, `conditional_work_ticks`, callback `raw_xp`, projected `before_level`/`after_level`, cumulative `applied_gain`/`decay_loss`, native `provenance`. Absent mechanics are also named in selection uncertainty. Reader: Method-correlated `production/productive-cooking` case and OTLP timeline. Native callbacks/output, not these predictions, establish progress. |
| `coupled_selection.service_investments` (nested attrs, same decision kind) | Derived food/shelter investment explanations: existing Concern `owner` and `predicate`, `horizon_ticks`, `beneficiaries`, physical `tasks`, `metric` (`food_threshold_ticks` or `stationary_thermal_degree_ticks`), conditional `avoided_exposure`, and `retirement`. No second goal or material ledger. Readers: OTLP timeline and Method-correlated `shelter/service-investment`; native completion remains separate. |
| `coupled_selection` (decision) | Normal shared portfolio comparison. Target Method ID (or `continue`), verdict `selected`/`continue`/`deferred`, reason `shared_consequences`/`best_found_bounded`/`protected_method_ownership`/`method_not_admitted`. A refused proposal records `admission_failure` after journal admission rejects it; refusal does not revoke authority or interrupt installed work. Attrs `observed_tick`, `horizon_ticks`, `selected_ids`, `baseline`, `selected`, `rejected` consequence vectors, `beneficiaries` (need and mood consequences), `protected_continuation`, `uncertainty`, `predicted_tasks`, `planned_rooms` (per planned room with a site finished inside the horizon: `room`, `completed_sites`, `complete` alternatives; #3276), `composition_expansions`, `search_expansions`, `search_phases`, `selected_phase`, `warm_start` (as on `rounds_review`; #3563), generation/search/explanation truncation. A selection whose only new work is planned sites is compared again without them for its orders (finding `planned_unplaced_reselected`, #3557); one that then starts no order records target `planned_unplaced`; the room reconcile places the selected sites ([planned rooms](../architecture/portfolio-selection.md#planned-rooms)), and refuses an assessed unselected room with an `AwaitingPlan` verdict, subject `portfolio_selection`. Each vector (`baseline`, `selected`, every `rejected` row) carries a nested `value` attr (#3522, [comparator](../architecture/forecast-value.md#comparator)): what the outcome was ranked by. `total`, `hunger`, `rest`, `raw_ingredients` (meal nutrition forgone by eating cookable ingredients raw, #3626), `food_poisoning`, `thermal`, `mood_breaks`, `wellbeing`, `harm`, `investment` (tier-2 credit of project progress and applied skill XP, less medicine consumed, with `investment_contributions` rows `activity`, `pawn`, `key`, `units`, `value`; #3636; a closed planned bedroom's credit is a row with an empty `activity` under `bedroom/<room>/<pawn>`, #3655), `goals` (negative: the player's goals the outcome progresses, with `goal_contributions` rows `goal`, `progress`, `worth`, `value`; #3503) are capability-days lost (lower is better; null when unassessed, and a comparison uses only the terms both sides assess); `harm_detail` (#3546; null while `harm` is) gives `raids_per_day`, `exposure_range` (`[lo, hi]` hits per colonist per raid over the pawns), `down_probability` and `death_probability` (expected pawns downed and dead per raid, summed over the colony) and `cvar` (the summed per-pawn CVaR of one raid's loss); `unknown` lists the ledger's findings; `floors` holds `urgent_zero`, `break_risk_cap` and `death_risk_floor`, each with `assessed`, `passed` and `excess` (`death_risk_floor` is assessed when the outcome's and the baseline's harm are); `policy` lists the constants used. Selection `uncertainty` names `harm_capability_unassessed` (with the harm term's specific finding: `threat_schedule_unknown`, `raid_composition_unknown`, `exposure_cell_uncalibrated:<bin>/<role>`, `outfit_unobserved:<pawn>`) only while the chosen outcome's harm is unknown, `death_floor_unassessed` only while its death floor is, and `break_floor_unassessed`. Vectors also expose support first-care latency, pending care batches, unsupplied demand, production/care identities, conditional room effects and tend quality; see [support evidence](support-work.md#execution-and-evidence). Rejected explanations cap at 16 to retain selection and observation keys within the telemetry payload limit; omitted explanations set `explanation_truncated`. Readers: OTLP log timeline, prepared `production/coupled-needs`, `production/development-lifecycle` and `production/devilstrand-investment-starving` (`TelemetryKinds.CoupledSelection` through `CoupledSelections.Of`: the selected tasks and the urgent-zero floor of the baseline and the selection) and `apparel/crafted_armor` (through `CoupledSelections.Of`: the selected ids, whose clothing alternatives name their stuff, and the harm of the baseline and the selection; #3621) and `sleeping/bedroom-waits-for-value` (through `CoupledSelections.Of`: the investment rows and goals of the selected and rejected vectors; #3655). Predictions are not completion evidence. |
| `coupled_observed` (decision) | Existing Method continuation evidence. Target Method ID, verdict continue/transition after admission, or deferred with reason work_resource_conflict_or_unknown when shared resources cannot admit the productive continuation; ordinary evidence uses reason native_task_evidence; attrs `observed_tick`, `satisfied_tasks`, `remaining_tasks`, `native_states`. Readers: OTLP log timeline, Method-correlated `production/coupled-needs` and prepared support cases. Native completion and subsequent physical need changes remain separate from accepted orders. A worker read `completed` while it has a recipe left is a finished task: verdict deferred, reason `awaiting_task_review`, attrs `observed_tick` and `finished` (the task keys), and the woken scheduler step runs the rounds review (`rounds` `task` on `clock_step`, #3624) before the Method continues. |
| `standby` (decision) | One per colonist the rounds review gave standby. Target the pawn, verdict `selected` or `deferred` (the claim conflicted, or admission refused it). Reason `nothing_selected` (explicit idle, #3322), the claim conflict's reason, or `method_not_admitted`. Attrs `method`, and `refusal` when refused. Readers: OTLP log timeline. |
| `gear_search` (decision) | `verdict` `ok`, `reason` `best_found_within_budget`, `target` the pawn, level `WARN`; attr `budget_nodes` (`policy.GearSearchBudget`). One per pawn per rounds review whose loadout search spent its node budget and kept the best ensemble found. Reader: the launcher Observe tab's Problems and Log views (every WARN) and `rimgovernor log` |
| `tech_tier` | event row. `tier`, `evidence` |
| `layout_plan` (decision) | target `plan`: `verdict` `planned` (a plan derived; a colony's first plan is derived at the end of the review, after the ledger and the resource demand, and holds the clock until it is, #3652) or `replanned` (the tiled replan changed the plan; `reason` the triggers that fired, comma-joined), attrs `colonists`, `summary`; `skipped`/`facts_unknown` (attr `fact`) or `skipped`/`no_core` when the survey holds no core; `failed`/`chain_error` (attr `error`). Target `perimeter` (#2922): one row per defense node PlanPerimeter traced, when a plan is derived or a replan changes the ground (attr `node`): `place`/`ring_traced` the wall ring, `place`/`opening_most_routes` the killbox opening where the most raider routes cross the ring (a replan keeps it, and the reason), `unplaceable`/`opening_none` no route reached a ring position that opens. Target `perimeter_cut` (#2973, advisory): rows for the routing cut `PlanPerimeterCut` advises, on a derive or a perimeter-changing replan (attr `colonists`): `place`/`cut_traced` every route but the killbox opening is closed (attrs `cut` cells, `walls` cells thickened, `unclosable`, `cost`), `unplaceable`/`cut_unclosable` the cut crosses ground nothing can wall or a source touches the base (attr `why`); then one row per segment, `place` with reason `thickness_sapper_detour`, `thickness_capped_at_budget`, `thickness_no_route_to_opening` or `thickness_hp_unknown` (attrs `segment`, `cells`, `thickness`, `detour_ticks`, `hp`). Target `defense_tiers` (#2973, advisory): one row per defense tier node `PlanDefenseTiers` cut from the defense round's census, when the cut changes (attrs `node`, `nodes`): `place`/`tier_sited` the tier is sited from the census, `place`/`tier_pinned` it is built and stays where it stands, `place`/`tier_displaced_by_built` a tier still to build gave up a cell a built tier holds, `unplaceable`/`tier_layout_failed` (attr `why`) the census holds no corridor (only built tiers stand as nodes). Target `defense_tier_block` (#2973, advisory): one row per pinned tier a tree, chunk or new building blocks, when the price changes (attrs `tier`, `blocked`, `clear_w`, `clear_known`, `move_w`, `move_known`, `downtime` `unpriced`): `clear`/`tier_clear_cheaper` removing the blocker (work from the defs at the work-day price, net of yield) costs no more than moving, `move`/`tier_move_cheaper_downtime_unpriced` moving is cheaper (a lower bound: the downtime is not priced), `unpriced`/`tier_block_unpriced` neither could be priced. |
| `layout_interior` (decision) | the interior search (`SearchInterior`) of a derive or replan, component `layout`, target `interior`: one row per kept move, `verdict` `place`, `reason` `interior_search_move`; attrs `group`, `move` (slot:kind:terms improved). Written by `logInterior` after the `layout_stations` row (derive) or the `layout_replan_tiled` row (replan). |
| `layout_conduit` (decision) | the power conduit net's decisions (`TileResult.ConduitDecisions`, #2966, #3036), component `layout`, one row per node `LogConduits` journals after a derive's `layout_stations` row and a replan's `layout_replan_tiled` row. `target` the node routed (`room:<id>` or `site:<node>`); attr `to` the node it joined (empty when it found no net). `place`/`conduit_kept` the held link still serves it, `place`/`conduit_routed` it routed anew, `unsatisfied`/`conduit_no_route` no path reaches the net, `unsatisfied`/`conduit_no_source` the plan has no power site. Reader: `rimgovernor log` |
| `layout_edit` (decision) | `target` zone, role, item or building, `verdict` `admitted`/`refused`/`abandoned`/`closed`/`proposed`, `reason` refusal code or detail; attrs `family` (`stockpile`, `field`, `building`), `kind`, `role` (stockpile create/delete),  `plan`, `crop`, `cells`, `moves`, `gain`, `owner`, `x`, `z`. Reader: `acceptance colony`'s stockpile audit (`TelemetryKinds.LayoutEdit`, admitted stockpile creates and deletes) |
| `layout_regroup` (decision) | the regrouping search (`policy.Regroup`, #2878), component `layout`, target `groups`, written with each `layout_stations` run (skipped, and no rows, while its inputs are unchanged). One row per move applied: `verdict` `applied`, `reason` `regroup_merge_total_falls`, `regroup_split_conflict_priced`, `regroup_split_role_guard`, `regroup_move_need_total_falls` or `regroup_filth_merge_walk_beats_poison`; attrs `move` (`merge`/`split`/`move`), `a`, `b` (the nodes of the pair rule), `before`, `after` (the priced total in score points, lower is better), `walk`, `poison` and `clean` (the move's change of the embedding score, the food-poisoning price it adds and the change of the cleaning-labor price, in the same points; `clean` is zero for a filth merge because both groupings pay the same cleaning labor, #2993) and `lost_effects`. A butcher kept in its closet because the poisoning outweighs the walk saved is a `kept` row, `regroup_filth_merge_poison_beats_walk`, with the same attrs. With no move applied one row: `kept`/`regroup_no_move_improves`, or `skipped`/`regroup_work_price_unknown` while the work-day price is unknown. Reader: `rimgovernor log` |
| `layout_replan_tiled` (decision) | the replan (`Rounder.replanLayout`, `policy.ReplanLayout`, #3005, J4 of #2965): the tiled plan is the next plan. Component `layout`, target `plan`, one row per replan, `dur_ms` the run's wall time (the catalog rows, `ReplanFacts`, the chain and the tiler). The run is off the review's hold path (#3565): the hourly check that finds the inputs drifted starts it on a worker, the review that finds it finished adopts it (attr `adopted`, the ticks between), and no second replan starts while one runs. `verdict` `replanned`/`reason` `tiled_plan`: attrs `changed` (the plan was recorded), `inputs_changed` (the replan inputs that drifted since the last replan, from `LayoutInputs.Changed`: `plan_tick`, `survey`, `grown`, `pawns`, `geysers`, `growth`, `load` or a `StationFacts.InputsKeyParts` name such as `stock`, `orders`, `nutrition_per_day`, `expected_mood`, `plan`), `same`, `moved`, `new`, `dropped` (room counts of the tiled plan against the plan the colony held), `rooms` (each `room` the old plan's id, 0 for a new one, `tiled` the tiled plan's id, `role`, `verdict`, `reason`, `built`, and `short` when the room's group needs more floor than its standing rooms give, #3004), `unmatched_built` (each standing room the chain has no node to pin: `room`, `role`, `reason` `tile_outdoor`, `tile_no_native_role` (both skipped by `BuiltRegions` unless the room is a room node's, #3003), `tile_not_a_block` or `tile_no_node`), `built` (standing rooms) and `tiled_rooms`. A room `reason` is `tile_kept_built` (same), `tile_successor` (moved), `tile_sited_again` (an unbuilt room matched by role), `tile_unpinned_built` (an unmatched standing room the tiler builds its own room of), `tile_superseded` (dropped, its standing successor is the room), `tile_built_retired_by_old` (new: a tiled block continues a standing or replaced room the plan no longer holds), `tile_no_old_room` (new) or an unmatched reason (dropped). `skipped`: `station_advice_unavailable` (the run's chain made no advice; its `layout_stations` row says why), `facts_unknown` (attr `fact`, the tiler's) or `superseded` (the world or the saved plan changed under the run; attrs `started_tick`, `adopted`). `failed`/`tile_error`, `WARN`, attr `error` (the chain or the tiler could not plan; the colony keeps its plan and the next trigger tries again). Reader: `rimgovernor log` |
| `layout_role_guard` (decision) | `verdict` `mismatch`, `reason` `native_role_differs_from_predicted`, `target` the planned room id; attrs `native` (the census role), `predicted` (PredictRole of the room's observed stations), `scores` (role to score). One row per standing planned room whose native role differs, on each rounds building step that reads the plan, rooms, construction and sleeping census; a room with an unknown bed state or an ideo altar is not compared. The regrouping reads the rows as a cost input and a split move (`layout_regroup`). A guard error is `failed`/`guard_error`, `WARN`, attr `error`. |
| `layout_stations` (decision) | the station chain, one row per replan's chain run, written when the replan is adopted (#3565; the chain runs only inside a replan, so an hourly check whose inputs are unchanged writes none), component `layout`, target `plan`, `dur_ms` the chain's wall time. `verdict` `advised`/`reason` `station_chain`: attrs `nodes`, `edges`, `groups` (each `group`, `role` predicted, `stations` {node, count}, `requires`, `footprint`, `clearance`, `area`, `cost_ticks_per_day`, `lost_effects`, `rooms` matched built room ids, `built_area`, `outgrown`, `climate` {cells, max_c, best, options {opening, mean_w, peak_w, heating_w, cooling_w, build_w, total_w}} on a cold group when the curve is known, `block` when placed), `conflicts` ({a, b, conflict}), `bed_merge` (the free-bed pool's priced barracks size, #3658: {node, verdict `merge`/`split`, reason `cluster_bed_merge_priced` or `cluster_bed_merge_unpriced:<part>` with part `thermal` or `walls`}), `embed` ({level, placed, issues, built {group, room, verdict `keep`/`move`, reason `embed_built_*`}, cells, total, soil, edge, centre, walk, unmet_links, switch, seed}), `sites` (each power site `node`, `x`, `z`, `anchored`, and for a turbine `rotation`, `blocked` wind-path cells, `output` the fraction of rated output left), `site_issues` ({node, verdict, reason}), `vents` (the tiled plan's vent sites {x, z, rotation, reason}) and `vent_issues` ({group, verdict, reason} for a `tile_vent*` decision that placed none) and `comfort` (the advised comfort tier of the sleep node and each worked bench, {node, def, stuff, comfort, priced, gain, benefit_w, cost_w}; empty while the mood or comfort model or the colonists' mood is unknown, `priced` false for a node that could not be priced). `skipped`/`facts_unknown`: attr `fact` names the unread fact. `failed`/`chain_error`, `WARN`, attr `error`. A derive writes the same row with attr `derive` true; `embed` also carries `fields` (each field's placed rectangle). The replan tiles this advice (`layout_replan_tiled`). Reader: `rimgovernor log` |
| `snapshot_skip` | decision, `WARN`. `target` the snapshot (`colony`, `combat`, `defense`, `layout`, `firebreak`, `shelter`), `verdict` `skipped`, `reason` `not_recorded`; attrs `error`, `tick` |
| `goal_review` (decision) | the goals family's review ([goals contracts](goals-contracts.md)), component `goals`: one row per player goal per review. `target` the goal id, `verdict` the state (`met`, `pursuing`, `blocked`, `infeasible`, `no_method`), `reason` the owning Need's blocked reason, the broken floor (`urgent_zero` or `break_risk_cap`) of an `infeasible` goal, `facts_unread`, `no_pursuit`, `not fluid` or `missing_dlc` (empty while met or pursuing); attrs `kind`, `need` (the goal's NeedId), `owner` (the Need that pursues it, empty when none), `deficit` (in the kind's unit, absent while unread), `eta_day` (the tier-2 estimated completion day, #3505; absent when unmodelled or met) and, for a goal in a theme, `theme`, `theme_met`, `theme_total`. Readers: `rimgovernor log` and the goal acceptance cases `goals/research-goal` and `goals/theme-reform` (`TelemetryKinds.GoalReview` through `GoalReviews.Of`) |
| `routine_skip` (decision) | `target` the subject, `verdict` `skipped`/`waiting`, `reason`; attrs `quest`, `script`, `detail`, `findings` |
| `placement_refused`, `foreign_held` | event rows, component `building-planner` (`RoundsRoomReconcile.cs`). `msg` only: `furniture cell refused: <def>@<x>,<z> blocked by <thing, ...>` (`ring cell refused:` for a wall or door cell) (the native preview's blockers, `unreported` when it names none) and `foreign thing held: <def>@<x>,<z>:<reason>` (`casket`, `ancient_danger`, `not_deconstructible`, `forbidden_item`: a forbidden haulable stack on a wanted door cell, named but not blocking). The same text is the room's wait key (`<room>_reconcile:blocked:...`, `<room>_reconcile:held:...`). Read by the shelter climate cases (`ServiceLogs.LogMessages`) |

### Defense, population and pawns

| Kind | Shape and fields |
|---|---|
| `defense_action` (decision) | `target` pawn, plan, incident, cell or tier, `verdict` `applied`/`refused`/`failed`/`waiting`, `reason` the refusal or issue; attrs `incident`, `plan`, `outcome`, `x`, `z`, `error`. An unbuilt tier emits `waiting` / `tier_unbuilt` when its census changes, targeting the tier with attrs `buildings`, `census`. A turret, mortar or IED tier cut short by a gate emits `refused` with the gate as `reason` (`turret_unavailable`, `turret_threat_budget`, `turret_power`, `turret_stock`, `turret_no_conduit_route`, `mortar_*`, `ied_*`; see [defense tier counts](spatial-contracts.md#defense-tier-counts)), targeting the tier. |

### Other event families

| Kind | Shape and fields |
|---|---|
| `http_access` | event row, one per HTTP request. `method`, `path`, `status`, `dur_ms`, `bytes`, `long_lived`; 2xx GETs of `/api/state`, `/api/routines`, `/api/health` and `/api/presentation/*` under 250 ms are dropped; errors, slow calls and every non-GET are kept. Pprof rows are written at finish with `long_lived: true` (`dur_ms` is the capture, not latency). Written by `httpapi.Config.Access` (the brain) or `HttpChannelServer` (C#, [HTTP channel](../architecture/http-channel.md)); component `httpapi`; level `WARN` for a 5xx |
| `fields_select` | decision, one per farm site-type selection (`RoundsFieldPlanner`). `target` the winning crop, `verdict` `selected`, `reason` the winning site kind (`outdoor`, `greenhouse-new`, `greenhouse-reuse`, `hydroponics`); attrs `cells`, `buildings`, `needed`, `urgent`, `candidates` (each `kind`, `crop`, `needed`, `cells`, `score`, `terms` {name: value}, `reason` for an unplantable one). Read by the `Farm` acceptance cases. Component `clock-scheduler` |
| `recovery_batch` | decision, one per home-upkeep family read with queued recovery removals (`HomeUpkeepReader`); it journals the batch the planner executes. `target` `clearance`, `verdict` `planned`/`held`/`failed`, `reason` the stage (`roof`, `removal`, `none`, or `roof_rules` for a failed row); attrs `targets`, `roof_cells`, `batch` (load ids), `held` (each `id`, `reason` a `RemoteHoldReason` word), `blocker` (the first joint roof-check word), `error`. Component `clock-scheduler` |
| `food_credit` | decision, one per ledger counter group when the credit factor moves by 0.1 or more or the group's state changes. `target` the group (`crop:<zone>`, `fish:<x,z>`, `forage:<def>`, `animal_product:<race>`), `verdict` `credited`, `reason` `window` (the trailing window moved it) or `rebaseline` (a load held it); attrs `expected`, `observed` (nutrition over the window), `factor`, `window_days`, `state` (`designated`, `delivering`). Component `routine`. Written by the food review (`RoundsFoodPlan.cs`); `logview` prints the factor and state |
| `build_stalled` | decision, level `WARN`, component `building-planner` (`RoundsRoomStall.cs`): a room whose blueprints or frames have stood unfinished for `roomBuildStallTicks` (30000), repeated every window while it lasts. `target` the room, `verdict` `waiting`, `reason` the most common census reason (`awaiting_materials`, `no_eligible_builder`, a native `FinishingBlocker` word, `unreported`); attrs `blueprints`, `frames`, `reasons` {reason: count}, `tiers` {tier: count}, `eligible_finishers`, `max_min_skill`, `stalled_ticks`, `sites` (up to 8 `<def>@<x>,<z>`), `obstructions` (`<op>:<def>(<count>)@<x>,<z>` for pending claim, cut and haul targets; with no site standing `reason` is `obstructed`). The room wait key meanwhile reads `<room>_reconcile:obstructed:<first obstruction>` or `<room>_reconcile:awaiting_build:<n>_blueprints,<n>_frames:<reason>`. Reader: `rimgovernor log` (`logview.Entry.Words`) and the launcher Log panel (WARN) |
| `telemetry_drop` | event row, level `WARN`, component `otel`: the OTEL pipeline lost items (at most one per pipeline per 10 s). `signal` (`trace`/`log`/`metric`), `sink` (`file`/`collector`), `reason` (`queue_full`/`export_failed`), `dropped_since_last_warning`. The cumulative count is the `rimgovernor.telemetry.dropped` metric ([OTEL core](otel-core.md)). Reader: the launcher Observe tab's Problems and Log views (every WARN) and `rimgovernor log` |
| `log_overflow` | event row, level `WARN`: one summary of dropped diagnostics. `dropped`, `side` `mod` (the mod's ring overflowed while nothing was subscribed or the publisher lagged; `seq` is where the lines were) or `controller` (the reader goroutine's hand-off queue of 1024 was full) |

### Session and reader events

| Kind | Notes |
|---|---|
| `state_reset` | event row, level `WARN`, component `brain-host`: the state database came from another schema version and was moved aside (`path`, `aside`). Written once at startup. |
| `control_failed` | event row, level `WARN`, component of the player host: a player Resume or Pause failed (`request`, `kind`, `phase` the journaled record's phase or `""` when none began, `error` the exception with its stack; a resume names the step that threw, `resume <step>: ...`). The route's 503 detail carries the same message. Reader: diagnosis of a 503 control reply (#3549). |

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
| pawn_activity_execution | pawn, assignment, method, revision, step, state, reason, job_id; authorization/offer/start/termination are distinct | OTLP timeline; pawn observations ActivityExecution; control/pawn-activities; speed matrix `completed_tasks` (`SpeedMatrix.CompletedTasks`) |
| pawn_activity_interval | sequence, world (colony/load/map), pawn, start_tick, end_tick, job, job_id, phase, activity, purpose, targets, start_location, end_location, end_reason, nullable method/assignment | RimGovernor.cmd pawnactivity; observations/pawn-activity |
| pawn_activity_output | colony, load, nullable map/pawn/job_id, source, output_kind, resource, units, tick; observed production-site output, not job success or storage delivery | OTLP timeline; observations/pawn-activity |
| pawn_activity_fault | error; subsequent observations stop and report as missing time | OTLP timeline; pawnactivity gaps |
| pawn_activity_capacity | refused_observations; open interval cap reached | OTLP timeline; pawnactivity gaps |

These events use the existing bounded telemetry queue and rotating logs. Interval
boundaries are non-overlapping and captured by native transition hooks; there is no periodic phase sampling. Open intervals export only when changed or closed. Lost or rotated records are unknown time, not zero work.

The existing `coupled_selection` vectors also carry `maintenance` rows: actual requirement identity, conditional native XP shortfall and findings. The OTLP timeline reads these attrs; native `production/maintenance-range` and `production/maintenance-sparring` correlate the selected Method with real skills and restoration.
