# Design

> Superseded on 2026-10-26 by the [native-led labor design](../native-led-labor-execution/design.md). The advance-release and caregiver allocation decisions below are retained as historical design, not current implementation instructions. The labor change owns their removal and the native feeding proof; confirmed food requirements remain unchanged.

## Context

See [proposal.md](proposal.md) and [the food delta](specs/food-security/spec.md).
The confirmed feeding requirements and the delta's outcome scenarios are unchanged.
The maintainer explicitly requested vanilla-owned feeding, observation and reactive
coordination, with additional prediction or execution constraints justified by
concrete failures. This replaces the earlier design's exact-input approach.

The inspected integration has three relevant behaviors:

- `BabyFeedingRules.ReviewBabyFeeding` suppresses a baby-food shortage whenever
  any breastfeeding-capable pawn exists. That establishes neither available care
  nor observed intake.
- `NativeBabyCare` already observes food level, active care and conditional
  breastfeeding/bottle-feeding opportunities. `ForecastBabyCare` uses those
  opportunities for nursery exposure bounds; it allocates nobody.
- `NativePawnActivities` blocks vanilla selection, job overrides and finalizers
  for committed workers. Unowned colonists run vanilla. Current incumbent release
  supports particular services and work boundaries; merely adding a care option
  cannot free a busy caregiver.

Inspection of the installed native drivers also established that bottle-feeding
can select food from inventory, both feeding modes choose a sitting/standing
location, and their lifecycle can return the baby to safety afterward. These are
native mechanics to integrate with, not reasons to reproduce the drivers.

## Goals / Non-Goals

**Goals:** Make normal automatic control compatible with timely vanilla feeding,
including when eligible adults are busy. Observe each baby's outcome, make room
for care when justified, protect the active care lifecycle from conflicting work,
and react to interruption, inadequate progress and unavailable supply.

**Non-Goals:** A new childcare scheduler, copied feeding drivers, exact food-stack
selection, a milk-allocation solver or detailed chair/path forecasts. Patient,
animal and away feeding remain outside this on-map baby fix. These boundaries do
not relax any confirmed requirement or certify the whole of RG-FOOD-008.

## Decisions

### 1. Observe the need and the outcome with existing facts

Food remains the owner of feeding demand. Use current hunger and condition,
active native care, eligible caregivers and usable nursing/food observations.
Extend GameFacts only for a fact needed by the response or its outcome check.
A capable nurse, a proposed job or suitable stock is not evidence of intake.
Existing native feeding that succeeds needs no redundant governor assignment.

Policy decides whether the baby needs a response and whether competing work should
change. Runtime sequences the existing review and Method lifecycle. Keep expected
supply separate from observed stock and intake. Missing facts leave the relevant
claim unresolved and visible; do not invent zero travel or guaranteed recovery.

Alternative: forecast every feeding alternative and its full timeline. That adds
precision before a demonstrated need and does not fix the current ownership block.

### 2. Coordinate a feeding opportunity through the existing shared lifecycle

Use the existing Need/Method, admission, journal and Hands path to make caregiver
capacity available. Native childcare chooses its legal feeding work and performs
pickup, food/milk consumption, seating, feeding and safe return. A coarse care
response may identify the dependent and eligible caregiver; it does not bind a
food lot, count or chair. Reuse native job creation/selection where a response
needs to start care, without copying native legality or consumption mechanics.

Making room is a response with an observed outcome, not proof that care will
happen. If vanilla does not begin or complete useful feeding, the next assessment
must retain the need and change the response or expose its blocker. Releasing a
worker into indefinitely unobserved native work is not a completed response.

Reuse exact assignment revisions for any change to existing governor work.
Preserve carried inputs, partial work and dependent participants when releasing
or pausing that work. Keep combat and involuntary protection boundaries. A losing
response changes no ownership, and replacement work cannot acquire a caregiver
whose earlier owner has not actually released it.

### 3. Account for actual occupation through safe return

During active native feeding, the caregiver and baby are occupied. Shared
admission and continuation must not assign them incompatible work. Preserve that
protection while the caregiver is still carrying the baby or performing the
associated native safe-return job. Food received and care finished are separate
observations; do not reclaim the worker immediately on a food-level increase.

Permit the associated native continuation through the current ownership hooks
without authorizing arbitrary unrelated jobs. Observe the actual native care
chain and release its protection when cleanup is complete. An interruption must
leave the baby, caregiver and carried state visible to ordinary recovery, not
strand a baby under a completed feeding claim.

