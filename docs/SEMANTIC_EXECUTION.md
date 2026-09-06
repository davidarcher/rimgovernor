# Semantic objectives, execution and strategy guidance

The normal review path now separates planning, objectives and native execution:

1. Strategy/daily planning supplies department assignments.
2. Up to four departments review the same observed snapshot concurrently and submit typed ObjectiveProposal objects. They receive only the submit tool: no native writes or detailed command discovery.
3. The administrator accepts or defers individual objective candidates, resolving scope and priority conflicts. Every active project must be kept or retired; accepted candidates can update an existing project and correct its command-system kind. This approval authorizes execution within those objectives.
4. A reasoning-enabled task planner handles one approved project at a time. It reads fresh state and uses only the native command group for that project kind. A submitted batch executes serially without another LLM approval. One failed project does not prevent other approved projects from proceeding.
5. Existing native checks track each issued order. Projects link those work IDs and report orders_verified separately from achieving the overall objective.

Projects contain a stable ID, owner, player-system kind, outcome, optional quantity, constraints, optional native definition requirements and success signals. Supported kinds are construction, growing, production, storage, work assignment, supply access, care, security and research. These are API capability groups, not recipes for individual rooms. Execution remains model-driven. Trade and other unlisted project systems remain future coverage.

Continuing an objective reuses its project ID. Existing work remains visible; the executor must inspect it before adding orders. Exact active outcome/kind duplicates reuse a project across owners; differently worded equivalents require administrator judgment with the full active ledger visible. Retired projects leave game orders untouched. Correcting a project kind archives its old work links so unrelated commands cannot count toward the corrected objective. Native order completion does not establish enclosure, accessibility or all higher-level success criteria. The current executor returns concrete blockers for reassessment; there is no claim of automatic proof of an entire room or production system.

## Definition requirements

Construction objectives may require native building-definition properties, for example is_bed=true and bed_humanlike=true. Before retaining a construction draft, the controller reads its exact native definition and checks these properties. Unknown or mismatched properties fail explicitly. No names of particular beds/materials are encoded in this validator. Requirement selection remains the manager's responsibility, assisted by guidance; omitted criteria cannot be inferred automatically.

The native construction contract now includes description, is_bed and bed_humanlike. A live test caught animal beds being selected for colonists before these facts/requirements were available. This discriminator comes from RimWorld's definition, not a controller synonym table.

## Strategy library

Editable entries live in controller/rimbot/data/strategies/*.json, validated with the Strategy model. Each has an ID, version, title/tags, applicability, approach, facts to verify and reconsideration signals. Sixteen concise entries cover the first days, shelter, crop choice, food gaps, cooking, storage, work, recreation, research and defense. Entries paraphrase linked RimWorld Wiki sources, with source-check dates shown in the dashboard. Scenario-specific examples are starting points, not guaranteed quantities. Local observations and player constraints override wiki advice; in particular we do not adopt the quickstart guide’s blanket allow-all recommendation.

Deterministic text retrieval selects up to three matching entries per planning/manager/executor request. The library provides conditional guidance, not hardcoded numerical game rules or authority over player instructions. Source metadata is excluded from retrieval scoring and model prompts to avoid wasting context. It is packaged with the controller and visible from the dashboard's Projects section. GET /api/strategies lists it; q and limit search it. GET /api/semantic/schema publishes the objective schema.

## Models, concurrency and display

Departments and concrete task planning use the configured department model (currently 4B); strategy, daily planning and arbitration use the main model (9B). Task planning respects the reasoning setting. The historical Executor role label refers to task planning; issuing its approved batch remains ordinary controller code without an additional model call. The department parallelism setting defaults to four and can be set to 1–4. Only objective reviews run concurrently. Their snapshot is shared; later execution refreshes live state. Model progress retains role/model labels, and the dashboard lists active departments.

The Projects panel shows objectives, constraints, feedback and order-tracking state. Manager activity distinguishes objective proposals and executor activity. Overlong administrator display prose is shortened to the UI bound without changing strict approval IDs or rejecting the decision solely for verbosity.

## Verification

Controller tests cover the complete objective/approval/execution path, scoped commands, definition requirements, project reuse, native order reconciliation, deferred objectives, independent project failures, bounded concurrency and strategy retrieval. Native read-only discriminator check:

```powershell
.venv\Scripts\python.exe scripts\live_semantic_definitions.py
```

Use scripts/benchmark_setup.py with the same fixed save to measure gameplay. The initial semantic run issued incorrect animal-bed orders and did not complete the startup goal; its trace is preserved under .rimbot/setup-benchmarks/20260905-221230/. It prompted the native discriminator and generic requirement checks above. A successful wire/protocol test alone is not evidence of good base design. Vision-based architecture is not implemented here.

## Context and server diagnostics

Managers receive a decision snapshot rather than the full execution view: alerts, worker activity, basic pawn health/mood, counts of existing construction, and bounded resource groups with explicit omissions. Detailed live APIs remain available to executors. Native API response DTOs are unchanged by this model-context projection. Streamed server errors are surfaced directly instead of being reported only as an incomplete response.

The parallel trial at `.rimbot/setup-benchmarks/20260905-222354/` is not a valid performance comparison: the user was changing model loads, and LM Studio reported context-size errors. Both 4B and 9B were subsequently verified loaded at 32,768 context before the next run.

The 32K trial at `.rimbot/setup-benchmarks/20260905-223029/` completed six model calls but issued no orders in 180 seconds. Managers selected humanlike-bed requirements correctly; the administrator deferred both objectives for forbidden materials and absent construction orders. Approval instructions now clarify that resolving in-scope prerequisites is executor work, not a condition that must already be completed before approval. This final instruction change is not yet gameplay-validated. The startup benchmark remains failing; protocol coverage is not a performance success.

## Bulk Allow with the requested jelly exception

`orders_unforbid_all` / POST `/api/v2/orders/unforbid-all` takes only `map_id`. It uses RimWorld's Allow designator eligibility and operation on explored items, skipping `ThingDefOf.InsectJelly`. Already allowed jelly stays allowed; this is not a recurring re-forbid policy. Explicit item Allow remains unchanged. Other supplies near threats are still allowed. The native result reports changed stacks, excluded forbidden jelly stacks and remaining eligible stacks. `orders_forbidden_overview` observes the same selection without changes, so the controller verifies completion immediately and can reconcile a lost response without issuing another write. Neither operation implies hauling or delivery.

These contracts use the existing schema-generation pipeline (its source filename is historically construction.openapi.json); the public routes belong to Orders. No Python-fabricated game result or hardcoded jelly numeric ID is involved.

Terrain and crop groups retain the bounded native resource overview instead of being reduced to the three most frequent groups. This preserves small nearby plantable patches. Idle status is explicitly separated from missing work configuration: construction, zones, bills and designations create ordinary jobs. Invalid outdated assignments can be revised; administrator corrections request a daily-plan refresh. These changes do not prove arbitrary model goals equivalent or guarantee correct placement.

The context also presents crop minimum fertility alongside matching observed nearby terrain, without claiming that fertility establishes pollution tolerance, route safety or full placement legality. Display-only daily-plan summaries are shortened instead of invalidating the structured plan.

Live check on the existing colony reached individual-objective arbitration but stalled waiting for LM Studio after prompt processing; no new construction was issued. The final crop comparison, tracking dismissal and display-summary change were not live-tested. Controller checks passed; this is not a successful colony-setup benchmark. Automation was returned to Manual.
