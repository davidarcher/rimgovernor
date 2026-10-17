# Clock and lifecycle contracts

| Contract | Required behavior |
| --- | --- |
| `Run`, `Hold`, `Heartbeat` | Continuous run at a target speed; a named hold ends only with a `Run`; no attempt key, no epoch, no tick budget. |
| `clock.Clock` | One supervised clock owner; autonomous execution never changes speed outside it. `SPEED_ULTRAFAST` is an ordinary wire speed (test acceleration via `-rimgovernor-test-acceleration`, `Status.test_acceleration_available`). |
| `clock.Event`, `EventsPage` | Immutable typed events (`HoldEntered`, `Resumed`), explicit gaps, journal failure holds play. |
| `lifecycle.Lifecycle.ReadIdentity` | Saved colony ID, fresh unsaved load token, current map/tick and pause state. Initialization belongs load hooks, not identity reads. |
| `lifecycle.Lifecycle.Save` | Explicit session action; completed save, unchanged identity/direction/tick and observed pause. |
| `lifecycle.Lifecycle.NewColony` | `NewColonySpec` is a required base `scenario` plus typed scenario parts (`starting_things`, `forced_traits`, `forced_hediffs`, `naked`, `set_need_levels`, `perma_game_conditions`, `disallowed_buildings`, `disabled_incidents`) and mandatory-part overrides (`player_faction`, `arrival_method`; the pawn count is `colonist_count`). Native builds game ScenParts from them (`NativeNewColonyScenarioParts`) and puts them on the copy of the scenario before the colonists are generated. Parts replace by kind: a non-empty list removes the base scenario's parts of that kind (all three `starting_things` placements are one kind) and a list left empty keeps the base's, so Crashlanded plus a full list yields exactly that list. It refuses, naming the field, an unknown defName, a trait degree the trait lacks, a hediff that is not `scenarioCanAdd` or above its editor cap, a non-major need, a part that cannot coexist with another, or a stuff/quality the thing does not take. `perma_game_conditions` accepts any condition def, not only `canBePermanent` ones (ColdSnap works); 1.6 has no map-scoped permanent part. |
| `lifecycle.Lifecycle.Load` | Distinguish pending/map/visual readiness, reread fresh native context. |
| `lifecycle.Lifecycle.Menu` (`rimgovernor/lifecycle_menu`, `rimgovernor/lifecycle_read_menu`) | End the loaded game without saving, as the player's quit-to-menu does (`GenScene.GoToMainMenu`); the process stays up. `MenuPending` until the game is at a fresh main menu (Entry program state, no game, no queued long event), the precondition NewColony starts from; then `MenuCompleted`. Already at a fresh main menu, it completes at once with `unloaded` false. |
| the brain gamehost process adapter | the harness owns process lifetime. No second native process manager. Stop only owned instance; detach attached sessions. |

## Clock validation

`Run` requires current authority (identity plus the expected native generation), one ordinary speed and a complete
watch policy. Policy fractions must be finite in 0.01–1, hostile distance finite in 1–250, cooldown 0–1800000 ms.
Each exact pawn ID set has at most 256 entries without duplicates. Medical-rest exceptions require their bounded
ticks. Native eligibility is checked on every sweep; acknowledgements cannot disable death or severe-health checks.
Unknown numeric enum values, omitted required fields and unsupported policies are refusals.

`Run` is idempotent and has no attempt key: sent while Running it changes the speed or the policy in place, sent while
Held it resumes. There is no tick budget and no deadline: Running continues until a hold. `Hold` names a `HoldReason`;
a `Hold` while already held keeps the first reason. After a native hazard hold, native stays held until the brain
sends `Run`; a native never resumes by itself. `Run` is refused (`Unavailable`) while a game-forced pause (a letter,
a dialog) is active.

