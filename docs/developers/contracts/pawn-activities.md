# Planner-owned pawn activities

`PawnActivities` is a Hands intent for installing/revising/releasing an atomic set
of exact per-pawn assignments. It is an execution capability; no production
selector automatically takes over colonists yet. #3127/#3128/#3132/#3135 provide
selection, needs forecasting and the normal-operation cutover.

The existing executor journals the domain action and dispatches its typed intent
through Actions/Apply. Installation acknowledges authorization, not started or
completed work. Each order carries assignment/Method identity, an expected and
new revision, exact pawn, and a bounded list of authorized steps. The surrounding
Apply identity and existing authority generation scope the batch. Native validates
all orders before replacing any entry. A repeated identical installed revision
is a no-op; a conflicting/stale revision is refused. Release uses the same path.

The initial step families are exact thing-target work (Clean, Repair, Harvest and BeatFire through a named native WorkGiver), selected food/count,
selected bed/rest, selected horseshoes recreation, exact delivery, exact cooking, and Hold. Unsupported families are refused, not sent to a
native economic selector. Other recreation and construction adapters remain outside this
initial execution slice. Firefighting is an explicit policy-selected emergency revision, not an automatic native fallback. Work givers use forced:false, required capacities,
capabilities, native target checks and native reservations. They may still perform
the normal internal mechanics of their chosen job; this does not model every toil
as a separate economic decision.

`Deliver` names a currently spawned item, a positive count and an exact destination
cell. The custom delivery driver uses native pathing, reservations, stack splitting
and carrying, but does not ask the hauling WorkGiver to select storage. It never
collects nearby duplicates or substitutes a nearby drop location. Admission and
execution require a capable hauler, sufficient source quantity and carrying capacity,
an empty carry tracker, and a reachable, unreserved destination without loose items.
This initial adapter deliberately refuses stack merging and multi-trip deliveries.
The planner must split larger transfers into bounded attempts and observe each result.

Destination availability is rechecked during execution. A changed destination,
lost source, capacity shortfall or deadline fails the assignment visibly. Completion
requires the selected count dropped directly at the selected cell. An interruption
after pickup may leave food carried by the pawn: failure is not evidence that it is
still at the source, and a new planner decision must inspect that state. Resuming a
carried stack is not yet supported by this adapter. Newly harvested/cooked items
require fresh observed identities and a revised assignment; future output IDs are
not guessed.

`Cook` names the exact bench, existing bill, recipe, cooking WorkGiver and up to
16 distinct ingredient stacks with positive counts. It authorizes one recipe
iteration, regardless of the bill's remaining repeat count. Bill creation and
configuration remain separate Hands actions; cooking neither creates nor edits a
bill. The selected bill must explicitly use DropOnFloor. Native production places
the output near the worker; observing its actual identity/location and assigning
delivery is a separate planner decision, not native storage selection.

The initial cooking adapter supports ordinary single-product nutrition recipes
with the base RecipeWorker, no unfinished item or special products, and one
ingredient definition across all selected stacks. Every recipe slot must accept
that definition; selected quantities must exactly equal the sum of native slot
requirements, including nutrition-based requirements. Mixed ingredients are
refused rather than resolved locally. Eligibility includes the selected bill's
filters, worker restrictions, native skills/capacities, bench recipe availability,
fuel/power usability, ingredient radius, reachability and reservations.

The custom cooking driver uses native ingredient collection, recipe work,
consumption and product generation with the already-selected ingredient queue.
It does not call the WorkGiver's bill/ingredient selector. All selected inputs and
the bench must reserve successfully; loss or shortage of placed ingredients,
an unusable/forbidden bench, bill deletion/suspension, or a changed recipe/store
mode fails visibly. It verifies complete input quantities before work and again
before consumption. Output quantity, poisoning and other native effects remain
RimWorld-owned. A successful job still requires physical output observation for
forecast credit. Interrupted collection can leave carried or placed ingredients;
there is no automatic retry, refund or assertion that those inputs remain at
their original locations.

