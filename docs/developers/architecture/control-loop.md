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

Optional projects (comfort, research, production targets, defense, expansion)
compete for bounded capacity based on measured deficits, player targets, waiting time,
labor contention and observed outdoor risk. Each goal declares the native work types
that can serve it; admission is bounded by both the project limit and free pawns of
those types. Accepted work keeps its identity as capacity changes; unavailable methods
yield to other candidates. Work displays the reason for deferral, including the
bottleneck work type. Worker capacity is a scheduling bound, not a completion-time
guarantee, and waiting age alone overtakes any deficit gap within a fixed tick bound.

## Execute under supervision

Reviews pause the game. Execution uses bounded native tick windows and a renewable
wall-clock lease. Danger, injury and player input can stop a window early; lease
expiry also stops a controller that becomes unresponsive.

The scheduler runs independently of dashboard refreshes. Only one review or
execution task runs at a time. Hands yields at its operation budget and requests
continuation. Idle/blocked work and autosave refusals use a two-second retry
backoff; lease renewal and periodic observation remain independent of task
completion.

Delivery from the native clock is a long poll on the event journal, not a push:
the transport is request/response only, so `clock_read_events` holds an empty
read for up to `wait_ms` (at most 5 s) and answers as soon as a row lands. Every
captured page wakes the scheduler step and the routine worker through one shared
wake signal, which also resets the step backoff; a page whose events carry
attempt outcomes names those actions so the worker reconciles them first. A
window can be armed with watched attempts: the native supervisor stops it at the
tick boundary on which any of them reaches a terminal outcome
(`STOP_REASON_WATCH_LATCHED`, a benign stop like the tick budget), so a completed
wall does not play out the rest of a 600-tick budget before the controller
notices. Authority changes observed while no epoch is running are journaled as
owner-less `AuthorityChanged` rows so a waiting poll learns of them at once.

## Verify progress

Hands records receipts; completion tracking checks native postconditions. A
no-progress watchdog can hold stalled work. Observed completion can release that
hold without replacing the action or its evidence.

Stability requires sleeping capacity, shelter, food, production, storage, cooking,
temperature and other gates together. Changed conditions can invalidate stability.
Sustained coverage belongs in campaigns and
[GitHub issues](https://github.com/davidarcher/rimgovernor/issues).

The gated [Go routine components](../../../go/README.md#routine-policy-components)
persist maintained goals, method reservations and action dependencies. Building
methods reserve all costs atomically against the shared journal; Hands rechecks
native placement and observed predecessor completion before execution. Runtime
composition for additional methods and method selection remain tracked in G01.05. Go routine reviews
persist need assessments and hysteresis together; missing facts cannot recover
goals. Manual cancels pending work independently of observation availability.

The opt-in Go routine building worker shares the selected player's direction and
native lease. Its journal verifies each method's current review, goal, epoch and
world before dispatch; it cannot run arbitrary plans or acquire authority. Pending
player work takes priority. Routine building work can keep finite clock windows
eligible after the selected player plan settles. Uncertain effects still reconcile
after cancellation, while observed terminal building outcomes yield accounting to
fresh native stock and placement facts.

Comfort joins that shared path through native access and use observations. Its
durable history distinguishes completed furniture from ordinary dining and
recreation use. After construction, a bounded clock allowance lets pawns use the
facilities; it expires from the original completion tick and cannot renew through
polling or transfer to a new player direction.
