# Source map

[Documentation](../README.md)

The runtime is Python, the dashboard is React, and native operations pass through
GABS/RimBridgeServer and the colony bridge companion.

```mermaid
flowchart LR
    Player[Player chat] --> Model[Local LLM interpreter / advisor]
    Game[RimWorld] --> Facts[Native and derived colony state]
    Facts --> Controller[Deterministic priority tree and methods]
    Model --> Intent[Semantic commands]
    Intent --> Plan[Shared ColonyPlan goals and actions]
    Controller --> Plan
    Plan --> Validate[Legality / geometry / resource validation]
    Validate --> Hands[Hands: durable intent and native executor]
    Hands --> Bridge[RimBridgeServer / GABS]
    Bridge --> Game
    Facts --> Outcomes[Postcondition reconciliation]
    Outcomes --> Plan
```

| Piece | Responsibility and source |
| --- | --- |
| Entry and lifecycle | [launch.ps1](../../launch.ps1) prepares/reuses the isolated game and controller; [controller/rimbot/__main__.py](../../controller/rimbot/__main__.py) starts FastAPI/Uvicorn on loopback port 8787. [headless.py](../../controller/rimbot/headless.py) prepares separate test profiles. |
| Runtime | [bridge_runtime.py](../../controller/rimbot/bridge_runtime.py) coordinates observation, reviews, execution, player direction, identity, clock events and rendering. Async guards prevent stale work after direction, plan or load changes. |
| Native boundary | [bridge.py](../../controller/rimbot/bridge.py) uses the MCP SDK to launch GABS and discover/call installed tools. [bridge_game.py](../../controller/rimbot/bridge_game.py) restricts gameplay capabilities; [native_contracts.py](../../controller/rimbot/native_contracts.py) validates native arguments and receipts. |
| Game integration | [integrations/colony-bridge/src](../../integrations/colony-bridge/src) supplies colony/status, pawn, item, building, room, zone, cell, research and world reads; construction, installation, settings, bills, orders, trade and dialog actions; clock supervision and rendering demand. The separate identity assembly persists colony identity in saves. RimBridgeServer supplies general game/UI tools. |
| Observation | [bridge_observation.py](../../controller/rimbot/bridge_observation.py) preserves raw responses and projects the generated [bridge_models.py](../../controller/rimbot/bridge_models.py) DTO. [data/bridge_observation.schema.json](../../controller/rimbot/data/bridge_observation.schema.json) is its source; [scripts/generate_bridge_observation.py](../../scripts/generate_bridge_observation.py) checks generation drift. |
| Control | [colony_controller.py](../../controller/rimbot/colony_controller.py) evaluates the priority tree in [colony_policy.py](../../controller/rimbot/colony_policy.py) and decomposes goals with [colony_skills.py](../../controller/rimbot/colony_skills.py). [player_commands.py](../../controller/rimbot/player_commands.py) validates semantic chat requests; [planner.py](../../controller/rimbot/planner.py) interprets only new human messages. [colony_plan.py](../../controller/rimbot/colony_plan.py) separates goals/specifications from execution progress. [resource_accounting.py](../../controller/rimbot/resource_accounting.py) arbitrates both paths. |
| Execution | [hands.py](../../controller/rimbot/hands.py) compiles semantic steps, validates geometry, records intent and advances native orders. [construction_grounding.py](../../controller/rimbot/construction_grounding.py) and [construction_preflight.py](../../controller/rimbot/construction_preflight.py) ground definitions and preview placement. [projects.py](../../controller/rimbot/projects.py), [medical_outcome.py](../../controller/rimbot/medical_outcome.py) and [trading.py](../../controller/rimbot/trading.py) reconcile specific outcomes. |
| Inference and advice | [model.py](../../controller/rimbot/model.py), [request_budget.py](../../controller/rimbot/request_budget.py) and [model_router.py](../../controller/rimbot/model_router.py) handle local streaming requests, structured-response recovery, context budgets and optional roles. [consultation.py](../../controller/rimbot/consultation.py), [scout.py](../../controller/rimbot/scout.py) and [visual_review.py](../../controller/rimbot/visual_review.py) supply bounded advice. |
| Knowledge and persistence | [knowledge.py](../../controller/rimbot/knowledge.py) retrieves local strategy cards; [wiki.py](../../controller/rimbot/wiki.py) retrieves reference material. [memory.py](../../controller/rimbot/memory.py) stores advisory colony notes. [review_evidence.py](../../controller/rimbot/review_evidence.py) retains exact review-local results in memory. [store.py](../../controller/rimbot/store.py) persists state, events and retired action/method evidence in SQLite; its bounded compressed decision-snapshot helpers currently have no runtime callers. |
| Dashboard and diagnostics | [bridge_server.py](../../controller/rimbot/bridge_server.py) serves the Vite-built dashboard and state, chat, control, project, notebook, camera and diagnostic endpoints. [dashboard/src/features/manager](../../dashboard/src/features/manager) renders colony/people, plans, activity and notes. [tool_diagnostics.py](../../controller/rimbot/tool_diagnostics.py) records strategy/native call outcomes. |

Unqualified Python module names in the table are under
[controller/rimbot/](../../controller/rimbot).

## Related reading

Start with [the system overview](../explanation/overview.md) for the relationships
between these components.
