# Interface contracts

[Documentation](../../README.md)

Control, clock and transport contracts the launcher and other clients rely on. The launcher is the only
player surface ([launcher](../architecture/launcher.md)); [the player API](player-api.md)
lists the routes the in-process host offers.

## Observation scope

The brain's observation identity retains the native `ObservationContext` reference;
typed colony, load, map and tick accessors read that context directly. Pause state
comes from the lifecycle reply. Generation is `Unclaimed` before the first authority
grant or `At` an allocated generation; an unclaimed generation cannot confirm a
planner boundary. Missing context or generation, malformed IDs, negative map/tick
values and generation zero fail the read.

A lifecycle reading carries this identity and its measured start/end timestamps.
The host checks that interval for freshness and reads pause from the same identity.

## Clock speed and the one ceiling

How the clock is owned and held is in the [control loop](../architecture/control-loop.md#execute-under-supervision)
and the [clock wire contract](../../../contracts/wire/clock-lifecycle.md). This section is what a client of the
speed and its clamp relies on.

- **Who owns the clock.** The bot owns it exactly while it is on (`state.enabled`). Resume gives the clock to the
  bot, and the in-game speed buttons then only request a speed (below) while in-game pause is the take-over; Pause holds the clock once (`Hold(Review)`) and the
  player is free to unpause. The bot never follows or re-takes a speed the player chose, and there is no
  player-set speed ceiling. See [player API](player-api.md#control-api-and-launcher).
- **Target speed.** `BrainCompositionOptions.TargetSpeed` (default Ultrafast) is the speed the brain asks for on
  every `Run`. `POST /api/player/clock-speed` (`{"speed":"Normal|Fast|Superfast|Ultrafast|Hyperfast"}`) changes it live and
  the next step sends `Run` at the new speed. Acceptance sets each launch's speed (a speed-matrix row's) through
  that route once the brain is up. While a fight is open and the combat resume-latency gate has tripped, the brain
  asks for the slower fallback speed instead (control loop, Combat).
- **The player's in-game speed request.** The in-game time controls call native's `RequestSpeed` (game thread;
  Normal through Hyperfast, undefined speeds throw). It records the speed and bumps a serial, which `Status` reports as
  `playerRequest` (`none` until the loaded game's first request, then `requested` with speed and serial; the
  serial starts again with each loaded game), and publishes a `SpeedRequested` colony event (`speed`). The event is
  only a wake (a quiet step, cause Review, when no hold is pending): the request lives in `Status`, so a lost event
  costs latency and the next read still finds it. The run driver adopts a serial it has not adopted for that load
  (any pending one at its first read) into the same live target the player-API route sets, and the step's `Run`
  carries it. Native never changes its own running speed for a request. Under `ClockTestAcceleration` the pinned
  Hyperfast ignores it, as it ignores the route.
- **Run fields.** A `Run` carries the identity, `expected_generation`, `speed`, the complete `policy` and
  `planned_hold_serial`: the `Status.hold_serial` of the read its plan came from. A client that drives the clock
  over `/native` (the acceptance harness) reads the status first and quotes its serial; a resume after a hold the
  client did not read is refused as `Unavailable` and the clock stays held
  ([clock wire contract](../../../contracts/wire/clock-lifecycle.md#clock-validation)).
- **Ultrafast and Hyperfast.** Both run the game's `TimeSpeed.Ultrafast` and differ only in the static
  `TickManager.UltraSpeedBoost`, which the run segment owns and restores at its hold (`ClockRunSpeed.cs`), so native
  records the wire speed and `Status.target_speed` reports it. Ultrafast is the game's own 15x (900 ticks/s): it owns
  the boost as off, because the host tools' Hyperfast sets it globally. Hyperfast owns the boost as on, which makes
  the game ask for 20000x, and a hard cap holds it at 30x (1800 ticks/s; `HyperfastCap.cs`, in the
  `TickRateMultiplier` postfix, separate from the one ceiling, which still applies beneath it inside a run segment).
  Native installs that postfix at startup and caps any boosted multiplier, with or without a run segment, so the
  host tools' `set_time_speed`/`play_for` Hyperfast (Ultrafast with the boost on) runs at 30x too. Neither has a launch
  gate; a `Run(Hyperfast)` is refused as `Unavailable` where the game does not provide the boost. A change between
  them is not an external speed change. The `-rimgovernor-test-acceleration` launch argument gates only the
  acceptance-only world patches (`AcceptanceWorld`): no autosaver tick and, for a game the
  `test/quiet_world` fixture op marked, no wild plant or animal tick outside the home area and no wild spawners.
  The acceptance profiles (headless and rendered) alone carry it. `BrainCompositionOptions.ClockTestAcceleration`
  pins the target to Hyperfast; a case's `RIMGOVERNOR_UNPIN_TEST_SPEED` turns off only that pin.
- **One ceiling** (`ClockCeiling.cs`) is the only clamp on a running clock's tick rate: the lower of a frame-budget
  input and a brain-heartbeat input. The brain only asks for a speed.
  - *Frame budget.* A frame whose tick work exceeds the budget lowers the frame input to 70% of the rate it ran at,
    at once. It climbs 25% per 250 ms, only after a 1 s hold and only while a frame stays under 75% of the budget,
    and releases at 1800 ticks/s (Hyperfast's rate).
  - *Heartbeat.* The brain's driver sends an explicit `Heartbeat` every second (a `Run` also counts as contact;
    reads do not). Silence past the tolerance drops the heartbeat input to Normal (60 ticks/s); once the brain is
    heard again it doubles back every 100 ms. Losing the beat is neither journalled nor a hold.
  - *Unmeasured.* The 30 ms frame budget, the 0.7 / 1.25 steps, the 5 s tolerance and the 1 s interval are
    conservative defaults. The live baseline that would tune them is #3199; until it is recorded they are guesses.
- **Rates.** Native never raises a speed beyond what `Run` asked for. Every run holds
  `DebugViewSettings.neverForceNormalSpeed`, so the game's forced slowdown (danger holding the tick rate at Normal
  for 800 ticks) never applies, and Hyperfast runs the full safety check before every tick, so hazard
  bounds stay tick-bounded however many ticks a frame carries.
  - `Status.pacing_reason` (`FIXED`, `FRAME_BUDGET` or `HEARTBEAT`) names the binding input;
    `paced_ticks_per_second`, `ceiling_ticks_per_second` (0 = none) and `effective_ticks_per_second` feed the
    `clock_step` row and the Observe tab's Now view. `Status` carries no other accounting: the frame account,
    the pause/run split, the probe and digest work and the hazard gaps are the acceptance-only
    `rimgovernor/acceptance_read_clock_accounting` read (`AcceptanceReadTools`), which production never calls.
  - The over-budget frame share and the queued-command dispatch wait (the manual-command dispatch bound) have no
    check; the hazard gaps are checked only by the `hazard/*` cases (one staged Hyperfast run per class).

## Native facade refusals

How an `INativeFacade` call reports that it did not answer ([native facade](../architecture/native-facade.md)).

- **Reads throw.** A converted read (identity, tick, governor state, authority status, the clock calls and the
  presentation reads) returns its payload, and a refusal throws `NativeFailureException` with the wire `Failure`, an
  unavailable game `NativeUnavailableException` or `NotLoadedException`. Over `POST /native/<op>` they are a 422 and a
  503 with `{"code","detail"}` ([HTTP channel](../architecture/http-channel.md)).
- **Apply keeps a typed refusal.** `ApplyAsync` still answers an `ApplyReply`: a batch failure and each intent's
  `Refused` result are outcomes read per intent beside the applied ones, not a fault of the whole call, so they
  stay values.

## Facts records

How a colony fact travels from native to the planner once its family is total (epic #3753; the delivery ledger
is the first, #3763). There is no unknown: a fact is read, or it is a named state, or the planner does not plan
the option.

- **One record per concept, in Contracts.** The record lives in `brain/src/RimGovernor.Brain.Contracts`
  (`RimGovernor.Contracts.Wire.Observations`), named in the planner's vocabulary (`DeliveryLedger`, `Kill`,
  `Butcher`). Native builds it, `WireJsonContext` carries it over `/native/<op>`, and Policy reads it directly.
  There is no Policy or Observation copy and no decoder; a Policy rule over it is a function taking the record.
- **Every member `required`, none nullable.** A record built without a member does not compile (CS9035), and
  JSON without one does not decode. Measured values are plain (`long`, `double`, `string`); a nullable reference
  is allowed only for a real relation (a pawn's equipped weapon). Domain types replace wire shapes: `Tick`,
  `PawnId` and `Cell`, written as a bare number, a bare id string and `{x,z}`.
- **Enums.** One enum per concept, declared in Contracts (or in Domain when a Domain record already carries it, as with `StockpilePriority`, `FilterBase` and `Quality`) and used by Policy and native alike, with no `Unknown`
  or `Unspecified` member. A genuine native state such as `Passion.None` remains explicit. `Wire.Name` uses the
  declared wire name when present and otherwise derives snake_case; removing a redundant attribute must preserve
  that serialized value.
- **A read that cannot be produced throws.** The native producer returns the whole record or throws; it never
  returns a partial or empty one. A throw inside the colony facts read fails that read
  (`NativeColonyObservationTools.Read`); there is no per-section "absent" wrapper.
- **The container holds the record.** `ColonyFactsSnapshot` carries the section as a `required` member of the
  record's type. `ColonyReading` retains that snapshot, its canonical tables and observation identity; readers
  access those native records directly. Derived planning inputs belong to the existing `RoundsFacts` review,
  and geometry planning requires the reading's observed planning window. The review retains the native `ThreatFacts`
  census through its canonical `ColonyFactsSnapshot` reference. Population, beds, temperature and work-resource
  inputs come from that same snapshot; raid points, wealth, factions, storyteller, disease and trader inputs
  come from its native threat section. The review stores no second scalar census. Economy, forecast and medicine
  inputs retain that same census for observed stock, physical resource ids and population. Pure stock queries
  convert native quantity rows for a policy calculation; proposed inventory changes and material budgets
  remain separate derived inputs.
  Every threat census includes both disease and trader reads; a missing read fails instead of becoming a forecast finding.
  A state that may legitimately be missing is a
  named case: the sleeping range is `Indoor` or
  `NoIndoorBed`; a pawn's need is `NeedLevel.Has(level)` or `Lacks` (an animal has no joy), and its food need
  `FoodNeed.Has(level, hunger)` or `Lacks` ([rule 9](../architecture/rules.md#review-items)).
- **A typed read returns its payload.** `INativeFacade` returns the record (`ListPawnsAsync` returns
  `PawnSnapshot`) and the native side throws `NativeFaults.Refused` or `NativeFaults.Unavailable`; there is no
  reply-outcome union.

## Prepared profiles

A rendered prepared baseline may bypass the native mod mismatch only when the sole missing recorded
mod is the render-only HeadlessRim module. Missing gameplay mods keep the compatibility checks. The
saved game is not modified.
