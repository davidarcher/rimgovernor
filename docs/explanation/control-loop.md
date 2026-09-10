# The control loop

[Documentation](../README.md) · [System overview](overview.md)

Routine operation is an observation-and-verification loop. The controller asks what the
colony needs, chooses a bounded piece of work, lets the game run under supervision, then
observes what actually changed. It does not need a model call to repeat this cycle.

## Observations are evidence with limits

Native tools expose facts such as food, pawn health, work assignments, rooms, stock and
threats. Python retains native responses and derives the facts used by colony policy.
Several sequential reads are not one atomic world snapshot: the game or player may
change something between them.

This is why missing information stays unknown. A failed food read cannot prove the
colony recovered, and a projected harvest cannot count as food already in storage.
Forecasts help select work, while native observations establish outcomes.

## Priorities select work; goals retain it

The priority tree considers emergencies before routine development. A dangerous injury
can suspend shelter work. Food, wood and temperature use different entry and recovery
thresholds, so small fluctuations do not repeatedly replace the current objective.

A goal retains the intended outcome. A method describes how to pursue it, and steps
describe work that Hands can execute. Keeping these identities lets a later review
continue existing work instead of creating another copy whenever a need is still
present.

Optional storage, defense and resource work competes for a bounded number of new
projects. Observed deficits, player targets and waiting time determine their order.
Accepted work retains its identity when capacity changes, and unavailable methods
yield to other candidates. The dashboard shows why a goal is deferred. The bound uses
available workers as a coarse limit; it does not promise a native completion time.

For example, a food shortage may lead to ordinary wild-plant acquisition. Pending
harvest yield limits additional designations, but the food goal cannot clear until
observations establish adequate supply. An order and its expected yield serve different
purposes from stocked nutrition.

## Time is part of the execution contract

Reviews pause the game to inspect and decide. Execution uses bounded native tick windows
and a renewable wall-clock lease. Native danger, injury and player input can stop the
window before its planned endpoint. The controller must respond to that stop rather than
assuming it still owns permission to advance time.

The lease also protects against a controller that stops responding. Its timeout is
independent of whether Python completes its next review. Exact budgets and stop rules
are in the [session contracts](../reference/session-contracts.md).

## Progress must be observable

Hands retains order receipts, while completion tracking looks for the action's
postcondition. A no-progress watchdog can hold work that is not advancing. A later
observation of tracked completion can release the corresponding hold without replacing
the original action or its evidence.

Colony stability is a conjunction of gates: sleeping capacity, shelter, food,
production, storage, cooking, temperature and other necessities. It can be lost again
when conditions change. A bounded period of stability says something useful about that
run, but it cannot prove arbitrary long-term survival.

## Continue into the implementation

Start with [colony_controller.py](../../controller/rimbot/colony_controller.py),
[colony_policy.py](../../controller/rimbot/colony_policy.py) and
[colony_skills.py](../../controller/rimbot/colony_skills.py). The runtime ties them to
observation and execution in
[bridge_runtime.py](../../controller/rimbot/bridge_runtime.py).

Use [controller contracts](../reference/controller-contracts.md) for the exact priority
order, gates, capacity bounds, hunting screen and current combat scope. Continue with
[plans and Hands](plans-and-hands.md) for the execution side.
