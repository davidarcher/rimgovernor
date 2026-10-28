# Evaluated support work

[Documentation](../../README.md) · [Portfolio selection](../architecture/portfolio-selection.md) · [Pawn activities](pawn-activities.md)

[Food evidence](../../requirements/food-security.md) records the separate executed
native bottle-feeding and nursing claims and their limits.

## Baby feeding

Food derives each baby's hunger from the canonical needs read, including babies in
a carry tracker. `BabyCareFoodRead.WantsFeeding` reads native `WantsSuckle`;
current food level is observed separately from opportunities and active jobs.
Possible nursing, food in storage and job start do not satisfy hunger. Missing food
observations retain an unmet need with `baby_food_unobserved`.

`BabyCareRules` assesses finite current provision, prioritizing lower observed
food levels. This assessment neither chooses native caregivers nor reserves them.
Depleted milk with future refill is not
usable milk now. `ReadyToFeed` reads the native lactation activation predicate
separately from charge/refill: positive charge below activation is still unready.
Future opportunity bounds retain that refill observation. One caregiver cannot
cover two simultaneous demands. Active native care retains its caregiver,
including carrying and safe return. This is
recomputed current capacity, not a milk ledger or a feeding-duration forecast.

Shared admission withholds only observed active care capacity from fresh work.
Vanilla selects its legal feeding jobs, caregivers, food, seating and safe return.
There is no advance caregiver release. An actual native takeover invalidates the
old job handle and publishes a pawn-fact invalidation; the worker re-reads ownership
and reconciles the installed Method through the journal and Hands. It releases
only exact remaining participants and never ends the replacement care job.
The normal 600-tick review cadence reassesses hunger, interruption, changed
supply and eligible alternatives. Active bottle-feeding may accumulate nutrition
before ingestion; unchanged food on one sample does not cancel care. Actual
partial intake stays in the next observation; the response never refunds or
replays consumption. No usable alternative leaves an explicit
`no_usable_feeding_opportunity` blocker.

A selected shared graph that binds no native order reports the existing
`coupled_selection` decision with target `no_dispatchable_activity`; its selected
tasks and consequence findings remain inspectable rather than disappearing at binding.

Provision is separate: the existing food owner bills suitable stock when current
usable nursing does not cover the baby-food floor. Capability counts no longer
exempt the colony from provision, and stock alone never certifies feeding.

The colony model keeps babies outside independent labor and meal-service cycles.
Their food demand remains in the existing other-consumer nutrition accounting;
the presence of a baby must not fault the review before childcare can respond.

## Shared support methods

Support work serves observed production, care or material demand. Family providers
offer care, surgery and conditional delivery operations to the existing shared
composition and ColonyForecast comparison. Delivery participants, quantities and
timing are what-if evidence; ordinary ingredient gathering and hauling remain
native work. Ordinary tending and already-indicated surgery also remain native
work under Doctor priorities and existing care policies/bills. Their detailed
worker/medicine scenarios do not authorize exact care orders. Existing in-flight
work can finish; at a task boundary an old support Method releases its remaining
participant rather than installing another routine service. Unfinished cross-pawn
dependencies end the coordinated attempt, without crediting their completion or
cancelling unrelated work. Critical medical interventions retain their existing
medical Need, journal, safeguards and Hands path; the general intervention policy
remains under migration.
There is no separate helper scheduler, demand ledger or progress store.

