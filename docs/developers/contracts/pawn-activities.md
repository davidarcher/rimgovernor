# Planner-owned pawn activities

`PawnActivities` is a Hands intent for installing/revising/releasing an atomic set
of exact per-pawn assignments. EnsureFoodSupply now admits a bounded, same-pawn
harvest-to-delivery attempt, with optional one-iteration cooking, for selected plant acquisition demand. This is the
first production caller, not the full #3127/#3128/#3132/#3135 cutover.

## Live harvest and delivery

The normal acquisition planner keeps the shared FoodPlan's selected plant demand.
For those sources, the typed `food_activity` read enumerates idle, capable pawns,
native routes and empty allowed storage cells. ColonyForecast's shared outcome
comparator evaluates these mutually exclusive Method alternatives against the
existing acquisition horizon, using its explicit labor-per-unit objective with
nominal nutrition as the comparable unit.
It assumes equal unit labor rates and zero injury
recovery on Danger.None routes, not calibrated opportunity costs or proof of safety.
Non-idle jobs are protected, including a fresh check at native admission.

The admitted Method journals one exact harvest through Hands. Its OutputDelivery
payload records only resource, source area, destination, quantity cap, original
deadline and remaining attempts. It is an obligation, not predicted inventory.
On completed harvest, the worker reads native ownership and fresh loose food
within two cells of the original source, then journals an exact delivery revision.
Equivalent food is fungible: the read does not claim individual-stack provenance
or credit the harvest's predicted yield. Split/merged/replaced stacks are rebound
by current identity and quantity; empty storage destinations are reconsidered.

Running work is retained. Missing observations or unknown demand cannot authorize
replacement. Known recovery releases; a completed delivery ends this one trip.
A failed delivery with an empty carry tracker can use fresh source/destination
observations, at most three delivery attempts under the original deadline. A
failed harvest, no feasible food, exhausted attempts, expiry or carried food
releases instead. Carried-food resumption remains unsupported. A refused Apply
retains the previous installed assignment through journal predecessor history
and permits cleanup only; an uncertain receipt never authorizes replacement.

Reads bound plants/pawns/stacks to 16, destinations per source to eight and route
probes to 128. Policy generates at most 16 eligible alternatives for the shared
comparator, which uses one 512-expansion budget. Native and policy generation
truncation are exposed in the returned comparison findings and initial Method
reason; an omitted alternative is not proven worse. There is no joint helper assignment, needs forecast, mixed production
portfolio selection or cross-owner material reservation in this slice. Other acquisition
families and existing hauling/cleaning/eating helpers remain.

Pure tests cover changed stacks, shrinking counts, interrupted delivery, carried
food, lost ownership, unknowns and deadlines. Real-journal tests retain the
obligation and exact targets through Hands intent construction, including cleanup
after native refusal. The prepared delivery case checks native candidate reads,
replacement-stack binding and physical delivery; its explicit fixture revisions
do not establish autonomous end-to-end colony behavior.

## Execution contract

### Live one-iteration cooking

After harvest produces observed loose food, policy proposes raw delivery and
eligible one-iteration cooking alternatives before choosing a recipe or stack.
Native enumerates eligible existing bills, exact single-resource slot counts,
worker-adjusted recipe work, nominal nutrition, bench supply cells and routes.
It neither creates bills nor selects the winning recipe. Shared ColonyForecast
comparison evaluates supply, cooking and output-delivery dependencies against the
same deadline and labor ceiling. Recipe material conversion uses the same graph
constructor as the broader food candidate generator.

The selected Method first delivers the exact ingredient count from one observed
stack to a selected cell near the bench. On completion it reobserves local stock
and rebinds the same bench/bill/recipe to a current stack with enough ingredients.
Changed identities can therefore recover, but combining several undersized stacks
is not implemented in this live caller. Missing/blocked benches, insufficient
ingredients or failed supply release; they do not silently change recipes.

Cooking authorization replaces the supply obligation with an output-delivery
obligation. Completed Cook permits delivery only of currently observed matching
food near the bench. Failed Cook always releases, even if meals or ingredients
are nearby: no replay, refund or assumption of atomic consumption. Unknown or
running execution holds; Concern recovery releases. All stages retain the original
Method identity and deadline and use normal journal/Hands transitions.

