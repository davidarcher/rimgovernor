# One strategic authority, deterministic execution

## Inspection and migration map

Before this change the active bridge controller already had one `Planner`.
The administrator/domain-manager reconciliation loops were retired with RIMAPI;
they survive only in Git at `9209b74` and `docs/archive/rimapi`. They were not
silently restored. The live gaps were a direct model-to-native-write loop, prose
plans, a fixed 30-second review timer and one hardwired inference configuration.

This refactor reuses `BridgeGame`/`ObservationGateway` native schemas and checks,
`LocalModel` streaming/request budgeting, `Store` SQLite state and events,
`ProjectBook` observed building/zone reconciliation, native colony/load identity,
and the in-game clock lease plus draft ownership cleanup.

## Active modules and authority

- `planner.py`: sole strategist. Read native contracts, inspect state/dry runs,
  optionally consult, then commit a schema-checked decision. No native-write or
  direct clock tool is advertised. Continue/defer leaves the existing plan intact.
- `colony_plan.py`: typed `ColonyPlan`, goals, priorities, assumptions, constraints,
  risks, dependencies, semantic steps, completion/reconsideration conditions,
  chosen tick, rationale and bounded revision history. Mutable execution progress
  is separate. Commit requires strategist authority and the expected revision.
  Players can cancel steps; advisers cannot commit or cancel them.
- `strategic_state.py`: deterministic projections, power-deficit runway, material
  shortages, pawn health/hunger/mood signals, trends and pending events. Hysteresis
  prevents mood/hunger threshold chatter. Model context excludes whole-map rows
  and operation histories; detailed facts remain selectively queryable.
- `hands.py`: no model client. Compiles a room shell to the complete perimeter and
  one door, building batches to placements, and patch-shaped zones to native cells.
  Validates reserved walkways, plan overlap, full occupied footprints, existing
  zones, material availability and native placement eligibility. Persists write
  intent, distinguishes receipts from finished construction, and retains partial
  progress. Native schemas and native validators remain the authority for legality.
- `model_router.py`: lazy STRATEGIST / ANALYST / ARCHITECT / CRITIC routing. Each role
  has independent provider/model/context/reasoning/temperature/timeout/output size.
  Only the local OpenAI-compatible provider is implemented; unsupported providers
  fail configuration. Roles can share one endpoint/model. Only STRATEGIST is
  configured by default; no automatic larger-model request or model selection.
- `consultation.py`: one-shot generic analyst, optional spatial/VL architect and
  optional critic. Takes a question and 1–3 bounded state sections. The same analyst
  can consider food, labor, caravan preparation or any other narrow question.
  Only `report` is exposed; no game client, plan store or recursive consultation.
  Architect alone may receive a screenshot and return a structured spatial design.
  Findings include evidence, uncertainty and missing facts. Strategist marks which
  consultation IDs influenced its decision; reports never auto-apply.
- `bridge_runtime.py`: observes and advances committed steps without inference;
  material changes, player direction, blocked/completed steps and a daily check
  *with changed facts* wake the strategist. No unchanged-state 30-second LLM loop.
  Native clock events still interrupt stale calls, and Manual prevents execution.

No mandatory analyst fan-out exists. Advisory call depth is one. The strategist
can work without any adviser, with a single loaded local model.

## Execution semantics

Semantic actions currently supported:

- `build_room_shell`: rectangle, entrance side, observed wall/door definitions,
  acceptable materials. This is a shell, not a promise that roofing is complete.
- `place_buildings`: a batch of observed definitions/positions/material preferences.
- `create_zone`: stockpile or growing patches, optional storage preset/priority,
  explicit crop for growing zones. Reject incomplete geometry and duplicates.
- `clock`: controlled native time with the existing lease/interrupt semantics.
- `native_operation`: an explicit fallback for bills, configuration, pawn orders,
  trading and designators. It is committed and validated, not an immediate model
  side effect. Its completion means native command receipt/readback, not arbitrary
  downstream pawn labor. Broad domain verbs without a reliable implementation
  are not invented or silently approximated.

