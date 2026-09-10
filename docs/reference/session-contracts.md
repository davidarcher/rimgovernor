# Session, checkpoint and clock contracts

[Documentation](../README.md)

These are the identity, restart and execution-window boundaries. Operational commands
are in [save and resume a session](../how-to/save-and-resume.md).

Startup/reload enters Manual. Saved colony ID plus map scopes durable plans, projects,
direction, memory and trends; a per-load token invalidates in-flight work. Save after
identity attachment to retain identity across game restarts. Model changes do not erase
colony intent. SQLite defaults to `.rimbot/bridge.sqlite`; `RIMBOT_DATA` can isolate
controller state.

## Paired checkpoint publication

Owned DirectPath sessions support paired checkpoints through the local `POST
/api/session/checkpoint` endpoint and Autopilot's checkpoint control. Saving invalidates
pending direction, enters Manual, verifies pause and owned-draft cleanup, then records
an ordinary native save and a SQLite backup under the writer lock. Identity/tick
changes, partial saves and unresolved ownership prevent publication. The final manifest
records hashes for both artifacts. No native save content is edited. `python -m rimbot
--resume <checkpoint.json>` validates the pair, restores into a new database, enables
the private profile's native pause-on-load preference before launch, and checks
colony/map identity and tick before connecting. Native load may advance one tick; larger
advancement or rewind fails closed. The controller resumes in Manual with a new load
token; old draft ownership is not reclaimed.

## Owned and attached restart

`scripts/restart_session.ps1` requests a checkpoint before stopping a server. It checks
the source, process birth time and retained process handle, validates checkpoint hashes,
stops the owned game through GABS after rechecking player direction/tick, and verifies
the restored session. New chat is rejected while closing so the player can retain and
resend its draft. Older servers lacking checkpoint support are left running. Snapshots
remain reusable if startup fails; later database writes do not mutate them.

An attached controller can checkpoint a game whose private save profile is known
under its configured DirectPath root. Its manifest records `owned: false` and the
exact live load token. Stopping for restart detaches the controller without a game
stop. Resume attaches to that same live game and requires an unchanged paused tick,
colony, map and load. It never reloads the native save or pauses a changed game.
An attached checkpoint cannot launch a replacement for a missing external game.

Checkpoint pairs are retained until explicitly deleted. `GET /api/session/checkpoints`
lists valid and damaged pairs; `POST /api/session/checkpoints/delete` requires the
current session and exact manifest path. Deletion refuses the active resume pair,
closing sessions, changed hashes and unexpected files. It unpublishes the manifest
before removing its two artifacts, preserving native profile saves and other pairs.

## Legacy takeover

On Windows, `scripts/migrate_legacy_session.py` upgrades an owned legacy server without
the checkpoint endpoint. It verifies the checkout and retained process birth identity,
enters Manual, pauses the old writer with a bounded recovery watchdog, and backs up
SQLite without modifying the original. Pending chat, unreleased drafts or disagreement
with public goals/policies/history block migration. An explicit GABS ownership takeover
then verifies the same native load and tick before using the existing paired checkpoint
and restart path. The replacement must retain the shared plan, settings and conversation
in Manual. Ownership transfer is one-way: an interrupted handoff can leave the old
dashboard disconnected while the native game remains paused. Retained migration
artifacts support an explicit guarded retry or checkpoint resume; resuming the old
process alone does not restore its GABS connection. The legacy migration procedure
remains restricted to owned games; attached controllers use their checkpoint path.
Migration checks the persisted GABS DirectPath workload claim against the observed
PID and retains a Windows handle with the claim's raw FILETIME birth fingerprint.
Missing birth identity and executable-name cleanup fallbacks are refused.
The pinned GABS v1.1.1 source (`b5f441a04fa908852fdece18a08a8876ac970a4d`)
owns the process claims, birth checks, lease recovery and shutdown join. RimBot
uses those existing operations rather than introducing a second game-process manager.
Durable phase records bracket suspension, backup, takeover, native save, game stop,
controller stop and replacement startup. A pending stop requires inspection before
recovery, because a missing receipt cannot establish whether the game exited.

## Clock leases and interruption

`clock_control.py` and native supervised play enforce a lease independently of model
inference (15-second production lease, renewed every 3 seconds). Danger, injury, lease
expiry and external pause/speed changes interrupt work. External holds require explicit
player release. Opening an AI-owned letter pauses its lease before reading the actual
UI; closing a window does not automatically resume time. `notifications.py` and
`dialog_control.py` retain exact native targets.

Controller reviews pause before observation and optional player inference. Hands runs
between reviews; automatic execution requires confirmed native work or a deterministic
goal waiting for simulation. Production defaults to Normal speed. An automatic window
targets 600 game ticks, ending sooner when work finishes. Existing explicit clock steps
also get a bounded window; direct player clock commands retain player control. The
native supervisor pauses at the tick boundary through the game's single-tick callback;
controller polling does not extend the window. Heartbeats renew only the wall-clock
lease. Native danger and lease stops remain independent. Automatic execution refuses
companion versions without native tick-boundary support. Native letter-triggered pauses
are attributed at the actual clock transition and can trigger a deterministic review;
unrelated player pauses retain their hold. A pre-dispatch native autosave refusal causes
a bounded re-observation, never a blind replay of an uncertain write. Letter opening
requires a fresh empty window list beforehand and identified windows afterward; existing
or unavailable windows require inspection and resolution.

Draft ownership is written before orders and scoped to the load. Manual, review failure
and shutdown attempt pause and verified cleanup; unresolved cleanup remains durable.
Pre-existing player drafts are not claimed. Native draft claims are acquired only
on an actual controller draft transition. Every later draft setter clears the claim,
including external undraft/redraft between observations. Cleanup rechecks ownership
atomically at the native write. Missing ownership metadata retains an inspection
hold; a confirmed different claim retires the obligation without undrafting.

## Gameplay and network boundary

Disposable acceptance can construct `PlayClock(test_acceleration=True)`. It
requires native acceleration capability and a positive tick budget, mapping the
execution window to native Ultrafast with boost. The epoch restores the previous
boost on stop, including journal failure and load change. Accelerated hazard
probes are at most 30 game ticks apart; external clock and lease checks run each
native tick and frame. Native forced slowdown remains enabled. Neither persisted
player policy nor model tools opt into this test mode.

The gameplay gateway disallows cheat placement, instant gear dropping, boosted
simulation; map trade requires adjacency. Native watch presentation is off unless the
player enables action follow for the current load. Native game eligibility remains
authoritative. The server binds to loopback and guards dashboard mutations with
same-origin/header checks. Model configuration accepts HTTP loopback endpoints by
default. `RIMBOT_ALLOW_DOCKER_HOST_MODEL=1` also permits `host.docker.internal` for the
explicitly configured host LM Studio URL, with no paid-provider fallback. Container
servers bind inside their network namespace; Compose publishes only a host loopback
port.
