# Design

## Context

See [proposal.md](proposal.md) for motivation and [the delta](specs/food-security/spec.md)
for acceptance scenarios. Inspection was refreshed at `9a38be6f9`.

- `BabyFeedingRules.ReviewBabyFeeding` accepts a breastfeeding-pawn count and
  sets `IsShort = !Breastfed && stockNutrition < floor`. The Food family's
  baby-feeding Need represents provisioning; its remedy is a standing bill.
- `NativeBabyCare` already reads each baby's food level, native feeding
  eligibility, lactation charge/refill and conditional care opportunities.
  `BabyCareOpportunityRead` lacks exact food identity, route/service timing and
  execution binding. `ForecastBabyCare` bounds possible native care for nursery
  exposure; it explicitly allocates nobody. These bounds cannot authorize work.
- `SupportMethodCandidates` currently generates medical care/surgery and
  deliveries. `ForecastNeeds` prices a selected pawn's own ingestion, not a
  caregiver's work benefiting a different pawn. Babies need recipient food
  consequences even when they are not eligible productive workers.
- `NativePawnActivities` already executes exact Work steps through guarded
  native jobs, but its supported workgivers do not include feeding. For committed
  workers, vanilla selection is suppressed. Unowned colonists can feed through
  vanilla, which is useful observed behavior but no allocation guarantee.
- `ForecastIncumbentPreparation`, `ForecastCoupledExecution` and
  `CoordinatedActivityBinding` own reconsideration and revisioned release.
  Supported retained services and productive workers at task boundaries have
  specific rules. They do not provide a general release for an actively busy
  caregiver simply because a baby's food is falling.

## Goals / Non-Goals

**Goals:** A single shared assessment must connect a baby's unmet feeding demand,
legal supply, caregiver time, competing obligations and observed intake. Both
breastfeeding and bottle-feeding must be executable under ordinary automatic
control. Planning, journal admission and native guards must agree about resources.

**Non-Goals:** Introduce a childcare scheduler, make nursery play an executable
service, replace RimWorld feeding mechanics, or generalize this milestone into
all medical, animal or away feeding. Existing native satisfied feeding is credited
without requiring a redundant governor action. No universal interruption or
free-capacity assumption is introduced for protected actors or unknown work.

## Decisions

### 1. Extend the canonical baby-care facts with exact feeding alternatives

Extend the existing GameFacts baby-care boundary, rather than copy its observations
into a second support census. Keep the conditional nursery exposure view distinct
from exact executable options, with both derived from one native read. Each exact
option identifies baby, caregiver, native feeding mode, current eligibility,
reachable approach/source/service geometry, service duration/gain bounds and the
source it actually uses. Bottle feeding carries a physical stack identity and
count with suitability and access observed for that pair. Nursing carries current
usable charge, refill and native restrictions for that caregiver/baby pair.

Use `ChildcareUtility` and the existing native drivers to derive eligibility and
effects. Verify the installed assembly's feeding job/workgiver symbols and exact
target/count/reservation semantics before implementing the adapter or building;
do not infer them from display names. A native food search may identify an option,
but execution must preserve that option's selected input rather than silently
choose a different source. Revalidate current facts at admission/start.

The existing broad roaming position bounds remain valid exposure bounds where
native positioning is variable; they are not a fabricated exact route. Unsupported
geometry or service mechanics produce named unavailable options/findings, not
zero travel. No required fact is defaulted to success. An unavailable option does
not prevent other completely observed alternatives from being considered.

Alternative: use the current breastfeeding count or conditional occupancy bound.
Both are cheaper but omit the very capacity and access constraints in #4040.

### 2. Put feeding demand and consequences in the shared comparison

Food retains ownership of the feeding outcome. Distinguish its provisioning
subproblem from each baby's immediate assisted-feeding demand; avoid a second
family or duplicate ledger. Pure feeding policy generates alternatives through
the existing coordinated candidate composition. Extend the shared food-need
trajectory/consequence path to credit the recipient's useful nutrition during
the selected native service, with hunger/starvation exposure while waiting and
the observed native gain and loss bounds. Keep uncertain effects bounded, not
invented forecasts of health recovery. The recipient is not added as spare labor.

