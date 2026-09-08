# Native control checkpoint

Implemented: durable write-ahead draft ownership scoped to the current native load token. Draft/goto/attack/tend resolve the pawn before issuing the action; already drafted player pawns are not claimed. Confirmed undrafted receipts clear ownership. Manual mode, review failure, and orderly shutdown attempt pause and release tracked drafts, verifying the final draft state. Uncertain cleanup retains the obligation and emits a blocker.

Validation: 26 controller tests pass, including lost write receipts, partial cleanup failure, and pause failure. No new live gameplay validation or deployment in this checkpoint.

Remaining: native event-aware clock lease and interrupt handling; human pause/speed override detection; combat completion cleanup while Automate remains on; observed movement/combat live test; live trading. Process-crash pausing needs the native lease, not this Python shutdown path. Ownership cannot distinguish a human undraft/redraft between observations.

Then: visual second opinion and bounded read-only data scouts as described in the instruments audit.


## Event-aware clock slice

Implemented the in-game supervisor and independent controller heartbeat. The
planner uses `control_clock` to start normal game time with native danger monitoring,
not a wall-time budget for model turns. External pause/speed override and expired
lease stop automatic resumption. Clock events enter the existing dashboard feed
as Game observations and invalidate pending planner actions. Explicit player
Automate clears the hold. Danger alone leaves the planner able to inspect and
respond, without the old global-hostiles construction block.

Live checks passed: 1-second test lease expires and verifies pause without a
heartbeat; an external pause prevents restart until explicit release; an actual
pawn reaches a nearby destination; the runtime ledger undrafts that pawn and
pauses on halt. Production lease is 15 seconds, renewed every 3 seconds.

Still remaining: combat completion cleanup while Automate stays on (the native
hostiles-cleared event is advisory); real combat and live trading validation;
visual second opinion and bounded read-only data scouts. No claim of winning a
fight is made by the movement test. Cleanup ownership still cannot distinguish a
human undraft/redraft between observations.

## Selected stand-down during Automate

The strategist can now commit `stand_down` with exact pawn IDs. Its context lists
current-load AI-owned drafts. Deterministic Hands releases only the selected owned
drafts, confirms pawn identity and final draft state through native reads, and
keeps Automate enabled. Player-owned and other-load drafts are untouched. This is
an explicit strategic decision; a hostiles-cleared event does not automatically
cancel every drafted job, and distant cave insects do not gate cleanup.

Partial failures remain durable cleanup obligations and block the step with native
failure details. Retrying first reads current state: a lost successful undraft
receipt does not require another undraft. New direction/load/plan revisions stop
remaining writes. Cancelling a plan still does not silently cancel game orders.

Validation: 52 controller tests; `scripts/native_stand_down_smoke.py` passed on the
headless eight-tribal fixture. It drafted two pawns through separate controller and
test-player paths, executed the committed cleanup step, verified only the owned
pawn was undrafted, and verified Automate remained on. Fixture drafts were cleaned
up afterward. No model call or actual fight was part of this check.

Next combat acceptance: a controlled threat scenario with observed attack outcome,
injury interruption and strategy-selected stand-down. Victory detection, rescue
completion and human undraft/redraft ownership ambiguity remain open.

## Real melee and injury interruption

`scripts/native_combat_smoke.py` now reloads the isolated headless baseline,
selects a reachable existing wild animal through native query/preview, and issues
a normal melee attack. It uses supervised Superfast with colony injury monitoring
and acknowledges only its selected target for hostility checks. It does not spawn
or weaken anything, inject wounds, heal pawns, or enable boosted time.

Two live runs observed a tribal hit a red fox, receive a wound, and pause through
the native `colonist_injury` event. The target remained alive and active. The test
verifies actual target health change or an observed incapacity/injury stop; a
missing target is explicitly unobserved and does not count as victory. It also
checks that the event wakes the strategist and changes its revision, then releases
the selected owned draft without disabling Automate. The game is paused and the
fixture is discarded at the end. Evidence is `.rimbot/bridge/combat-smoke.json`.

The run exposed imported event instructions for an unsupported `--allow-injured`
CLI and automatic post-combat handling. Native event text now reports observations
only; structured wound and hostility evidence remains unchanged. The strategist
must decide how to respond rather than treating event prose as orders.

Validation: native build/install, two real headless melee runs, and 53 controller
tests including stale-order rejection after injury delivery. No local-model combat
decision was tested. Ranged equipment/fire, rescue completion, actual hostile
encounters and strategy-selected victory/stand-down still need acceptance tests.

## Ranged equipment and observed hit

Native item discovery now supports `category=weapons` and reports the game's
ranged/melee flags, without a list of weapon def names. Invalid categories fail
explicitly rather than silently becoming a broad haulable query.

`scripts/native_ranged_smoke.py` is a repeatable headless acceptance test. It
selects a nearby native ranged weapon and eligible pawn through query/preview,
issues equip, then checks the exact weapon ID in the pawn's actual equipment.
It moves through the normal drafted order, verifies the game's snapped destination
rather than the requested cell, validates a ranged target, and requires an observed
new injury. An accepted attack receipt or a disappeared target does not pass.

Live run: Marulo equipped a short bow, reached the native movement destination,
and inflicted an observed `Cut (short bow)` on an existing red fox. Selected owned
draft cleanup succeeded. The test explicitly acknowledges this fixture's sealed
ancient-danger proximity warning once; production pause handling is unchanged.
There is no spawning, injected damage, boosted time or model call. Earlier attempts
correctly refused out-of-range shots and exposed test assumptions about movement
snapping and vanilla warning pauses. A farther firing position yielded no observed
hit and was not counted as a pass.

Validation: native compilation/install, 53 controller tests, and the live ranged
acceptance above. Evidence is `.rimbot/bridge/ranged-smoke.json`. Actual raids,
autonomous model tactics, rescue completion and victory handling remain unproven.

## Native treatment after combat

The controller supplies the native `allowPersistentDraft` handshake for a tend
order only when it has durably recorded that doctor's current-load draft cleanup
obligation. This saves the model from supplying lifecycle plumbing. An explicit
false remains false, pre-existing player drafts are not claimed, and a lost write
receipt retains the cleanup obligation. The input argument object is not mutated.

Run `scripts/native_combat_smoke.py --tend` for the disposable medical acceptance
case. It obtains actual wounds from the existing melee fixture, retreats the
patient to the starting group through a native movement order, holds them still,
and selects an eligible doctor via native tending preview. It requires a new
observed tended wound AND no remaining native tending need, not an accepted job
receipt. A missing/dead patient never passes. Both owned medical drafts are
released afterward; Automate remains enabled and the game is paused on exit.

Live result: Marulo treated Sam's two fox scratches; both were marked tended,
bleeding was false, and needsTend was false. Both owned drafts were released.
The earlier attempt beside the fox stopped on additional combat damage instead
of claiming medical success. This fixture explicitly acknowledges its sealed
ancient-danger proximity warning once; no production auto-resume changed.

Validation: 56 controller tests, including three lifecycle-handshake cases, and
actual headless wound/treatment/readback/cleanup. No DLL changed in this slice.
Evidence: `.rimbot/bridge/medical-smoke.json`. No wound injection, healing cheat,
boosted time or model call. This verifies treatment, not full healing or rescue.
Native-operation plan completion still means receipt acceptance; durable medical
outcome tracking and model-selected triage remain future work.
