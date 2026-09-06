# Semantic objectives, execution and strategy guidance

The normal review path now separates planning, objectives and native execution:

1. Strategy/daily planning supplies department assignments.
2. Up to four departments review the same observed snapshot concurrently and submit typed ObjectiveProposal objects. They receive only the submit tool: no native writes or detailed command discovery.
3. The administrator accepts or defers objective proposals, resolving scope and priority conflicts. This approval authorizes execution within those objectives.
4. A model-driven executor handles one approved project at a time. It reads fresh state and uses only the native command group for that project kind. A submitted batch executes serially without another LLM approval. One failed project does not prevent other approved projects from proceeding.
5. Existing native checks track each issued order. Projects link those work IDs and report orders_verified separately from achieving the overall objective.

Projects contain a stable ID, owner, player-system kind, outcome, optional quantity, constraints, optional native definition requirements and success signals. Supported kinds are construction, growing, production, storage, work assignment, supply access, care, security and research. These are API capability groups, not recipes for individual rooms. Execution remains model-driven. Trade and other unlisted project systems remain future coverage.

Continuing an objective reuses its project ID. Existing work remains visible; the executor must inspect it before adding orders. Exact outcome duplicates from the same owner reuse a project, but differently worded equivalent objectives still require administrator judgment. Native order completion does not establish enclosure, accessibility or all higher-level success criteria. The current executor returns concrete blockers for reassessment; there is no claim of automatic proof of an entire room or production system.

## Definition requirements

Construction objectives may require native building-definition properties, for example is_bed=true and bed_humanlike=true. Before retaining a construction draft, the controller reads its exact native definition and checks these properties. Unknown or mismatched properties fail explicitly. No names of particular beds/materials are encoded in this validator. Requirement selection remains the manager's responsibility, assisted by guidance; omitted criteria cannot be inferred automatically.

The native construction contract now includes description, is_bed and bed_humanlike. A live test caught animal beds being selected for colonists before these facts/requirements were available. This discriminator comes from RimWorld's definition, not a controller synonym table.

## Strategy library

Editable entries live in controller/rimbot/data/strategies/*.json, validated with the Strategy model. Each has an ID, version, title/tags, applicability, approach, facts to verify and reconsideration signals. Sixteen concise entries cover the first days, shelter, crop choice, food gaps, cooking, storage, work, recreation, research and defense. Entries paraphrase linked RimWorld Wiki sources, with source-check dates shown in the dashboard. Scenario-specific examples are starting points, not guaranteed quantities. Local observations and player constraints override wiki advice; in particular we do not adopt the quickstart guide’s blanket allow-all recommendation.

Deterministic text retrieval selects up to three matching entries per planning/manager/executor request. The library provides conditional guidance, not hardcoded numerical game rules or authority over player instructions. Source metadata is excluded from retrieval scoring and model prompts to avoid wasting context. It is packaged with the controller and visible from the dashboard's Projects section. GET /api/strategies lists it; q and limit search it. GET /api/semantic/schema publishes the objective schema.

## Models, concurrency and display

Departments and executors use the configured department model (currently 4B); strategy, daily planning and arbitration use the main model (9B). Routine execution uses reasoning off. The department parallelism setting defaults to four and can be set to 1–4. Only objective reviews run concurrently. Their snapshot is shared; later execution refreshes live state. Model progress retains role/model labels, and the dashboard lists active departments.

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

## Proposed bulk supply release (not implemented)

The existing native things/set-forbidden command accepts explicit IDs and directly applies their requested flag; it has no hostile-distance filter. A separate bulk operation could accept a camp center/radius and configurable hostile exclusion radius, select visible spawned item stacks, then report changed IDs and skipped counts/reasons. Recheck the selection against current native state when executing. Keep ordinary explicit-ID behavior unchanged. Do not special-case insect jelly: the risk comes from location, not the item definition. Hostile-distance checks are a heuristic, not proof of a safe hauling route; moving threats, ranged reach, hives and routes through danger need explicit treatment before labeling this operation safe. The model should receive the actual filter parameters and exclusion report instead of selecting every stack itself.
