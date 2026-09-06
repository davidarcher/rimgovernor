# Startup benchmark and construction work observations

## Native work observations

`GET /api/v1/map/construction-work?map_id=0&offset=0&limit=16`

OpenAPI generates the wire DTOs. This read-only endpoint groups pending native blueprints/frames with:

- Native remaining material requirements, including material cost multipliers and delivered frame contents.
- Visible allowed/forbidden stack quantities. Stockpiles and loose ground stacks are both usable sources; being stored is not required for construction.
- Supply quantities reservable/reachable by an eligible constructor, with example IDs.
- Each colonist's current job, native idle status, draft/downed state and construction priority.
- Native unforced construction eligibility and failure text, plus blocking object IDs when available.
- Current job targets and frame work progress.

The controller supplies the first 16 sites to planning and refreshes them before each manager. The response includes total and next_offset so omitted sites are explicit; managers can request more. Missing support is unknown, not an empty queue.

These are observations, not a full job simulation. Native CanConstruct does not encompass every WorkGiver decision. Dedicated haulers and colony mechs can differ from the assessed colonists. Materials are shared, not allocated per site. Reservation failure can mean another pawn is already working. Current job targeting does not prove progress; changes in delivered materials/work or completion do. Normal native danger/reach checks are not a guarantee of safety.

## Fixed-save benchmark

Requires a loaded game, an idle dashboard in Manual, and a complete disposable `.rws` in RimWorld's Saves directory. It reloads that save, uses the dashboard model settings, resets only its own isolated controller state, then runs the production manager loop. The normal dashboard stays in Manual. Do not interact with the colony during the run.

```powershell
.venv\Scripts\python.exe scripts\benchmark_setup.py --execute --save "C:\Users\darch\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Saves\YOUR-FIXTURE.rws"
```

Default case: provide sleeping arrangements for the colonist count, verified by native completed Bed/SleepingSpot objects. These fixture definitions are benchmark criteria, not instructions or placement rules supplied to the model. Custom cases can set --objective, --target-def (repeatable), --target-count, --speed and --timeout. This initial completion criterion counts standard single sleeping places; it does not yet verify ownership, accessibility or enclosure.

Reports and sampled observations are under `.rimbot/setup-benchmarks/`, with an isolated SQLite model/tool trace. Reports capture save SHA256, source revision, model/controller settings, speed, first order latency, new completed objects, sampled idle pawn ticks, delivery/work transitions, peak excess sleeping capacity, rejected calls and token/latency totals. First order is not necessarily useful; completion and excess capacity must be evaluated alongside it. Excess capacity is a duplicate-work signal, not proof an order was unjustified. Idle ticks are sampled, so brief transitions between samples are missed.

Use the same fixture, model settings and speed for comparisons, and repeat runs because model/game execution is not guaranteed deterministic. Source revision identifies the last commit; a dirty development run requires preserving its diff separately. A timeout is a recorded failed baseline, not a successful gameplay test.

`live_construction_work.py` is a separate disposable native test: it places a bed blueprint near a starting wood stack, toggles and restores that stack's forbidden state, and verifies that available supply changes without pretending delivery occurred. It leaves the blueprint; reload the fixture afterward. Run paused in Manual.

## Next architecture experiments

Managers currently choose objectives and draft exact native orders. Use this benchmark to compare a semantic execution layer: managers choose outcomes/constraints; an executor resolves construction projects, workbench bills, zones and work assignments through the native API; observations verify progress. Do not add a bespoke procedure for every room or colony objective.

A small versioned strategy library can supply relevant experience without putting a whole guide in context. Entries should state applicability, priorities/tradeoffs, facts to verify and reconsideration signals. Native definitions supply current numerical rules. Retrieve a few relevant entries and benchmark them before assuming they improve play. This iteration does not implement that library or an additional executor agent.

### Spatial architect experiment

Compare the current model against a vision-capable architect on identical explored maps. Provide a simplified top-down rendering and typed cell data sharing exact coordinates: terrain, obstacles, structures, roofs, doors, zones, existing plans and colony location. Mark unexplored space explicitly. Do not substitute decorative game video for precise coordinates.

The architect proposes a layout; native placement validation and geometric enclosure/access checks assess it before an executor stages construction. Evaluate complete enclosure, reachable entrances, overlap/placement rejection, distance from the colony and opportunities for expansion. Include existing structures, mountain edges and poor-soil maps. Measure latency and model calls too. Vision is a hypothesis to test, not assumed to solve room design. This iteration implements neither the renderer nor a vision agent.

### Model routing experiment

Test a smaller model for narrow manager/executor assignments while retaining the current 9B for strategy, arbitration and ambiguous failures. Measure completed-work latency and retries, not only generation speed. Record loaded-model memory use and switching overhead. Keep native validation/progress deterministic. This iteration does not change model routing.

## Initial live baseline

The fixed-save 180-second run failed to complete sleeping arrangements: no orders issued and no new completed objects. See local report `.rimbot/setup-benchmarks/20260905-214548/report.json`. Native material/forbid observations passed their separate live test. The manager trace shows repeated numeric work-priority corrections and an incomplete model stream during coordination. More context alone did not resolve execution; semantic execution and narrower responsibilities remain experiments, not completed fixes.

## Department model routing

Settings now offers a planning/administrator model and an optional department model. Blank or identical department model uses the main client. Infrastructure, Survival, Security, Development and Workforce use the department client; strategy, daily planning and arbitration use the main client. Reasoning and generation limits remain unchanged. Clients keep independent server compatibility state. Model call/failure events and the active status identify the selected model.

The current local experiment uses `qwen3.5-4b` for departments and `qwen/qwen3.5-9b` for planning/arbitration, both verified loaded at 32,768 context. This is role routing, not automatic escalation or a semantic executor.

### 4B department trial

Same fixed-save SHA, speed 1 and 180-second deadline as the initial baseline. Result: 0 orders, 0 completed objects, 41 completed model calls, 0 rejected calls. The run timed out, with incomplete-stream failures also observed. Trace/report: `.rimbot/setup-benchmarks/20260905-215503/`. This single failed trial does not establish better or worse model quality; the setup pipeline did not make useful progress with either configuration. Per-model call IDs confirm routing. The 4B department setting remains enabled for further experiments.
