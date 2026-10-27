# Colony labor spec delta

## Purpose

Manage the colony's available labor toward its goals through native routine work, adaptive operating settings and justified direct interventions, accounting for interruptions and actual outcomes.

The maintainer-confirmed direction and proposed-detail boundary are recorded in the [decision provenance](../../proposal.md#decision-provenance-and-review-boundary). These new RG-LABOR IDs formalize that direction; scenarios illustrate acceptance and do not establish gameplay passes. Existing RG IDs and the [confirmed food specification](../../../../specs/food-security/spec.md) are unchanged. The [labor evidence page](../../../../../docs/requirements/colony-labor.md) owns implementation inspection, migration coverage and proof limits separately from food evidence.

## ADDED Requirements

### Requirement: RG-LABOR-001 — Let native systems execute routine work

While automatic control is active, the governor SHALL normally direct labor through native work priorities, timetables, bills, designations, policies and facilities. Native job selection, need responses and cleanup SHALL operate under those settings without requiring an exact governor assignment for every job.

#### Scenario: Routine food production is functioning

- **WHEN** native workers are producing and delivering food adequately under the colony's settings
- **THEN** the governor accounts for that work and its outcomes without issuing redundant exact cooking, hauling or eating assignments
- **AND** it continues assessing future demand and risk

#### Scenario: Native care becomes due

- **WHEN** a baby needs feeding and native conditions permit an available caregiver to feed it
- **THEN** routine governor control does not suppress native selection of feeding or its associated cleanup
- **AND** actual intake and care completion remain distinct observations

### Requirement: RG-LABOR-002 — Adapt operating settings under full colony control

The governor SHALL autonomously adjust labor priorities and schedules, and the work made available by colony settings, as goals and conditions change. It SHALL retain full control over these settings, including existing assignments. A setting change SHALL be assessed by its observed application and resulting conditions rather than presumed completed work.

#### Scenario: Colony work demand changes

- **WHEN** a harvest, injury, arrival or colony goal changes the demand for available workers
- **THEN** the governor can revise numbered priorities, schedules and relevant work settings without seeking approval for each reassignment
- **AND** unchanged or previously assigned settings receive no ownership exemption

#### Scenario: Regular daily timetable

- **WHEN** current service mechanics support a timetable for an ordinary worker
- **THEN** the governor generates regular sleep, recreation and work blocks, adjusting their budgets to observed mechanics and known time-of-day effects
- **AND** the blocks remain native preferences, without guaranteeing execution, intake, recovery or output
- **AND** missing service evidence does not fabricate a timetable or prevent independent supported priority changes

### Requirement: RG-LABOR-003 — Compare demand with realistic available capacity

The governor SHALL assess demand, bottlenecks and capacity across colony goals from observed needs, care, supply/reserves, access, native activity, travel, interruptions and realized output. It SHALL distinguish conditional estimates from unassessed future behavior. Timetable hours SHALL NOT guarantee production. An empty modeled schedule SHALL NOT predict native inactivity; missing evidence SHALL establish neither failure nor success.

#### Scenario: Eating is absent from the modeled schedule

- **WHEN** usable food is accessible and the governor selects no exact eating activity for a pawn eligible for native self-service
- **THEN** omission of modeled eating alone does not establish starvation or authorize a direct eating order
- **AND** actual intake remains observed rather than presumed successful

#### Scenario: Future native progress cannot be established

- **WHEN** current native activity or realized output is observed but its future timing or yield cannot be supported
- **THEN** the affected future estimate remains explicitly unassessed and is not credited as guaranteed stock or idle labor
- **AND** the uncertainty does not by itself establish a crisis, adequate service or a reason to stop unrelated supported decisions

#### Scenario: Scheduled labor is interrupted

- **WHEN** a worker spends scheduled work time resting, caring for a baby or responding to another native need
- **THEN** the assessment uses the resulting capacity and observed output
- **AND** it does not credit the scheduled hours as completed production

#### Scenario: Production is blocked by delivery

- **WHEN** a capable cook and ingredients exist but usable ingredients are not reaching the kitchen
- **THEN** the governor accounts for the delivery bottleneck when changing labor allocation
- **AND** cooking capability alone does not establish the required meal throughput

#### Scenario: Useful native work is preemptible

- **WHEN** a pawn performing useful native work could legally be redirected
- **THEN** intervention comparison accounts for the useful work displaced
- **AND** preemptibility alone does not make that pawn idle or its labor free

### Requirement: RG-LABOR-004 — Reserve direct orders for meaningful interventions

The governor SHALL justify direct orders by a concrete meaningful shortfall, credible risk, stalled essential work or worthwhile opportunity, assessing native progress, settings responses, disruption, displaced labor and uncertainty. A small efficiency gain or improvement over a no-modeled-service counterfactual SHALL NOT alone justify orders. Preventive action SHALL NOT require harm or an exact prediction of vanilla's future jobs.

#### Scenario: A marginally faster worker exists

- **WHEN** routine production is adequate and substituting another pawn offers only a small scheduling efficiency gain
- **THEN** that gain alone does not trigger a direct order
- **AND** the governor remains free to improve ordinary work settings

#### Scenario: A foreseeable shortfall needs timely action

- **WHEN** observed conditions indicate a meaningful shortfall before routine work or setting changes can adequately respond
- **THEN** the governor considers a bounded direct intervention before harm occurs
- **AND** compares the work and disruption needed against other colony needs

#### Scenario: Reserves support preventive action

- **WHEN** usable reserves, known upcoming demand or loss of supply, and supported recovery lead time indicate a meaningful risk before routine settings can adequately respond
- **THEN** the governor may justify a bounded intervention before an observed need causes harm
- **AND** it does not need to manufacture an exact future native job schedule or treat all unmodeled work as absent

#### Scenario: Native progress changes the intervention premise

- **WHEN** useful native progress or changed availability materially alters the premise of a proposed or interrupted direct order
- **THEN** fresh admission reassesses that premise against the current native baseline
- **AND** an old winning forecast or Method does not alone authorize dispatch or reissue

### Requirement: RG-LABOR-005 — Bound direct work and preserve native order semantics

The governor SHALL give direct interventions a specific purpose and completion or cancellation condition. It SHALL use native ordered-job and queue semantics where applicable, observe acceptance separately from completion, and let routine native selection resume when the intervention ends. It SHALL NOT add indefinite exclusive control beyond the intervention's justified work.

#### Scenario: A short sequence completes

- **WHEN** the governor orders a justified finite sequence and its required outcomes are observed
- **THEN** the intervention ends and normal native job selection resumes
- **AND** the pawn is not retained in an exclusive waiting assignment for speculative future work

#### Scenario: A forced job follows different interruption rules

- **WHEN** the governor issues a native forced order
- **THEN** its actual forced-order interruption and queue behavior applies
- **AND** neither universal interruptibility nor unconditional completion is presumed

### Requirement: RG-LABOR-006 — Reconcile observed native takeover

When native behavior interrupts or replaces governor work, the governor SHALL reconcile execution ownership, affected commitments and dependencies using observed state. It SHALL preserve actual partial outcomes, avoid replaying consumption or releasing another owner, and reconsider remaining needs. Stale intent SHALL NOT automatically reassert the displaced work over the native response.

#### Scenario: A participant is taken over

- **WHEN** native behavior takes over a participant in coordinated governor work
- **THEN** the governor withdraws stale execution ownership and reassesses the affected dependent work
- **AND** it does not cancel the replacement native job merely to restore its old schedule

#### Scenario: An interrupted task already consumed inputs

- **WHEN** some inputs were consumed or output produced before interruption
- **THEN** recovery uses the actual remaining stock, carried items and observed output
- **AND** it neither refunds consumed inputs nor counts unobserved output as available

### Requirement: RG-LABOR-007 — Protect ongoing native care from stale work

The governor SHALL account for current native care and its necessary cleanup when admitting interventions. A stale assignment or review SHALL NOT prematurely reclaim a caregiver or dependent during feeding, carrying or associated safe return. Completion of intake SHALL NOT by itself establish that the caregiver is available for competing work.

#### Scenario: Care starts after a planning read

- **WHEN** vanilla starts baby feeding after the governor considered a competing assignment
- **THEN** dispatch observes or rejects the now-conflicting intervention
- **AND** feeding and necessary safe return can finish before ordinary work reclaims the caregiver

### Requirement: RG-LABOR-008 — Reassess outcomes and explain meaningful interventions

The governor SHALL reassess labor decisions when observed progress, supply, worker availability or colony goals materially change. It SHALL expose the reason for a meaningful intervention, relevant blockers and its observed outcome through shared explanations. Missing evidence SHALL retain the affected need or uncertainty rather than establish success.

#### Scenario: Releasing work does not solve feeding

- **WHEN** competing work ends but the baby still receives no adequate nutrition
- **THEN** the governor retains the feeding need and reassesses the actual care, supply or access blocker
- **AND** release, job acceptance or a possible caregiver is not reported as successful feeding