Charge the caregiver's approach, obtaining food, carrying/positioning as applicable
and service time. The baby is an exclusive service target. Bottle inputs enter
the existing physical material accounting by lot/count, shared with adult eating,
production and other babies. Nursing uses a caregiver-specific finite charge and
refill constraint derived from observations, alongside that caregiver's occupied
time; compare all babies against that same capacity. It is a transient forecast
constraint, not a stored milk inventory. Compatible sequential feeds are allowed;
neither input nor service benefit is counted twice.

Integrate resulting demand/consequences into the existing shared comparator and
Need tiers. Do not create a baby-only score that always wins or a fixed hunger
threshold that defines adequacy. Feeding competes on projected consequences,
including the work forgone and native protection boundaries. Add feeding benefit
to the same schedules used for nursery exposure so selected feeding is not also
credited as independent possible native care.

Alternative: add feeding to medical support as a completed-service count alone.
That would miss recipient hunger timing and cannot distinguish timely intake from
a late feeding after preventable harm.

### 3. Make caregiver release part of the selected, executable response

Reuse the existing conditional incumbent comparison, exact Method journal and
Hands release path. Include the retained work's remaining consequences when
comparing a feeding replacement; losing candidates release nothing. A selected
release must name the observed assignment/revision, preserve independent
participants and their dependencies, and wait for fresh released ownership
before replacement admission. CAS mismatch triggers reobservation through the
existing lifecycle. A forecast that assumes release without an executable release
must be rejected.

Extend the current reconsideration boundary for caregiver work as needed to cover
the issue's busy-caregiver path. In-flight productive work requires an observed
safe cancellation/continuation description: partial progress, remaining inputs,
carried items, native reservations and dependent tasks must be reconciled. Do not
discard these obligations or treat all occupied time as free. Where cancellation
is not legal, compare a reachable safe boundary or another caregiver/source;
report a genuine shortfall if none is timely. Protected combat, forced work and
involuntary states retain their existing owners.

This change cannot stop at adding candidates that are then filtered out because
every caregiver is claimed. Decision tests must begin with a real modeled
incumbent and show retained work losing when preventable feeding harm justifies
replacement, with the same release actually bindable and admitted.

Alternative: release caregivers into vanilla and hope Childcare is chosen, or
whitelist native childcare past committed ownership. Both create unpriced work
outside shared claims and fail to establish who feeds which baby in time.

### 4. Bind an exact native feeding Work service through Hands

Reuse the existing Work activity kind with a typed feeding payload for mode,
recipient and exact source/quantity as required. Add this to GameOrders and the
existing candidate, binding, journal codec, continuation and native validation
paths. A typed feeding variant keeps medical fields from being repurposed and
allows invariants to be checked. No independent order endpoint is needed.

The selected native feeding driver performs game mechanics. Native validates the
pair and source again and starts only the authorized feeding job; stale or illegal
bindings return the existing explicit refusal/failure path. Follow native target
and reservation semantics for babies carried by a caregiver and babies in beds.
Starting or accepting the job is not proof of feeding. Observe food-level/intake
progress, job/ownership state, source change and relevant condition. After
interruption retain actual nutrition gained, release only the attempt's claims
and rederive the remaining need. Never replay milk or food consumption from a
missing receipt. Existing native feeding already in progress must be represented
as occupied care with observed intake, not a free second feeding opportunity.

### 5. Reconcile alternate provision without the blanket nursing exemption

Replace `Breastfed` as a colony-wide adequacy assertion. Share the finite feasible
nursing assessment with baby-food provisioning and carry the uncovered demand
into the existing supply/bill path. A potential nurse with no usable timely
capacity cannot suppress bottle-food demand. Conversely, feasible nursing may
reduce the portion of provision it can actually cover; do not unconditionally
require manufactured food in addition to adequate nursing.