For owned pawns the normal and constant think-tree selection paths are intercepted.
Override probes cannot consume/restart assignments. Opportunistic prefixes and
optional finalizer jobs are suppressed; the underlying driver's cleanup and
reservation handling still run. Existing queued/player-forced work refuses initial
acquisition instead of being erased. Native work priorities and timetables do not
select activities inside this boundary. Uncontrolled pawns keep native behavior.

A selected native Job is identified by both reference and load ID, so pooling
cannot transfer ownership. Actual StartJob and EndCurrentJob hooks distinguish an
offer from execution. Native reservation failures terminate the attempt visibly.
Successful work advances only to an explicitly authorized next step; failed or
interrupted work never silently restarts. A new revision is required to retry.
The longer-lived Method can survive interruption and submit a new attempt.

GovernorWait has no opportunistic prefix or player-forced flag. Absolute game-tick
deadlines never reset when revisited. The first dependency adapter holds on an
exact known filth reference: its destruction makes the authorized continuation
eligible; a missing/unspawned dependency is not guessed complete. More complex
dependencies require policy revisions. A deadline without release fails visibly.
Sleep and recreation are bounded by their deadlines and may finish earlier through native recovery.
Work deadline exhaustion is a failure. Native food/rest levels and physiological
consequences are not modified by execution.

Completion/failure stops the current clock window through the existing requested
pause journal path, exposing typed readback for replanning. Without another
assignment, an owned pawn waits and the game pauses rather than reverting to
vanilla choices. Downing, mental state or drafting encountered at selection/start
pauses and releases this voluntary-work owner; native involuntary behavior and
the existing combat owner are not replaced. This is not a complete autonomous
emergency-response policy; full-control production cutover requires that coverage.

Authority generation changes, game changes and despawn invalidate the transient
table and end matching owned jobs without clearing unrelated jobs. Assignments
are not saved. A restored native job has no restored planner authorization;
normal authority and current facts must authorize new control after load.

## Observation and tests

Pawn observations expose `ActivityExecution` (assignment, Method, revision, step,
state, reason, current native job ID and tick). `pawn_activity_execution` records
execution changes in the existing OTLP ring. `pawn_activity_interval` includes
known assignment/Method attribution; neither job success nor installation is
physical output evidence. No additional durable history is created.

`control/pawn-activities` uses a prepared hungry/tired producer and helper to
verify cleaning dependency, harvest, selected meal and selected bed, probe
idempotency, stale revisions and real save/load invalidation. `control/pawn-activity-lifecycle` covers atomic refusal, deadline expiry, native work refusal, selected recreation, explicit firefighting preemption and player takeover. Pure tests round-trip the
Hands action through the session codec and intent builder and reject malformed
batches. Existing `clean/bench` and `clean/care` cases remain: their helpers are
still used outside explicit ownership. #3128 deletes/replaces those policies and
cases with their economic replacements. The activity observation case remains
independent native lifecycle/output coverage.

`control/pawn-activity-delivery` starts from a prepared ripe crop and checks edible
harvest output, refusal when the selected delivery cell becomes occupied after
installation, and an explicit new revision delivering one unit to another exact
cell while conserving the source quantity. The fixture drives those revisions
through native activity installation; it does not prove forecast selection, normal
Rounds/Hands dispatch or autonomous recovery. Domain tests separately round-trip
the exact delivery payload through the journal codec and Hands intent builder.

`control/pawn-activity-cooking` prepares a fueled stove with a competing bill and
unselected food. It checks wrong-quantity refusal without changing ownership,
idempotent installation, failure after losing a selected stack or blocking the
bench, then one explicit revision's exact ingredient consumption, edible output
and selected-bill decrement. The other bill and unselected stack must remain
unchanged. This fixture-driven execution case does not establish live forecast
selection or autonomous recovery. Journal/Hands intent tests preserve the cooking
recipe, bill and ingredient identities/counts and reject ambiguous payloads.
