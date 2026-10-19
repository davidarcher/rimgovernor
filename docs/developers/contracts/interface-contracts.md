# Interface contracts

[Documentation](../../README.md)

Control, clock and transport contracts the launcher and other clients rely on. The launcher is the only
player surface ([launcher](../architecture/launcher.md)); [the player API](player-api.md)
lists the routes the in-process host offers.

## Clock speed and the one ceiling

How the clock is owned and held is in the [control loop](../architecture/control-loop.md#execute-under-supervision)
and the [clock wire contract](../../../contracts/wire/clock-lifecycle.md). This section is what a client of the
speed and its clamp relies on.

- **Who owns the clock.** The bot owns it exactly while it is on (`state.enabled`). Resume gives the clock to the
  bot and native swallows the player's speed and pause input; Pause holds the clock once (`Hold(Review)`) and the
  player is free to unpause. The bot never follows or re-takes a speed the player chose, and there is no
  player-set speed ceiling. See [player API](player-api.md#control-api-and-launcher).
- **Target speed.** `BrainCompositionOptions.TargetSpeed` (default Ultrafast) is the speed the brain asks for on
  every `Run`. `POST /api/player/clock-speed` (`{"speed":"Normal|Fast|Superfast|Ultrafast"}`) changes it live and
  the next step sends `Run` at the new speed. Acceptance sets each launch's speed (a speed-matrix row's) through
  that route once the brain is up. While a fight is open and the combat resume-latency gate has tripped, the brain
  asks for the slower fallback speed instead (control loop, Combat).
- **Run fields.** A `Run` carries the identity, `expected_generation`, `speed`, the complete `policy` and
  `planned_hold_serial`: the `Status.hold_serial` of the read its plan came from. A client that drives the clock
  over `/native` (the acceptance harness) reads the status first and quotes its serial; a resume after a hold the
  client did not read is refused as `Unavailable` and the clock stays held
  ([clock wire contract](../../../contracts/wire/clock-lifecycle.md#clock-validation)).
- **Ultrafast.** Native serves Ultrafast with the game's tick boost (150x, 9000 ticks/s) driven up to the one
  ceiling, with no launch gate; a `Run(Ultrafast)` is refused as `Unavailable` where the game does not provide the
  boost. The `-rimgovernor-test-acceleration` launch argument only reports `Status.test_acceleration_available`
  and gates the acceptance-only world patches (`AcceptanceWorld`): no autosaver tick and, for a game the
  `test/quiet_world` fixture op marked, no wild plant or animal tick outside the home area and no wild spawners.
  The acceptance profiles (headless and rendered) alone carry it. `BrainCompositionOptions.ClockTestAcceleration`
  pins the target to Ultrafast.
- **One ceiling** (`ClockCeiling.cs`) is the only clamp on a running clock's tick rate: the lower of a frame-budget
  input and a brain-heartbeat input. The brain only asks for a speed.
  - *Frame budget.* A frame whose tick work exceeds the budget lowers the frame input to 70% of the rate it ran at,
    at once. It climbs 25% per 250 ms, only after a 1 s hold and only while a frame stays under 75% of the budget,
    and releases at 9000 ticks/s.
  - *Heartbeat.* The brain's driver sends an explicit `Heartbeat` every second (a `Run` also counts as contact;
    reads do not). Silence past the tolerance drops the heartbeat input to Normal (60 ticks/s); once the brain is
    heard again it doubles back every 100 ms. Losing the beat is neither journalled nor a hold.
  - *Unmeasured.* The 30 ms frame budget, the 0.7 / 1.25 steps, the 5 s tolerance and the 1 s interval are
    conservative defaults. The live baseline that would tune them is #3199; until it is recorded they are guesses.
- **Rates.** Native never raises a speed beyond what `Run` asked for. Every run holds
  `DebugViewSettings.neverForceNormalSpeed`, so the game's forced slowdown (danger holding the tick rate at Normal
  for 800 ticks) never applies, and the boosted rate runs the full safety check before every tick, so hazard
  bounds stay tick-bounded however many ticks a frame carries.
  - `Status.pacing_reason` (`FIXED`, `FRAME_BUDGET` or `HEARTBEAT`) names the binding input;
    `paced_ticks_per_second`, `ceiling_ticks_per_second` (0 = none) and `effective_ticks_per_second` feed the
    `clock_step` row and the Observe tab's Now view. `Status` carries no other accounting: the frame account,
    the pause/run split, the probe and digest work and the hazard gaps are the acceptance-only
    `rimgovernor/acceptance_read_clock_accounting` read (`AcceptanceReadTools`), which production never calls.
  - The over-budget frame share and the queued-command dispatch wait (the manual-command dispatch bound) have no
    check; the hazard gaps are checked only by the `hazard/*` cases (one staged Ultrafast run per class).

## Prepared profiles

A rendered prepared baseline may bypass the native mod mismatch only when the sole missing recorded
mod is the render-only HeadlessRim module. Missing gameplay mods keep the compatibility checks. The
saved game is not modified.
