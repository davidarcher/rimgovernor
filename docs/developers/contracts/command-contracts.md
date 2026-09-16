# Player command contracts

[Documentation](../../README.md)

These contracts describe admission, scope and preservation for player guidance.
For the end-to-end path, read [plans and Hands](../architecture/plans-and-hands.md).

The player command set is guidance, not per-pawn orders: build (one building
placement), research (project selection), create_goal, cancel_goal,
set_population_policy, set_expedition_policy, set_population_decision,
modify_resource_policy, set_resource_reserve and evaluate_world. Tend, rescue,
draft, movement, surgery, bed assignment, husbandry, recovery service, building
temperature, caravans, quests, settlement gifts, trade, zone edits and room
shells are not player commands; those families are produced only by the routine
planners ([issue #54](https://github.com/davidarcher/rimgovernor/issues/54)).
The model receives individually named
semantic commands and read-only native facts, not arbitrary native
execution. Fresh native facts and resource-definition labels are available in Manual as
well as Automate. The latest player message follows the evidence context. Consecutive
maintained goal and policy requests in the same response are applied before
interpretation ends; downstream routine work stays with the controller. Partial
rejections are included in the acknowledgment alongside accepted requests.
Acknowledgments come from accepted structured results, not inferred completion. Optional
local knowledge/wiki lookup, colony notebook, scout, visual review and consultations
remain chat tools. Advice is evidence, never executable authority.

Routine zone and bill actions follow the [animal husbandry](husbandry-contracts.md)
and [action contracts](action-contracts.md). See [player action coverage](player-actions.md) for fields, special storage filters,
UI reference guards and the native capability audit.

## Research and work assignments

Research admission resolves the player's project label through native dry-run validation
before adding an action. Refusals return the native reason directly; successful requests
use the resolved definition and still pass through Hands and fresh research readback.
Research contract discovery permits reads/previews only. Work commands accept an exact
unambiguous colonist name or observed ID, then retain the native ID for execution and
persistent work overrides. Current pawn jobs do not establish work-type assignments.

Tending and rescue are routine medical actions, not player commands; see
[medical care](medical-care.md).

## Context and native labels

Initial chat context includes the shared controller state and a native fact-section
index. Large native definition catalogs are retrieved through `inspect_colony_facts` in
one to three sections instead of being inlined into every question. These reads are
explicitly historical observations from the current review; native validation still
refreshes facts before a command is accepted. The initial context retains a bounded
native resource-label glossary so resource policy names remain grounded when the larger
definition catalog is omitted. Resource choices expose exact native display labels, with
definition IDs used for ambiguous labels. Exact observed IDs from earlier context remain
valid aliases. Resolution persists the native definition ID; base names do not include
qualified resource variants automatically.

## Provenance and resource policies

Actions carry PLAYER, AUTOPILOT or LLM_ADVISOR provenance. Advisory provenance cannot
commit orders. Player steps normally run before routine optimization; hard validation
and resource policies still apply. A food target updates the same EnsureFoodSupply goal
and hysteresis policy. Work overrides are retained by the deterministic allocator.
Resource constraints include reserves and normal/defense-only/stopped spending.
`ModifyResourcePolicy` changes only spending; `SetResourceReserve` changes only an
explicit numeric reserve, including zero. Each preserves the other field and validates
the resource against native facts. Policy changes queue a shared Hands action for native
production enforcement; clock admission refreshes the current budgets before simulation.
Persistent `MaintainResource` goals use exact native output definitions and stock
targets. Native source and bill facts supply their actual work types and relevant
skills. Active resource goals extend deterministic work coverage with these jobs;
incapable pawns remain excluded and explicit player work overrides retain ownership.
They acquire nearby reachable mineables or mature wild plants with identity-guarded
ordinary designators, or discover native bench recipes and create target-count bills.
Available recipes with an available native option for every ingredient slot take
precedence over recipes missing inputs; native bill admission still owns reachability,
reservations and consumption. Existing bills count as continuing capacity only when
their repeat mode and target cover the request. Player bill edits are preserved; an
explicit goal renewal is required before replacing previously issued production that no
longer covers its target. Construction material alternatives are selected against both
native costs and current policy/reservations. Unavailable sources, recipes and benches remain explicit
prerequisites; accepted orders and projected yield do not count as stock. The spending
contract rejects reserve fields; combined requests use separate policy calls in one
interpreter response. Existing stored policies retain their values.

## Inspection and player settings

Chat can inspect structured controller facts, gates, goals, blockers, reservations,
policies and intent history to explain what is running or why work is blocked. The
dashboard presents the same state. Its Autopilot page shows native readings, all
foothold gates, goal provenance/blockers and effective food, wood, temperature and
execution-speed parameters. Player setting edits go through a typed local API, colony
identity and effective-policy version checks, and cross-field validation. They persist
in ColonyPlan, invalidate pending direction and wake deterministic review without
creating a human chat request. Verification/recovery limits remain read-only in the
editor. Chat food targets and the editor use the same policy. Unsaved drafts survive
polling; concurrent changes require an explicit reload. Runtime revision/load guards
reject stale requests and conversational changes. Manual execution is scoped to the
accepted current player requests and does not resume time or release an external hold.
