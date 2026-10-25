# Protect the colony and restore disrupted services

[Collection](README.md) · [Review queue](review-queue.md)

Protection includes getting people out of danger, making urgent work possible and
restoring the services damaged by an event.

## RG-PROTECT-001 — Respond to immediate danger

The governor should recognize supported threats, arrange protective action and
reconsider ordinary work that exposes people to harm. Available observations must
justify the response. When a danger clears, the colony should regain useful work
under a fresh assessment.

For example, a home fire should allow an eligible firefighter to respond. People
who need shelter should receive appropriate area restrictions. An unread threat
must remain unresolved. Combat, evacuation and fire each need evidence suited to
their actual effects; starting a response is one step toward safety.

**Intent:** Documented in [disaster planning](../developers/contracts/disaster-planning.md)
and [controller contracts](../developers/contracts/controller-contracts.md).
**Implementation:** Supported for sampled shelter-area planning and native fire
response release; combat success, evacuation and survival remain unassessed.
Evidence P1–P2.

## RG-PROTECT-002 — Verify restoration after disruption

The governor should track affected services through an event and arrange repair,
refuelling or replacement where supported. It should report restoration only when
observations show those services recovered. This keeps the recovery plan tied to
what the colony still lacks after the original danger ends.

When a solar flare ends but a facility remains damaged, recovery should continue.
A missing building or unread service should leave its outcome unresolved. When
all affected services recover, the episode should close. Renewed damage should
reopen the relevant work.

**Intent:** Documented in [disaster planning](../developers/contracts/disaster-planning.md).
**Implementation:** Supported for the inspected service-state and work-selection
path; complete native restoration remains unassessed. Evidence P1, P3.

## Evidence and inspection coverage

- **P1 — From danger to work.** [DisasterRounds](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsDisaster.cs)
  reads current emergency and service facts. Sheltering commits owed area changes;
  the ordinary services pass chooses recovery work under the need. The read rejects
  a different world or observations older than the review. The
  [disaster contract](../developers/contracts/disaster-planning.md) defines the
  observed service conditions for disruption, temporary survival and restoration.
- **P2 — Native fire response.** Registered OnDemand
  [ControlHomeFire](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Control/ControlHomeFire.cs)
  uses a staged home fire and installed activity ownership. Its assertions check
  release of the firefighter's assignment and a firefighting job, while the other
  pawn's sleeping assignment stays owned. This sample concerns work interruption;
  it does not establish that the fire is extinguished or buildings survive.
- **P3 — Recovery decisions.** [DisasterFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/DisasterFamilyTests.cs)
  checks owed shelter moves, service deficits, missing observations and claims.
  Its journal test admits a shelter move alongside service work and prevents a
  conflicting second area change. These are supplied-state decisions.

[Habitat](habitat.md) owns usable services and [medical care](colonist-wellbeing.md)
owns patient outcomes. Raid tactics, hunting injuries, toxic exposure, fire spread
and post-combat recovery remain unassessed here. See
[T9](review-queue.md#t9--observe-restored-service-after-disruption).