Bounds are four benches, eight bills per bench, two supply cells and four product
destinations within the shared 128-route-probe read budget. Policy retains its
16-outcome, 32-task and 512-expansion limits; cooking alternatives may reach the
task limit first. Omission is explicit. Nominal nutrition is not eater-specific
eligibility or a coupled survival forecast. Work estimates use recipe work and
the pawn's work-speed stat, not calibrated end-to-end toil timing. Product identity
is fungible local stock, not proof of individual batch provenance. The broad
multi-batch survival portfolio, helper delegation and carried-item recovery remain
unintegrated; existing hauling/cleaning/eating helpers stay.

Pure tests cover distance/work/ingredient-driven reversals, stale recipe refusal,
changed ingredient identities, observed meal delivery and interrupted cooking.
Codec/Hands tests preserve obligations and exact ingredients. The prepared native
cooking case also probes eligible recipe observations; its explicit revisions
prove native conversion, not autonomous full-chain planning.

## Exact activity execution

The existing executor journals the domain action and dispatches its typed intent
through Actions/Apply. Installation acknowledges authorization, not started or
completed work. Each order carries assignment/Method identity, an expected and
new revision, exact pawn, and a bounded list of authorized steps. The surrounding
Apply identity and existing authority generation scope the batch. Native validates
all orders before replacing any entry. A repeated identical installed revision
is a no-op; a conflicting/stale revision is refused. Release uses the same path.

Normal Method admission checks that every activity order, including release,
names the Method being committed. Across that Method's plan, a pawn may appear
in only one activity action: a sequence of revisions requires observation and a
later decision, not separate competing actions in the same plan. Multiple pawns
may share one atomic batch. These checks run in the existing owner transaction,
before plan creation or Method binding; refusal leaves both unchanged. They do
not reserve pawns across different owners or prove current native availability.

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
The longer-lived Method can survive interruption and submit a new attempt through
the [journal continuation path](method-continuation.md#pawn-activity-methods).
Worker cleanup remains active after Concern recovery and releases failed or
expired installed assignments. Productive continuation requires terminal native
readback and current exact input binding by its owning planner.

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

Live food policy separates candidate generation, generic comparison and exact
binding under the [enforced stage contract](../architecture/colony-forecast.md#live-activity-policy-stages).
The binder consumes the shared forecast's selected outcome and concrete binding map. Lifecycle ownership checks,
the Method journal and Hands retain their existing responsibilities.

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

`PawnActivityMethodTests` commits coordinated assignments through a real SQLite
Method journal and builds the Hands intent from the reloaded action. It checks
pending progress, the existing open-Method guard, wrong-Method refusal (including
release), repeated-pawn refusal across actions and transaction rollback. This is
admission/payload evidence, not a live forecast caller or native dispatch test.

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

## Needs forecast support matrix

The [needs forecast](../architecture/colony-forecast.md#needs-alternatives-and-future-capacity)
is generated by ordinary review. Selection/cutover remain #3134/#3135. The existing
Method journal owns admitted work and Hands writes exact assignments; a candidate
does not reserve a pawn or authorize native fallback.

| State or choice | Forecast and ownership behavior |
| --- | --- |
| Ordinary loose permitted food | Enumerate observed edible stack/count alternatives; consumption only after completed ingestion; nutrition waste explicit. Native mechanics choose the chewing position, whose extra travel/mood is unassessed. |
| Eligible existing bed | Enumerate timing and partial/full recovery; travel occupies the same pawn. No ground-sleep adapter is inferred. |
| Horseshoes | Enumerate exact observed pin with current tolerance-adjusted gain. Future tolerance and playing-position travel remain unassessed. |
| No eligible target / exhausted bounds | No feasible candidate or explicit truncation, never an instant need or permission for native AI to choose. |
| Interrupted needs | Partial rest/recreation service only; unfinished eating contributes no forecast food. Reobserve before journal continuation/release; no replay credit. |
| Drafted / player-controlled / queued or player-forced work | No voluntary candidate. Existing player/combat authority owns the pawn; a timetable bypass cannot override it. |
| Downed / dead / mental state | No voluntary candidate or supported productive capacity. Native involuntary behavior and existing protection remain authoritative. |
| Unknown mechanics / occupied job duration | Unknown capacity, never free time. |
| Other recreation, DLC/mod needs, babies or special ingestion | Explicit unsupported effects/targets; no inferred adapter or hidden fallback. Full-control enabling must handle the visible unsupported state under #3135. |

The exact action execution and ownership contract above remains unchanged. Needs
candidate generation does not install a second controller or remove eat-trigger,
cleaning, hauling or scheduling helpers prematurely.