Native reservations, carried/held resources and current consumption are inputs to
the existing shared resource view. Do not count a nursing possibility as future
stock, refund consumed inputs or keep stale stock available after observation.
Protect known conflicts using that view and current care occupation. Do not build
a general ledger of every food vanilla might choose. If a real reservation or
consumption race defeats the existing boundary, reproduce it and add the smallest
shared correction that resolves that failure.

Alternative: let childcare bypass all ownership checks. That leaves a caregiver
promised to two activities and can interrupt safe return. The required boundary
is coordinated occupation, not control of vanilla's internal food selection.

### 4. React before preventable feeding harm

Use the existing review/event mechanisms to reassess food received, hunger and
condition, care starting/ending, caregiver availability and supply changes. A
feeding animation can precede an increase in food level, especially because the
bottle driver accumulates nutrition before ingestion. Lack of an increase on one
sample is not itself a stalled feed. Ground any no-progress check in the native
phase/mechanics and test it against real progress rather than inventing a timeout.

When care is blocked or stops helping, release justified competing work, consider
another legal caregiver or feeding source, and arrange missing suitable provision.
Retain an unmet need and explain food versus care/access blockers when no timely
response exists. A worsening trend must not wait for starvation before it can
trigger action. Timeliness needs enough native information to act before harm;
it does not require an exact full-bout forecast.

Multiple babies share real caregiver and feeding capacity. Current occupation and
unmet demand must remain visible for all of them; serving one must not declare
another adequately fed. Reassess finite milk and food as they change. Correct the
blanket breastfeeding-count exemption using usable care/supply and observed
outcomes, without adding a parallel reserve model or requiring bottle food even
when nursing is working and can provide usable cover. Keep longer-term reserves
with the existing food-supply owner.

### 5. Add precision only to address a demonstrated failure

Begin with focused tests of the known integration failures: governor work blocks
care, active care can be reclaimed too soon, and breastfeeding capability can
mask an unfed baby. Then exercise both native feeding modes under normal bot
control. Observe interruption, unavailable care and competition between babies.

For any additional forecast, reservation scheme or native execution constraint,
record the concrete failure, the smallest regression reproducing it, why the
current observation/response cannot prevent it, and the narrower fix. A code
trace can establish an ownership conflict; a hypothetical inventory choice or
uncertain route alone does not justify an exact-input adapter. No extension is
required solely to restore the previous design's precision. Any real gap against
the confirmed scenarios remains in scope and must be fixed, not relabeled as
acceptable uncertainty.

## Risks / Trade-offs

- Reactive care could respond too late -> test a hungry baby with an occupied
  eligible adult and assert intake before staged harmful deterioration; wake the
  existing review when the necessary observed state changes.
- A native care opportunity may not be taken -> observe actual work and progress;
  retain the need and respond again rather than equating release with success.
- Feeding may finish while the baby is still carried -> protect and observe the
  native safe-return continuation before ordinary work can reclaim the caregiver.
- Native consumption may differ from a supply estimate -> reconcile current
  reservations, held inputs and actual stock; add tighter constraints only for a
  demonstrated conflicting commitment or preventable feeding failure.
- Simpler planning trades scheduling precision for observation and adjustment ->
  accept conservative current capacity, test response timeliness, and avoid
  claiming sustained or universal feeding coverage from isolated cases.

## Migration Plan

Keep native observations in GameFacts, any needed intent in the existing typed
request/action path, Method progress in the session journal and derived assessment
in memory. No new state store, milk ledger or mandatory feeding-payload/codec
migration is planned. Change a boundary only where the working response needs it.

Update the owning support-work, pawn-activities, voluntary-control and affected
continuation/forecast contracts with the implementation, maintaining links from
`docs/README.md`. Update the food evidence page with scoped results and provenance.
Delete superseded count-only assumptions and anything made unread by the fix.
No code was changed during this planning revision. A later implementation revert
uses the normal lane; no fallback feeding subsystem is introduced.

## Architecture Rules 7–9 Review

- Pure Policy assesses need, urgency and response; Runtime sequences and Hands
  changes governor orders. Vanilla performs childcare mechanics.
- Shared demand, admission and current native occupation govern competing work.
  Existing journal actions own commitments; native reservations and actual stock
  inform the same resource view without a second food or care ledger.
- Read the necessary facts or expose a named unavailable state. Possible care is
  neither completed feeding nor certain future supply. Any additional precision
  must trace to a concrete failure and its regression.
