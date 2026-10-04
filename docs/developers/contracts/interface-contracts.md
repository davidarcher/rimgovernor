# Interface contracts

[Documentation](../../README.md)

Control, clock and transport contracts the launcher and other clients rely on. The launcher is the only
player surface ([launcher](../architecture/launcher.md)); [the player API](go-player-api.md)
lists the routes `serve` offers.

## Clock pacing

- **Time controls.** Time requests enter Manual, invalidate pending execution and verify a
  native pause (drafted pawns stay drafted) before requesting Normal, Fast or Superfast through
  the supervisor. An in-flight review finishes before a play request; Pause stays available.
  New direction or a load change prevents resuming.
- **Ultrafast and test acceleration.** The clock wire admits Ultrafast. The native tick boost
  behind it (`--clock-test-acceleration`, `StartRequest.test_acceleration`) is refused unless the game
  was launched with `-rimgovernor-test-acceleration`, which only the acceptance profiles (headless and
  rendered) carry. The same argument gates the acceptance-only world patches (`AcceptanceWorld`):
  no autosaver tick and, for a game the `test/quiet_world` fixture op marked, no wild plant or
  animal tick outside the home area and no wild spawners.
- **Player speed.** Autonomous windows run at the player's own speed. Native reports the last speed
  the player chose in the loaded game (`Status.player_speed`: any speed assignment but the
  supervisor's own; unset after a load); `serve` starts each window at it, Ultrafast when none was
  chosen, so there is no speed flag. A speed change inside a running window is an
  `external_speed_changed` stop. `--clock-test-acceleration` pins every window to boosted Ultrafast
  instead. Acceptance sets a slower speed as the player's choice (`rimworld/set_time_speed`, then a
  pause) before `serve` starts (`na.WritePlayerSpeed`).
- **Blind-tick budget** (`--clock-blind-ticks`, `StartRequest.blind_tick_budget`). Blind ticks are
  those the controller has not observed: since its last status or bundle read, or since the oldest
  journal row it has not acknowledged with an events cursor, whichever is older (an events poll
  acknowledges only). Past the budget native clamps `TickManager.TickRateMultiplier` to Normal at the
  next frame and, once the controller reads again, doubles the ceiling every 100 ms back to unlimited.
  Both transitions are journaled as `SpeedChanged` (`regulated_ticks_per_second`, `blind_ticks`) and
  neither ends the epoch or reads as an external speed change. The lease guards a gone controller, the
  budget a slow one. A continuous ceiling (`max_ticks_per_second`) sits beside it; an accelerated epoch
  admits a live ceiling change through `SpeedRequest` while its speed stays Ultrafast.
  `speedmatrix/plain`'s `regulated` row runs uncapped under a 300-tick budget and must match the
  capped speeds' outcome.
- **Player acceleration** (every Ultrafast window without `--clock-test-acceleration`,
  `StartRequest.pacing = PACING_PLAYER_ACCELERATED`) is Ultrafast only and never combines with
  `--clock-test-acceleration`. Native raises Ultrafast's multiplier between 15x and 150x against the
  30 ms frame budget (reported as `Epoch.frame_budget_ms`; the start request carries none): a frame
  whose tick work exceeds it lowers the rate at once, one under 75% of it climbs 25% per 250 ms; the
  game's forced slowdown is kept. Every tick runs the full safety check, so hazard bounds stay
  tick-bounded however many ticks a frame carries.
  - The controller watches each critical planner wave and lowers the epoch's `max_ticks_per_second` so
    a wave fits in half the safe horizon (`--clock-blind-ticks`, default 2500 when unset), with
    hysteresis (changes under 25% ignored, at most one fresh change per 5 s). It parks at Normal only
    when a wave's evidence nears the whole horizon (stale), climbs back at most doubling per fresh
    wave, never releases to full speed while any evidence is stale, and a new window starts at the
    earned ceiling.
  - `Epoch.pacing_reason`/`paced_ticks_per_second` and `Status.effective_ticks_per_second` feed the
    clock_step row and the launcher's Now tab. `speedmatrix/observations`' `player` row checks the
    hazard gaps, the over-budget frame share (the manual-command dispatch bound) and `speed_changes`
    per 6000 ticks.

## Native gesture admission

PlayerInput `LeaseInput`/`SendInput` is player infrastructure outside model capabilities.

- Only a private Linux display and its process-owned game window admit direct input; the bridge
  session establishes ownership.
- An ordered shared-memory mailbox dispatches events on the native main thread without another
  game-order owner.
- Frame age, source, map/load, camera matrices, window state and UI event revision guard gesture
  beginnings. Matching key releases and context-menu right-button releases tolerate their own view
  changes.
- Input is serialized, obsolete moves are coalesced, gesture boundaries are retained, and no
  uncertain event is retried.
- Native eight-second expiry releases held input independently. Cleanup cancels unfinished
  designations before releasing buttons; disconnect, player direction and load changes also release
  input.

## Prepared profiles

A rendered prepared baseline may bypass the native mod mismatch only when the sole missing recorded
mod is the render-only HeadlessRim module. Missing gameplay mods keep the compatibility checks. The
saved game is not modified.