Hands yields after 12 operations per controller pass so player input can intervene;
this is not a limit on room size or an LLM turn budget. No inference is needed for
the next batch. Building steps wait on `ProjectBook` observations. Dependencies may
require either issued orders or completed construction. A blocked step reports a
structured code, detail, evidence and retryability. Explicit `retry_steps` only
accepts retryable failures. Ambiguous non-idempotent writes (e.g. a lost trade
receipt) are never automatically replayed.

Unchanged step IDs preserve receipts. Changed execution intent needs a new ID.
Identical intent cannot be renamed into another active step. Player cancellation
also records the action fingerprint to resist recreation under a different ID.
Native building presence and exact zone geometry prevent practical duplicate work.

## Persistence and context

SQLite stores plan, progress, chat/direction, strategic state/trends, adviser reports
and events by saved colony ID/map. The load token invalidates in-flight inference.
A model change does not change the game API or erase the plan. Load/restart enters
Manual. Construction observation notices targets removed by the player or absent
in a loaded save. Old prose plans remain readable but are not converted into guessed
executable actions. Event history and revision history explain why a plan changed.

Model metrics are durable controller-wide totals by role: calls, input/output tokens,
elapsed inference time, failed calls, consultations and consultations used. The
reported output tokens per wall second includes reasoning/network time; it is not
GPU decoder throughput. Plan revisions and execution failures are visible in the
existing event stream. Projects shows committed steps/cancellation; Activity shows
role metrics. Adviser reports are evidence, not authoritative conversational memory.

## Configuration

`launch.cmd` uses just the strategist selected by `-Model` (default Qwen 9B).
To opt into generic 4B analysis:

```powershell
.\launch.cmd -ModelsConfig config\models.example.json
```

Restart an already running controller after changing configuration. Edit exact model
IDs/context windows to match LM Studio. Omit analyst/architect/critic to disable them.
Add architect or critic entries with the same fields as strategist; an architect
receiving images needs a vision-capable model. Nothing automatically loads an extra
role simply because it is installed in LM Studio.

## Native interface gaps / boundaries

- Nutrition per edible item, diet-adjusted daily consumption, spoilage and expected
  harvest are not supplied by the compact contract. Food-days is explicitly unknown,
  not fabricated from item names/counts. Pawn food needs and native alerts do work.
- Beds are detailed natively, but the compact contract does not provide validated
  sleeping-slot capacity or a universal roof-completion predicate. Room-shell
  completion only asserts built walls/door. Automatic furnishing placement, mining
  shelters and reuse of ruins need additional semantic compilers.
- Generic native operations do not yet all have end-to-end job completion observers.
  Trading transaction validation and combat outcome/cleanup remain migration work.
- Native reads and writes are separate requests; immediate native validation is used,
  but Python footprint/resource checks are not a transaction with the game. The native
  tool rechecks its own legality immediately on the game thread.
- Building queries lack complete rotation details; existing targets are reconciled
  by definition, origin and material. Rotation-change reconciliation needs richer
  native observation rather than a guessed JSON field.
- Architect proposals are now available as an optional capability, but this change
  does not establish that a local VL model reliably designs a good base. Blind visual
  second opinion and interactive data scouts remain separate queued capabilities.
- Fast reflex currently means native danger/health/lease pause. Automatic rescue,
  firefighting and heat escape require native safety/path validation before adoption.

## Checks

`controller_tests/test_strategic_architecture.py` covers authority, disabled advisers,
malicious adviser tool output, durable plans/model switching, material blockers,
no-op observation scheduling, downed-pawn signals, hysteresis, cancellation, graph
validation and room geometry. No model/game runs in those tests.

`scripts/native_strategy_smoke.py` uses one scripted strategist decision against
real RimWorld: committed stockpile -> native validation -> observed zone -> replay
without duplicate action or another inference. It removes its probe afterward.
This verifies the execution pipeline, not model playing skill or colony survival.

Live Qwen 3.5 9B also returned a validated `continue` decision without game writes
in roughly six seconds. Its first request exposed an LM Studio grammar compiler
failure on nested string-length bounds. The inference adapter omits those bounds
from the wire schema; the original controller schema still validates every result.
This protocol check does not establish autonomous planning quality.
