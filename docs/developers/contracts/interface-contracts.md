# Interface contracts

[Documentation](../../README.md)

Control, clock and transport contracts the launcher and other clients rely on. The launcher is the only
player surface ([launcher](../architecture/launcher.md)); [the player API](player-api.md)
lists the routes the in-process host offers.

## Clock pacing

- **Time controls.** Time requests enter Manual, invalidate pending execution and verify a
  native pause (drafted pawns stay drafted) before requesting Normal, Fast or Superfast through
  the supervisor. An in-flight review finishes before a play request; Pause stays available.
  New direction or a load change prevents resuming.
- **Ultrafast and test acceleration.** The clock wire admits Ultrafast. The native tick boost
  behind it (`BrainCompositionOptions.ClockTestAcceleration`, `StartRequest.test_acceleration`) is refused unless the game
  was launched with `-rimgovernor-test-acceleration`, which only the acceptance profiles (headless and
  rendered) carry. The same argument gates the acceptance-only world patches (`AcceptanceWorld`):
  no autosaver tick and, for a game the `test/quiet_world` fixture op marked, no wild plant or
  animal tick outside the home area and no wild spawners.
- **Player speed.** Autonomous windows run at the player's own speed. Native reports the last speed
  the player chose in the loaded game (`Status.player_speed`: any speed assignment but the
  supervisor's own; unset after a load); the host starts each window at it, Ultrafast when none was
  chosen. A speed change inside a running window is an
  `external_speed_changed` stop. `ClockTestAcceleration` pins every window to boosted Ultrafast
  instead.
- **Target speed.** `BrainCompositionOptions.TargetSpeed` (default Ultrafast) is the speed each window the brain
  starts requests; Ultrafast is the game's own 15x (native never raises it; the top speed fell from 150x to 15x in #3201 until the Run/Hold contract #3177 lands).
  `POST /api/player/clock-speed` (`{"speed":"Normal|Fast|Superfast|Ultrafast"}`) changes it live from the next
  window; a running window keeps its speed. `ClockTestAcceleration` ignores it. Acceptance sets each launch's speed
  (a speed-matrix row's) through that route once the brain is up; it writes no player speed.
- **One ceiling** (`ClockCeiling.cs`, #3171) is the only clamp on a running epoch's tick rate: the lower of a frame-budget input and a brain-heartbeat input; the brain only asks for a
  speed. A frame whose tick work exceeds the 30 ms budget lowers the frame input to 70% of the rate
  it ran at, at once; it climbs 25% per 250 ms only after a 1 s hold and only under 75% of the
  budget, and releases at 9000 ticks/s. The heartbeat is brain contact (a status read, an events
  read, a start or a speed request); none within 5 s drops the heartbeat input to Normal, and
  contact ramps it back by doubling every 100 ms. `Epoch.ceiling_ticks_per_second` reports the
  ceiling (0 = none) and `pacing_reason` names the binding input (`FRAME_BUDGET` or `HEARTBEAT`).
  All of these numbers are conservative defaults, unmeasured until the live baseline (#3199).
  It is the only liveness and frame-budget guard: the brain-side pace backoff, the native
  blind-tick regulator (#3174), native player acceleration (`PACING_PLAYER_ACCELERATED`, the
  15x-to-150x raise on frame time) and the owner's `max_ticks_per_second` (#3201) are gone; the
  wire carries neither field. An explicit beat arrives with the Run/Hold contract (#3177).
- **Rates.** Native never raises a speed: a player epoch ticks at the game's own multiplier
  (Ultrafast 15x) under the one ceiling; only a `ClockTestAcceleration` epoch runs the 150x boost
  and runs the full safety check before every tick, so hazard bounds stay tick-bounded however many
  ticks a frame carries. Every epoch holds `DebugViewSettings.neverForceNormalSpeed`, so the game's
  forced slowdown (danger holding the tick rate at Normal for 800 ticks) never applies.
  - `Epoch.pacing_reason` (`FIXED`, `FRAME_BUDGET` or `HEARTBEAT`)/`paced_ticks_per_second` and
    `Status.effective_ticks_per_second` feed the clock_step row and the Observe tab's Now view.
    `speedmatrix/observations`' `player` row checks the hazard gaps, the over-budget frame share (the
    manual-command dispatch bound) and `speed_changes` per 6000 ticks.

## Prepared profiles

A rendered prepared baseline may bypass the native mod mismatch only when the sole missing recorded
mod is the render-only HeadlessRim module. Missing gameplay mods keep the compatibility checks. The
saved game is not modified.
