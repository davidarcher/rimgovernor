# Clock and lifecycle contracts

These are the shared adapter contract. Runtime migration and native acceptance
remain in N01; schema compilation alone does not establish these behaviors.

| Current boundary/source | Contract | Required behavior |
| --- | --- | --- |
| `home/supervised_play`: `status`, `events` in `SupervisedPlayTool.cs` | `clock.Clock.ReadStatus`, `ReadEvents` | Read-only, current identity, complete typed state and journal rows. |
| `start`, `pause`, `heartbeat`, `speed` in the same source | `Start`, `Pause`, `Renew`, `ChangeSpeed` | Exact epoch ownership, bounded native tick budget, no lease stealing or automatic reacquisition. |
| `home/play_until_event` and consumed ordinary `rimworld/set_time_speed` calls | Migrate supervised consumers to `clock.Clock` | One supervised clock owner. Remove bypass calls from autonomous execution; fixture acceleration remains outside production. |
| `Supervisor.Add`, `ClockEventJournal.cs` | `clock.Event`, `EventsPage` | Immutable typed events, explicit gaps, journal failure holds play. |
| New guarded clock admission and receipt lookup | `clock.ControlReceipt`, `ReadAttempt` | Shared attempt namespace and retained clock outcome; no retry inferred from missing receipt. |
| `home/colony_identity`, Runtime `Persistence/ColonyIdentity.cs` | `lifecycle.Lifecycle.ReadIdentity` | Saved colony ID, fresh unsaved load token, current map/tick, explicit capability availability. Initialization belongs load hooks, not identity reads. |
| `rimworld/save_game`, `session_checkpoint.py:create_checkpoint` | `lifecycle.Lifecycle.Save` | Explicit session action; completed save, unchanged identity/direction/tick and observed pause. |
| `rimworld/load_game_ready`, `bridge_runtime.py` | `lifecycle.Lifecycle.Load` | Existing SDK lifecycle adapter; distinguish pending/map/visual readiness, reread fresh native context. |
| GABS `games_start`, `games_stop`, process attach/connect | Existing external GABS typed process adapter | GABS owns process lifetime. No second native process manager. Stop only owned instance; detach attached sessions. |

## Clock validation

`Start` requires current authority and one ordinary speed, a complete watch policy,
lease 1000–30000 ms and tick budget 1–1800000. Policy fractions must be finite in
0.01–1, hostile distance finite in 1–250, cooldown 0–1800000 ms. Each exact
pawn ID set has at most 256 entries without duplicates. `watched_attempts` has at
most 16 distinct complete attempt keys, each resolving to a tracked construction
operation under the requesting identity (the only watchable family so far); an
attempt already terminal at start is reported once as `OperationOutcome` and not
armed, and the first armed attempt to reach a terminal outcome stops the epoch
at that tick boundary as `STOP_REASON_WATCH_LATCHED`. Medical-rest exceptions
require a budget at most 600 ticks. Native eligibility is checked on every sweep;
acknowledgements cannot disable death or severe-health checks. Unknown numeric
enum values, omitted required fields and unsupported policies are refusals.

Renewal and speed changes retain the original tick deadline. Pause is permitted
for the exact identity/session/epoch even after automation revocation so cleanup
can stop owned play. It cannot affect a replacement epoch or another owner.
Epochs are positive signed 64-bit values; cursor zero is the initial position.
Cursor arithmetic must not overflow. `ReadEvents` limits are 1–128 and rows are
strictly after the requested cursor; `wait_ms` (0–5000) holds an empty read until
a row lands or the wait lapses and never delays a page that has rows or loss. An invalid or regressed cursor is an explicit
failure; missing history reports loss. A complete page never silently drops rows.

`Running`, `Stopping`, `Stopped`, `NeverStarted` and `Unavailable` are exclusive.
A failed pause stays armed in `Stopping`; inactive does not prove paused.
Top-level observed speed/pause describes the actual current clock even when no
epoch has started or a running epoch is temporarily force-paused. Stop-specific
`pause_verified` describes the original stop, not an everlasting pause guarantee.
Consumers needing pause require fresh actual-paused and pause-verified facts for
the same identity. A tick-budget/pause race is reconciled by exact epoch and stop
reason plus a fresh clock observation. An uncertain transport response requires
inspection, never blind start/speed replay. Clearing a modal does not authorize a
new epoch. Acknowledgement belongs controller inspected-event handling; there is
no native acknowledge/delete/resume alias.

Typed journal payloads cover started/speed, notification/alert, injury/health,
hostile/downed/predator/hunting/medical-rest, hostiles-cleared, tick budget,
watch-latched with its operation outcome, authority changes (owner-less when
observed outside an epoch), pause-failure, external pause/speed, lease,
unavailable/watcher/journal errors and force-pause waiting/cleared events. Source numeric pawn IDs must resolve to exact
canonical IDs, never suffix matching. Every event carries its original native
context, not whichever map happens to be selected during later reads.

Lease durations use a monotonic clock within the native process lifetime. Current
`Supervisor.NowMs` reads UTC; the adapter/runtime migration must use a monotonic
clock for lease expiry. Durable event/stop timestamps remain Unix milliseconds
for diagnostic chronology; wall-clock jumps never change lease expiry. Stop
snapshots retain original epoch identity; observed current context makes identity
replacement explicit.

Start/renew/speed use the common attempt namespace and the same 4096-entry,
unsaved, non-evicting ledger as ordinary guarded operations. Record admission
before clock or lease changes. Equality compares the complete typed request,
including the method and immutable original preconditions. An identical attempt
replays its receipt; changed reuse is conflict. The clock receipt contains original
admission context, authorizing owner and applied or uncertain state. An applied
start may immediately stop on a safety finding; inspect its actual status.
Read-only attempt lookup survives authority revocation within that load and never
grants authority. Unknown records require reconciliation. Pause uses exact epoch
ownership and is idempotent only for the same already-stopped epoch; it never
pauses an unrelated replacement. There is no new native resume or event-ack verb.

## Lifecycle validation

Save names identify a single native save, not a path: nonblank, at most 128 UTF-8
bytes, no NUL, directory separators, dot-only components or platform-invalid
filename characters. The owned profile resolves the path. Saves require explicit
current player direction, Manual, resolved owned drafts, verified pause and exact
expected tick. `SaveCompleted` requires a nonempty complete native file and the
same observed identity/direction/tick afterward. It does not publish a paired
controller checkpoint; the controller verifies and durably publishes its own
snapshot. A potentially started failed save is `uncertain`, not pre-admission
failure, and needs file/context inspection before retry.

Load timeout is 1000–120000 ms. Map readiness and optional visual readiness are
distinct; completion carries the newly observed identity. A timeout after dispatch
is `pending`, with unavailable readiness facts absent. Do not retry loading from
an ambiguous acknowledgement. Load starts Manual and invalidates authority,
input/UI captures, owned drafts, pending actions and observation cursors. There
is no saved-lease restoration, old-format conversion or compatibility override.
The lifecycle facade uses the existing SDK implementation; its method descriptor
does not imply another in-game loader or native process server.

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
