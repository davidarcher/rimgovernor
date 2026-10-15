# Evaluated support work

[Documentation](../../README.md) · [Portfolio selection](../architecture/portfolio-selection.md) · [Pawn activities](pawn-activities.md)

Support work serves observed production, care or material demand. Family providers
offer exact cleaning and delivery operations to the existing shared composition
and ColonyForecast comparison. Policy chooses participants, quantities and timing;
the existing Method journal owns admitted work and Hands issues its orders.
There is no separate helper scheduler, demand ledger or progress store.

## Cleaning consequences

Cleaning is optional preparation for a service, not an unconditional prerequisite.
Self-service, a separate helper, immediate service without cleaning, waiting and
no support must remain distinguishable alternatives. Travel, cleaning, service,
waiting and displaced work occupy the same shared pawn timeline. Urgent care
cannot acquire an invented requirement to finish cleaning first.

Native room mechanics bound the supported consequences. Food production samples
the room's food-poison chance when the recipe produces its output. Tending can
sample the room's infection factor into the wound's later infection calculation;
that factor is conditional on other native state. It is not an absolute infection
probability, prognosis or guaranteed health improvement. Cleaning afterward cannot
change a factor already sampled by a completed service. Post-care work needs a
remaining room or service demand, rather than retroactive credit for treatment.
Surgery uses the native room cleanliness success factor for the same supported
operation. That factor is distinct from an overall surgical success probability;
other native success conditions still apply.

Missing room effects, routes, eligible workers or service timing remain explicit
unknowns. An unknown cleanliness benefit never becomes a free health guarantee.
The bounded route census prioritizes relevant cleaning/service transitions in
both directions; remaining omissions are reported rather than treated as zero
travel. Removing observed filth supplies a reusable condition to dependent work,
so multiple services can share the physical cleaning without paying or crediting
it twice. An after-care realization can supply the same condition to later work.
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

The current refill provider stages one exact ingredient definition at the observed
bench interaction cell. This is an outstanding bill requirement, not a generic
stockpile refill target. Unsupported mixed substitutions remain unassessed.

A chunk is eligible because a supported material or recipe requirement needs it,
not merely because a stockpile accepts chunks. Unrelated chunks remain outside
that demand. Native storage filters, reservations and work legality still apply;
global item definitions are not changed to make all chunks haulable.

## Execution and evidence

The existing `coupled_selection` decision includes first-care latency, pending
care batches, unsupplied demand, food-production identity and room effect,
served-care identity, tend quality and surgical room factor. Unknown values remain
explicit. These are separate consequence dimensions, not a health-to-labor score.
`coupled_observed` and journal/native evidence distinguish the prediction from
completed work; the prepared support cases read both signals.

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
| `clean/bench` | Rewrite: selected cleaning and actual bench output; remove the five-filth job-substitution assertion. |
| `clean/care` | Rewrite: observed critical-owner treatment within the fixture service budget and separately journaled useful cleanup; remove surgery job substitution and tend-finalizer assertions. The budget is not a health prognosis. |
| `clean/care-routine` | Add: observed selected treatment for a nonbleeding injury through the coupled Method and Hands. |
| `storage/refill-hysteresis` | Rewrite: demand-bounded delivery and production; remove full-stack withholding assertions. The name remains for case continuity. |
| `storage/changed-demand` | Add: withdraw a bill after selected pickup and observe release of the obsolete obligation. |
| `clearance/chunk-dump` | Rewrite: demanded chunk pickup and bounded delivery while unrelated chunks stay untouched; remove global hauling expectations. Stonecutting output is diagnostic, not proof of a new exact recipe adapter. |
| `control/pawn-activity-delivery` | Keep exact delivery, destination and storage-filter checks. |
| `control/pawn-activity-lifecycle` | Keep native refusal, atomicity and player-takeover checks. |

The old bench-cleaning and care-cleaning probe operations and refill fixture
are removed. Unrelated clearance scenarios retain their own fixtures and claims.
