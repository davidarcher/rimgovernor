# Sessions and recovery

[Architecture](overview.md)

Colony identity and map scope durable intent; a load token identifies the loaded
instance. Reloading can preserve concerns while invalidating old in-flight operations.
There is one author of orders, so no per-request direction counter or compare-and-swap
exists: authority is the load token (which world instance), the native tick (no rewind)
and the native order generation (no native order-history drift), plus a pause flag.
Control intents are `resume` and `pause` for an exact world; resume runs the bot under
that world's empty root plan (`root/<colony>/<load>/<map>`), and routine methods and
player submissions dispatch under it once authorized. "Manual" in contract text
means the paused state.
Observation revisions and native context are separate from authority: background
refreshes cannot authorize new work. Recheck load token, tick and generation before
writes.

## Save, load and restart

Saving (`POST /api/lifecycle/save`) is a plain pause-gated native save. The
save carries concerns and family plans in its `GovernorState` blobs, so loading
it restores that timeline's intent; the SQLite session journal is not paired
with it. Where each fact lives is in
[persistence contracts](../contracts/persistence-contracts.md).

Loading (`POST /api/lifecycle/load`, or the player loading in-game) issues a
new load token. The next rounds sees the world change, invalidates
the previous bindings, cancels their non-uncertain pending work and starts
fresh under the new world's root plan; uncertain dispatched work keeps its
recovery requirement. A restarted controller does the same with an empty
database, and reclaims Auto authority a killed controller left in native
(one revoke at the observed generation, then a fresh grant) because the
profile lock makes it the only author. With `serve --resume` the bot runs for the observed world at
startup and again after every load without a launcher click; otherwise it
waits for **Resume**. Attached sessions have a separate unchanged-game
reconnect contract.

The controller launches RimWorld itself (`go/internal/gamehost`, from
`games.<id>` in `<config>/config.json`) with `GABP_SERVER_PORT`, `GABP_TOKEN`
and `GABS_GAME_ID` in its environment, records the endpoint in
`<config>/<id>/endpoint.json` (pid plus start time, so a reused pid is never
mistaken for the game) and speaks GABP to the GABP host directly
(`go/internal/gabp`). The GABP connection correlates concurrent requests by
id, so a held `clock_read_events` long poll does not stall planner reads
behind it. The game is spawned detached and keeps running when
the controller ends; a restarted controller reattaches through the endpoint
record. The vendored GABP server runs every tools/call off its reader thread
(`GabpServer.HandleToolsCallAsync`), so a held journal read never delays the routine
worker's dispatch or the lease renew. The service holds its journal read
(`wait_ms`, 4 s) while a colony window it admitted is running, and keeps an
unheld 1 s cadence between windows while the planners read.

A game connection lost while the service runs (the GABP connection
dropping) is recovered in-process: the bridge client drops the session as
soon as it observes the end of its transport, later native calls fail
fast as disconnected, and a supervisor reattaches with bounded backoff --
the same start/connect handshake a restarted
controller uses against the game that kept running. Nothing is retried across
the gap. Native meanwhile revokes authority as `DISCONNECT` once the typed
clock lease lapses; because that is not the player's Pause, `--resume`
re-acquires it (one bounded resume cycle per loss) while the journal's current
control intent is still a running Resume for that world. A player's Pause
record stands.

See [save and resume](../../players/save-and-resume.md).

## Cleanup

Workers own their controller, private profile, database and game process. Cleanup
stops owned processes only. Windows workers share installed DLLs, so all games must
stop before replacing them.

## Uncertain writes and read retries

Recovery operates on the existing action identity and requires fresh evidence.
A lost reply after dispatch may conceal an accepted order: the receipt is
retained as uncertain and the game is inspected before any retry; ambiguous
non-idempotent writes are never replayed automatically. A reload (new load
token) or a tick rewind invalidates an interrupted attempt; completed or
resumed work is observed without another order. Retired flags on plans and
concerns bound the working set (see
[persistence contracts](../contracts/persistence-contracts.md)).

Launch collisions (a live endpoint record) and endpoint-record faults permit a
bounded number of retries for reads and explicit previews only; mutations do
not retry. Requests within one bridge session are serialized, cancellation while
queued sends no request, and a lost mutation response still requires
observation before the plan moves on.