Keep reserve planning and immediate feeding separate. A bounded nursing
projection cannot claim indefinite reserve coverage; its horizon and refill/time
constraints must be explicit. Raw ingredients or a promised bill output stay
future supply until observed. Reassess when nurse, milk, baby count or food source
changes. Expose unavailable care separately from missing food, so producing more
baby food does not masquerade as fixing an absent caregiver.

Remove the superseded count-only inputs, fields, comments and tests across their
actual readers, including native/wire reads that become dead. Preserve lactation
observations used elsewhere; an orphan sweep determines deletion, not a blanket
removal of Biotech facts.

### 6. Keep verification tied to distinct claims

Extend nearby policy tests (`FoodFamilyTests`, `SupportMethodTests`, incumbent
preparation/selection tests and baby-care exposure tests as affected). Exercise
actual candidate generation, comparison, binding and admission, not just a helper
with a manually chosen result. Cover finite milk and shared lots across babies,
no available care, feasible alternates, normal work competition, revisioned
release and interrupted intake. Ensure native care remains an observation rather
than double-counted forecast credit.

Register short staged native cases under the existing child case registration:
one for actual bottle intake after resolving competing governor work and one for
actual breastfeeding through its exact adapter. Each case owns one claim, starts
with the precondition observed, enables normal shared work selection, and stops
as soon as the baby's observed food rises through that feeding path. Assert the
caregiver's incumbent and selected feeding identity so incidental idle vanilla
feeding cannot pass the contested-work claim. Setup may stage food, charge and
work; after setup it must not assign feeding or free the caregiver itself.

Use game ticks and registered telemetry kinds/typed reads. Target about one minute
wall time per case, under two including startup. Do not wait for ageing or extend
a failing budget. The old first-child case retains only its original claim; a
failure there must be diagnosed independently rather than relabeled as #4040.

## Risks / Trade-offs

- Native feeding may select secondary targets or move a baby -> inspect exact
  driver semantics and retain source/recipient/position ownership through the
  existing activity lifecycle; do not allow hidden source reselection.
- Releasing productive work can strand inputs or other workers -> make dependent
  claims, partial progress and safe release explicit, and test the real binding
  path before trusting a favorable forecast.
- Future milk and native routing are bounded -> expose limits, admit only options
  with sufficient known mechanics, and reobserve; possible service is not success.
- A finite source or caregiver may serve several dependents -> use one shared
  resource/time constraint and validate admission as well as forecast feasibility.
- Broad food-spec support could be overstated -> update only the inspected baby
  paths in the evidence page; keep registered, executed and passed distinct.

## Migration Plan

No persistent feeding store or save-owned intent is added. Native facts stay in
GameFacts, selected requests in GameOrders, derived forecasts in memory and
attempt progress in the existing session journal. Amend the persistence contract
if that ownership description changes; do not introduce a second copy silently.
Update the journal codec for the typed service with its normal current-version
handling; regenerate affected disposable saves/fixtures instead of keeping dead
wire fields or compatibility shims.

Update the owning support-work, pawn-activities, voluntary-control, forecast and
action-completion contracts alongside implementation, and maintain their links
from `docs/README.md`. Add the baby audit, issue link and subsequently obtained
proof to `docs/requirements/food-security.md`. Synchronize the reviewed delta and
archive only through their explicit workflows after implementation is complete.
Rollback is a normal code revert of the milestone, not a runtime fallback mode.

## Architecture Rules 7–9 Review

- Keep decision math in pure Policy; Runtime composes facts and coordinates the
  existing lifecycle. Hands remains the sole order writer.
- Shared feeding demand, recipient consequences, caregiver time, baby target,
  physical food and milk capacity participate in one composition/admission path.
  No private assignment owner or duplicate material ledger is introduced.
- Native owns legality and observed mechanics. Missing facts name the unavailable
  state or remove the option; no zero-duration or assumed-success substitution
  is permitted. Unmet feeding remains visible.
