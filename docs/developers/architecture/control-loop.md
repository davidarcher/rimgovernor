# Control loop

[Architecture](overview.md) · [Controller contracts](../contracts/controller-contracts.md)

The controller observes needs, selects bounded work, advances supervised game time
and checks the outcome. Routine operation requires no model calls.

## Observe and prioritize

Native tools supply food, health, assignments, rooms, stock and threats. Sequential
reads may span world changes; missing information stays unknown. Forecasts help
choose work but cannot count projected harvests as stored food.

Emergencies preempt development. Food, wood and temperature use separate entry and
recovery thresholds to avoid replacing goals on small fluctuations. Goals retain
outcomes, methods retain approaches and steps identify executable work across reviews.

Optional projects compete for bounded capacity based on observed deficits, player
targets and waiting time. Accepted work keeps its identity as capacity changes;
unavailable methods yield to other candidates. Work displays the reason for deferral.
Worker capacity is a scheduling bound, not a completion-time guarantee.

## Execute under supervision

Reviews pause the game. Execution uses bounded native tick windows and a renewable
wall-clock lease. Danger, injury and player input can stop a window early; lease
expiry also stops a controller that becomes unresponsive.

A notification-driven scheduler runs independently of dashboard refreshes. Direction
changes and review completion wake it immediately. Hands yields at its operation
budget and requests continuation. Only one review or execution task runs at a time.
Idle/blocked work and autosave refusals use a two-second retry backoff. Native clock
journal reads notify the scheduler; lease renewal and periodic observation remain
independent of task completion. See [session contracts](../contracts/session-contracts.md).

## Verify progress

Hands records receipts; completion tracking checks native postconditions. A
no-progress watchdog can hold stalled work. Observed completion can release that
hold without replacing the action or its evidence.

Stability requires sleeping capacity, shelter, food, production, storage, cooking,
temperature and other gates together. Changed conditions can invalidate stability.
Sustained coverage belongs in [campaigns](../testing/campaigns.md) and the
[backlog](../../BACKLOG.md).

Implementation: `colony_controller.py`, `colony_policy.py`, `colony_skills.py` and
`bridge_runtime.py` under [controller/rimgovernor](../../../controller/rimgovernor).
The gated [Go routine components](../../../go/README.md#routine-policy-components)
persist maintained goals, method reservations and action dependencies. Building
methods reserve all costs atomically against the shared journal; Hands rechecks
native placement and observed predecessor completion before execution. Runtime
composition and method selection remain tracked in G01.05. Go routine reviews
persist need assessments and hysteresis together; missing facts cannot recover
goals. Manual cancels pending work independently of observation availability.