`Status.hold_serial` is bumped on the game thread by every hold entry, native hazards and the brain's own
`Hold` alike (a failed pause's retry is the same hold). `Run` requires `planned_hold_serial`, the serial of the read
its plan came from. Resuming a held clock is refused (`Unavailable`, the hold stays) when the serial has moved since,
so a hold the planner did not see is never released; a `Run` that only changes a running clock's speed or policy is
not guarded. A serial and not a tick: a hold pauses the game, so a read before and after a same-tick hold carry the
same tick.

`Heartbeat` is explicit and carries the identity. Losing it is neither journalled nor a hold: it lowers the clock
ceiling to Normal until the next beat. `SPEED_ULTRAFAST` uses the one ceiling's rate with no launch gate; the native
tick boost behind it is `Status.test_acceleration_available`, set only by a game launched with
`-rimgovernor-test-acceleration`, which the acceptance harness (`RimGovernor.cmd acceptance`) adds to its acceptance
profiles alone.

`HoldReason` is fine-grained: every native `StopReason` (health, hostile, downed, combat event, force-pause waits,
dialog pause and the like) plus the brain's own `COUPLED_ORDER`, `REVIEW` and `AUTOSAVE`, the `GAME_FORCED` pause and
`NEVER_RUN`. `Status` is flat: `Running`, `Held` and `Unavailable` are exclusive, the held reason and the actual
pause describe the current clock, and `Unavailable` carries a reason. Consumers needing pause
require fresh actual-paused facts for the same identity. An uncertain transport response requires a status read,
never a blind replay of anything but the idempotent `Run`. Clearing a modal does not resume the clock. Acknowledgement
belongs to brain inspected-event handling; there is no native acknowledge/delete/resume alias.

The journal is read with `ReadEvents`: limits 1–128, rows strictly after the requested cursor, never held. An invalid
or regressed cursor is an explicit failure; missing history reports loss. A complete page never silently drops rows.
Typed payloads are `HoldEntered` (reason, evidence, detected tick, occurrence tick) and `Resumed` (speed, from), plus
notification/alert, injury/health, hostile/downed/predator/hunting/medical-rest evidence, authority changes,
unavailable/watcher/journal errors, observation invalidations and `DialogPause` (window id/type/title) when a
game-opened `Verse.Dialog_NodeTree` force-pauses the clock. A dialog hold is non-benign; the brain answers the dialog
through a `DialogIntent` on Actions/Apply and sends `Run`. Source numeric pawn IDs must resolve to exact canonical
IDs, never suffix matching. Every event carries its original native context, not whichever map happens to be selected
during later reads.

Durable event timestamps are Unix milliseconds for diagnostic chronology. The native journal is an in-memory buffer
of the game process; there is no clock attempt ledger, receipt or epoch, and the brain persists no clock state beyond
its event inbox.

## Lifecycle validation

Save names identify a single native save, not a path: nonblank, at most 128 UTF-8
bytes, no NUL, directory separators, dot-only components or platform-invalid
filename characters. The owned profile resolves the path. Saves require explicit
current player direction, Manual, verified pause and exact
expected tick. `SaveCompleted` requires a nonempty complete native file and the
same observed identity/direction/tick afterward. It does not publish a paired
brain checkpoint; the brain verifies and durably publishes its own
snapshot. A potentially started failed save is `uncertain`, not pre-admission
failure, and needs file/context inspection before retry.

Load timeout is 1000–120000 ms. Map readiness and optional visual readiness are
distinct; completion carries the newly observed identity. A timeout after dispatch
is `pending`, with unavailable readiness facts absent. Do not retry loading from
an ambiguous acknowledgement. Load starts Manual and invalidates authority,
input/UI captures, pending actions and observation cursors (drafts are plan-owned and carry no native claim to invalidate). There
is no saved-lease restoration, old-format conversion or compatibility override.
The lifecycle facade uses the existing SDK implementation; its method descriptor
does not imply another in-game loader or native process server.

Menu requires a `request_id`. Ending a live colony map requires `expected_player` with
that map's current identity and a nonzero direction, as replacing one by Load does. Menu
refuses as `UNAVAILABLE` while any long event is queued, because the menu return would
clear that event: a load, a new colony phase or a save. It also refuses while another
menu request is still running. A retry of a known request id answers that request's
status. ReadMenu of an unknown request id is `NOT_FOUND`. The ledger keeps 32 entries
and prunes completed ones first. Ending the game revokes native authority as
`REVOCATION_REASON_SHUTDOWN`, like any orderly game end.

Load admission validates the exact owned/attached instance and trusted current
player direction; replacing a live map additionally requires `expected_player`
with its current identity. Before-map admission still requires instance and
direction. A queued load is invalidated by another load, disconnect or new player
direction. `request_id` correlates an invocation and grants no authorization.
ReadLoad/ReadSave inspect the exact request in the same instance without granting
control. A superseded load is explicit; observing some loaded map cannot prove
that an earlier request reached its requested readiness. An unknown request yields
NotFound and proves no absence of effects. Retain records for the instance
lifetime within a bounded4096-entry ledger, refusing new admissions when full.
