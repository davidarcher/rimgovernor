# Sessions and recovery

[Architecture](overview.md)

Colony identity and map scope durable intent; a load token identifies the loaded
instance. Reloading can preserve goals while invalidating old in-flight operations.
There is one author of orders, so no per-request direction counter or compare-and-swap
exists: authority is the load token (which world instance), the native tick (no rewind)
and the native order generation (no native order-history drift), plus a pause flag.
Control intents are `resume` and `pause` for an exact world; resume runs the bot under
that world's empty root plan (`root/<colony>/<load>/<map>`), and routine methods and
player submissions dispatch under it once authorized. "Manual" in older contract text
means the paused state.
Observation revisions and native context are separate from authority: background
refreshes cannot authorize new work. Recheck load token, tick and generation before
writes.

## Save, load and restart

Saving (`POST /api/lifecycle/save`) is a plain pause-gated native save: the
game writes a normal RimWorld save, and the controller records nothing beyond
the request's outcome. There is no paired database backup or manifest,
because the database is not needed to restore intent: routine goals are
re-derived from observation, and the durable state is the receipt journal
for uncertain writes, request-ID replay and the review cursor (see
[persistence contracts](../contracts/persistence-contracts.md)).

Loading (`POST /api/lifecycle/load`, or the player loading in-game) issues a
new load token. The next routine review sees the world change, invalidates
the previous bindings, cancels their non-uncertain pending work and starts
fresh under the new world's root plan; uncertain dispatched work keeps its
recovery requirement. A restarted controller does the same with an empty
database, and reclaims Auto authority a killed controller left in native
(one revoke at the observed generation, then a fresh grant) because the
profile lock makes it the only author. With `serve --resume` the bot runs for the observed world at
startup and again after every load without a dashboard click; otherwise it
waits for **Resume**. Attached sessions have a separate unchanged-game
reconnect contract.

The bridge client runs each GABS process as `gabs server http` on a loopback
port and exchanges every JSON-RPC message by one POST, because GABS's stdio
server answers one message at a time and a held `clock_read_events` long poll
would stall every planner read behind it (issue #115). Closing the client kills
its GABS, and on Windows a job object kills GABS however the controller ends;
the game GABS launched keeps running either way, as it does when GABS exits
on its own. The game side still answers companion-mod tools one at a time:
RimBridgeServer runs each on the GABP connection's reader thread, so the
service does not hold `clock_read_events` (`wait_ms` 0) and polls the journal
at its own cadence instead.

A GABS session lost while the service runs (the GABS process exiting, its
endpoint gone) is recovered in-process: the bridge client drops the session as
soon as the SDK observes the end of its transport, later native calls fail
fast as disconnected, and a supervisor reattaches with bounded backoff --
a fresh GABS process, then the same start/connect handshake a restarted
controller uses against the game that kept running. Nothing is retried across
the gap. Native meanwhile revokes authority as `DISCONNECT` once the typed
clock lease lapses; because that is not the player's Pause, `--resume`
re-acquires it (one bounded resume cycle per loss) while the journal's current
control intent is still a running Resume for that world. A player's Pause
record stands.

See [save and resume](../../players/save-and-resume.md).

## Cleanup

Workers own their controller, private profile, database and GABS process. Cleanup
stops owned processes only. Windows workers share installed DLLs, so all games must
stop before replacing them. Docker workers stage private binary snapshots and
keep their writable `/worker` tree (SQLite state, flight recorder, game log,
profile) on a private Docker volume so no synchronous write crosses a host bind
mount; the tree is exported to the host output directory only after the
container has stopped, every exported database must pass `integrity_check`, and
the volume is removed (and verified gone) only after a successful export --
otherwise it is retained as recovery evidence and named in the report
(`go/internal/nativeaccept/docker.Storage`). `-storage bind` keeps the
host-bind-mount comparison mode.

## Uncertain writes and read retries

Recovery operates on the existing action identity and requires fresh evidence.
A lost reply after dispatch may conceal an accepted order: the receipt is
retained as uncertain and the game is inspected before any retry; ambiguous
non-idempotent writes are never replayed automatically. A reload (new load
token) or a tick rewind invalidates an interrupted attempt; completed or
resumed work is observed without another order. Retired flags on plans and
goals bound the working set (see
[persistence contracts](../contracts/persistence-contracts.md)).

GABS launch-claim collisions and runtime-state publication faults permit a
bounded number of retries for reads and explicit previews only; mutations do
not retry. Requests within one GABS session are serialized, cancellation while
queued sends no request, and a lost mutation response still requires
observation before the plan moves on.