Cleaning is not coupled support work (#3662): vanilla cleans the home area under
each colonist's Cleaning priority while no coupled order holds it. The forecast
reads each room as it stands; it does not propose cleaning or credit a cleaner room.

## Room consequences

Routine food harvest, ingredient gathering, recipe work and output handling belong
to native growing zones/designations, bills, storage and numbered work priorities.
Coupled harvest, cooking and delivery scenarios
remain conditional planning evidence. A missing modeled recipe or delivery is not
by itself a native production failure, and a forecast product is not actual stock.
The food plan and work ledger retain bill ownership; no support-work scheduler
assigns the routine cook.

Native room mechanics bound the supported consequences, read at the room's current
cleanliness. Food production samples
the room's food-poison chance when the recipe produces its output. Tending can
sample the room's infection factor into the wound's later infection calculation;
that factor is conditional on other native state. Each worker/medicine alternative
observes the native single-treatment batch, whether its infection component still
has an applicable pending check, and the earliest check window. A bruise or an
already-resolved check carries no room factor. Unsupported component state
and treatment finishing outside the observed window remain unknown. Comparison
uses the room factor only for applicable treatment completed before that window.
It is not an absolute infection probability, prognosis or guaranteed health improvement.
Surgery uses the native room cleanliness success factor for the same supported
operation. That factor is distinct from an overall surgical success probability;
other native success conditions still apply.

External doctors require the humanlike patient to be in bed under native rules.
The current graph does not own a joint patient-bed retention obligation. When the
patient is standing, the census reports
`care_patient_not_in_bed_external_doctor_unavailable`; it does not invent an
external-doctor candidate. Legal self-tending remains an alternative.
Tend eligibility checks every installed native tending subclass for each observed
worker/patient pair; an urgent-only giver cannot stand in for routine or self care.
Equivalent legal givers are deduplicated by stable definition name, and the chosen
legal giver identity travels with the exact treatment operation.

Missing room effects, routes, eligible workers or service timing remain explicit
unknowns; omitted routes are reported rather than treated as zero travel.
Surgery choice and medical demand retain their existing [care owner](medical-care.md).
Exact tending performs one native treatment batch with the selected medicine or
an explicitly permitted no-medicine choice. Its duration is a first-batch estimate,
not proof that every wound is treated or bleeding stops. Remaining wounds require
fresh observation and ordinary reconsideration. Native surgery execution binds an
existing medical bill; it does not choose a new surgical indication.
The exact surgical binding supports whole single-definition ingredient choices.
Mixed ingredients and unique implant requirements remain explicitly unsupported
by this support adapter and retain the existing medical path.

## Delivery and chunks

Delivery serves an exact outstanding requirement. Available carrying capacity,
source quantity, destination access and shared claims constrain the proposed
quantity. There is no universal full-stack refill threshold: a small urgent
delivery and a larger routine batch are evaluated against their actual demand and
participant time.

The refill scenario values one ingredient definition at the observed bench
interaction cell against an outstanding bill requirement. It installs no exact
delivery or pawn claim, including after dependency composition. Native bill workers
gather their ingredients; native hauling follows storage and work settings.
Unsupported mixed substitutions remain unassessed.

A chunk is eligible because a supported material or recipe requirement needs it,
not merely because a stockpile accepts chunks. Unrelated chunks remain outside
that demand. Native storage filters, reservations and work legality still apply;
global item definitions are not changed to make all chunks haulable.

Carrying a loose stack to storage with no outstanding requirement is vanilla's:
a colonist with no committed order hauls it under its Hauling work priority (#3662, #3669). The brain
has no haul path of its own for it.

## Execution and evidence

The existing `coupled_selection` decision labels support as `conditional_scenario`
or `native_unprojected`. Conditional vectors include first-care latency, pending
care batches, unsupplied demand, food-production identity and room effect,
served-care identity, tend quality and surgical room factor. Native-operation
vectors retain current observed care/bench demands and delivery requirements;
future support values are null with `native_support_response_unprojected`.
Omitting a modeled delivery or treatment does not predict a native stall or wait
until the horizon; selecting one does not establish completion. The comparator
does not rank unprojected support as zero or successful service. These are separate
consequence dimensions, not a health-to-labor score.
`coupled_observed` and journal/native evidence distinguish the prediction from
completed work.

Selected tasks use the existing observed continuation and release contract.
Changed demand, interrupted work and exact remaining quantities must be reconciled
from current observations and receipts. Predicted production is never actual
inventory, and delivery cannot credit supply twice. Consuming work cannot infer a
refund or authorize replay from a forecast.
If treatment fails after medicine pickup, the carried material remains observed
carry for normal recovery; no hidden substitution or refund repairs the forecast.
Emergency treatment retains the existing medical admission owner and safeguards.
Its receipts are distinct from any later coupled support Method.

Pure scenarios establish comparison and conservation. Prepared native cases
establish native observations, exact operations and physical outcomes when run;
registration and compilation alone are not gameplay evidence. Case reports retain
participant time and unfinished obligations rather than treating an accepted order
as completed work.

The affected acceptance inventory is:

| Case | Disposition and claim |
| --- | --- |
| `control/pawn-activity-delivery` | Keep exact delivery, destination and storage-filter checks. |
| `control/activity-*` | Keep native refusal, atomicity and player-takeover checks. |
