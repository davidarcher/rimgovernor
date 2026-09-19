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
Accepted work holds its slot only while it is worked: the review reads each pawn's
current job and the work type of the giver that issued it, and a commitment whose
profile no pawn is on, while a pawn enabled for it idles or works for another type,
releases its slot after a game hour of that (`labor_idle`) without closing the work;
a pawn back on it takes the slot back. A colony asleep is no evidence either way.

## Execute under supervision

Execution uses bounded native tick windows and a renewable wall-clock
lease. Danger, injury, a coupled order and player input can stop a window
early (the stop tier, #240); lease expiry also stops a controller that
becomes unresponsive. Reviews and routine orders happen at the stop between
windows and under a running window alike (#243, #244): the planners read
one tick-consistent bundle and bind their facts to its tick, and the worker
dispatches every routine kind live; only the window itself is admitted at
the stop.

The scheduler runs independently of dashboard refreshes. Only one review or
execution task runs at a time. Hands yields at its operation budget and requests
continuation. Idle/blocked work and autosave refusals use a two-second retry
backoff; lease renewal and periodic observation remain independent of task
completion.

Delivery from the native clock is a poll on the event journal, not a push:
the transport is request/response only, so the poll's `observations_read_bundle`
(the scope and the events page in one call, like `clock_read_events`) can hold an
empty read for up to `wait_ms` (at most 5 s) and answer as soon as a row lands.
The service holds the read while a window it admitted is running (4 s, the
`serve` bound), so a stop is seen as soon as its row lands; between windows,
where a held read would queue ahead of the planners' reads, it polls unheld
once a second, the cadence a held read that returns empty early also falls
back to. Every
captured page wakes the scheduler step and the routine worker through their
wake signals, which also reset the step backoff; a page whose events carry
attempt outcomes names those actions so the worker reconciles them first, and
while any named action is still unreconciled the worker steps again at once
instead of waiting out its step interval. A
window can be armed with watched attempts: the native supervisor stops it at the
tick boundary on which any of them reaches a terminal outcome
(`STOP_REASON_WATCH_LATCHED`, a benign stop like the tick budget). The
scheduler arms them for a combat window only, the dispatched construction
and haul attempts of the window (the families whose native operation
records observe their own terminal outcome, at most 16), so the fight's
next step starts at the outcome tick. A routine window watches nothing
(#244): a completed order is not a reason to stop the clock, the
`OperationOutcome` row the poll carries wakes the worker under the running
window, and the step records the dispatched attempts the window does not
watch (`unwatched` on the `clock_step` row) as evidence. The one routine
stop is a coupled order, a plan action written against what an earlier
action in the same plan produced (`ActionDependency.Coupled`): the step
pauses the epoch when the prerequisite completes so the order is prepared
against a frozen read of the result, and the next step admits again.
Authority
changes observed while no epoch is running are journaled as
owner-less `AuthorityChanged` rows so a waiting poll learns of them at once.

Each scheduler step carries the reason it ran, and the reason selects the
planners: a step after a settled window, a tick advance or the 30 s safety
net plans everything; a timer step at the same paused tick runs no planner
and only re-evaluates admission from the journal; a wake runs the planners
that dispatch the latched outcomes' action kinds and the readers of any
invalidated fact family (an authority change plans everything); a step
that finds its own window running plans `live` (a full or wake step at
once, a timer step when the safety net is due) and admits nothing. Planner
facts are bound to the tick they observed, so admission holds with
`stale_planning` when they predate the admitted tick by more than the
planning tolerance (`bridge.PlanningTickTolerance`, the tightest fact
family's: 250 ticks) or a window has since outrun them; the scheduler's
`MaxAge` bounds only the admission reads. A routine window runs
`--clock-window-ticks` (default and maximum one game day, 60000 ticks, the
#126 bound) unless danger, a coupled order or player input stops it
earlier: there is no wall-time budget and no `--clock-window-seconds` any
more (#244), since reviews and routine orders happen under the running
window. Each step's flight-recorder `clock_step` row carries the window it
admitted, the reason the step acted on and, for a step a clock stop woke,
the latency from the native stop stamp to the step (`stop_latency_ms`,
#112), which `rimgovernor phases` reports as steps by reason and
stop-to-step latency. Combat windows stay at 300 ticks; a native work
allowance (a growing field, a home fire) still clamps either.

## Manual control

Manual mode is the only thing that pauses controller action. While
`NativeControlAuthority` reads Manual (for example the player took over
during combat), the controller does nothing. Once it reads Auto again the
controller may act on anything on the map immediately, including something
the player just drafted, forced, restricted or placed: there is no
per-subsystem "player owns this, hands off" state and no waiting period.
Colony, load and map changes and stale in-flight snapshots still invalidate
pending work; that is ordinary concurrency safety, not a player-ownership
rule. A pause or letter pause only suspends routine goals and their open
work until control resumes in the same world (see the
[overview](overview.md)): the owned drafts a suspended plan still holds
(a completed draft with unfinished, unfailed work behind it, such as a
combat hold plan's defenders) stay owned through the hold and the resume,
and the next order's claim readback catches a pawn the player undrafted
meanwhile. An explicit Pause releases every owned draft. The game's own
pause on an informational letter (NeutralEvent, PositiveEvent,
NegativeEvent, the classes the native supervisor never stops play for)
stops the window but holds nothing: the next step admits again without a
resume (#228).

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
