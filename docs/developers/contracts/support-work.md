# Evaluated support work

[Documentation](../../README.md) · [Portfolio selection](../architecture/portfolio-selection.md) · [Pawn activities](pawn-activities.md)

Support work serves observed production, care or material demand. Family providers
offer exact care, surgery and delivery operations to the existing shared composition
and ColonyForecast comparison. Policy chooses participants, quantities and timing;
the existing Method journal owns admitted work and Hands issues its orders.
There is no separate helper scheduler, demand ledger or progress store.

Cleaning is not coupled support work (#3662): vanilla cleans the home area under
each colonist's Cleaning priority while no coupled order holds it. The forecast
reads each room as it stands; it does not propose cleaning or credit a cleaner room.

## Room consequences

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

The current refill provider stages one exact ingredient definition at the observed
bench interaction cell. This is an outstanding bill requirement, not a generic
stockpile refill target. Unsupported mixed substitutions remain unassessed.

A chunk is eligible because a supported material or recipe requirement needs it,
not merely because a stockpile accepts chunks. Unrelated chunks remain outside
that demand. Native storage filters, reservations and work legality still apply;
global item definitions are not changed to make all chunks haulable.

Carrying a loose stack to storage with no outstanding requirement is vanilla's:
a colonist on standby hauls it under its Hauling work priority (#3662). The brain
has no haul path of its own for it.

## Execution and evidence

The existing `coupled_selection` decision includes first-care latency, pending
care batches, unsupplied demand, food-production identity and room effect,
served-care identity, tend quality and surgical room factor. Unknown values remain
explicit. These are separate consequence dimensions, not a health-to-labor score.
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