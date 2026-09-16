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

See [save and resume](../../players/save-and-resume.md).

## Cleanup

Workers own their controller, private profile, database and GABS process. Cleanup
stops owned processes only. Windows workers share installed DLLs, so all games must
stop before replacing them. Docker workers stage private binary snapshots and
retain output after container removal.

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
