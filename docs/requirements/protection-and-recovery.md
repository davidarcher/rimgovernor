# Protect the colony and restore disrupted services

[Collection](README.md) · [Review queue](review-queue.md)

## Intent and evidence ownership

Protection means getting everyone the colony holds out of danger and keeping the
colony ready for the dangers its forecast expects. The
[protection specification](../../openspec/specs/protection/spec.md) is the standing owner of
RG-PROTECT-001 and its scenarios; the maintainer-confirmed policy and its provenance
are in the [decision record](../../openspec/changes/archive/2026-11-05-confirm-remaining-capabilities/proposal.md#decision-provenance-and-review-boundary). This page owns implementation inspection,
executed results and proof limits, not requirements. Its inspection history (P1–P6)
is preserved as recorded. The civilian exposure estimates approved by the maintainer
and their limits remain in P6. The fight itself is owned by the
[combat specification](../../openspec/specs/combat/spec.md) (RG-COMBAT-001–004,
confirmed 2026-11-05); P4 and P5 are the inspected combat paths it cites, and the
equipping hold named in P5 is a defect to remove under that decision, not a known gap.

## Requirement map

Each requirement is stated once, in the specification.

- RG-PROTECT-001: [Protect everyone the colony holds from immediate danger](../../openspec/specs/protection/spec.md#requirement-rg-protect-001--protect-everyone-the-colony-holds-from-immediate-danger)

## Retired requirements

RG-PROTECT-002 — Verify restoration after disruption was retired on 2026-11-05.
Service restoration is compared as measured work under
[RG-HABITAT-003](../../openspec/specs/habitat/spec.md#requirement-rg-habitat-003--develop-facilities-for-the-benefits-they-provide),
which distinguishes causes of service failure; the cause of an outage does not
matter to whether a repair is worth doing, and a service stays deficient until its
operation is observed. The identity is not reused. Evidence P1, P3 and P4 describe
the recovery paths that habitat now owns.

## Implementation status of the original requirements

The status below was recorded before the specification owned the requirement text and stays with the evidence it cites.

### Status of RG-PROTECT-001 (respond to immediate danger)

**Intent:** Documented in [disaster planning](../developers/contracts/disaster-planning.md)
and [controller contracts](../developers/contracts/controller-contracts.md).
**Implementation:** Partial. Shelter restrictions, firefighting release and
combat dispatch are connected; precautionary disarming and routine defensive
equipping have explicit holds. Evidence P1–P2, P4–P5. Survival across raids is
an execution-evidence limit.

Forecast harm must distinguish unarmed people, people incapable of violence and
people already downed, using the same native facts as recorded fights. These
states require their own evidence; shelter intent does not establish immunity.
The user-approved empirical estimates and their limits are recorded in P6.

### Status of retired RG-PROTECT-002 (verify restoration after disruption)

**Intent:** Documented in [disaster planning](../developers/contracts/disaster-planning.md).
**Implementation:** Supported for the inspected service-state path and explicit
native repair. The disaster family no longer selects work: `power/basic` refuels,
acquires fuel for or fixes lost producers and `home-upkeep/repairs` repairs and fixes
damaged buildings, each compared as measured servicing work
([H12](habitat.md#h12--servicing-and-power-restoration-compared-as-measured-work)), and
`disaster/services` closes only on the disaster history's fresh finding. Autonomous
episode closure still needs execution evidence; the native case `upkeep/restore-service`
is registered but unexecuted. Evidence P1, P3–P5.

## Evidence and inspection coverage

- **P1 — From danger to work.** [DisasterReview](../../brain/src/RimGovernor.Brain.Runtime/Review/DisasterReview.Disaster.cs)
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
owns patient outcomes. Raid tactics, hunting injuries, toxic exposure and fire
spread need broader outcome evidence than the sampled dispatch cases. See
[T9](review-queue.md#t9--observe-restored-service-after-disruption).

### P4 — Native recovery and combat effects

[DisasterRecoveryReadTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/DisasterRecoveryReadTests.cs)
checks that pending work reads the buildings' hit points and fuel states against the native
auto-refuel fraction, rejects invalid service facts and keeps unread recovery unresolved; the
repair, refuel and breakdown selection it once checked is deleted, and the owning families'
comparisons are [H12](habitat.md#h12--servicing-and-power-restoration-compared-as-measured-work).
[recovery/service](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Recovery/RecoveryService.cs)
explicitly repairs a damaged wall to full health; it does not exercise autonomous
episode closure. [recovery/area](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Recovery/RecoveryArea.cs)
checks restrictions and refuses clearing them during a roof hazard. It does not
assert that each pawn reaches shelter before injury.

[ReviewCombat](../../brain/src/RimGovernor.Brain.Runtime/Review/DefensePlanner.Combat.cs)
reads the fight, chooses orders, commits a batch and sends it through Hands.
[CombatStep](../../brain/src/RimGovernor.Brain.Runtime/Review/CombatReads.CombatStep.cs)
reconsiders the fight from fresh observations. The registered
[combat/attack](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Combat/CombatCases.cs)
checks explicit melee, rifle and grenade attacks against prepared targets,
requiring the target down or dead. It does not prove autonomous tactics.
[post-fight work](../../brain/src/RimGovernor.Brain.Runtime/Review/DefensePlanner.PostFight.cs)
rechecks enemies, stripping and custody candidates; care belongs to RG-HEALTH-001.

### P5 — Confirmed planner holds

[MilitaryFamily.Propose](../../brain/src/RimGovernor.Brain.Policy/Military.cs)
holds defensive equipping as `unsupported_forecast_equipment`;
[MilitaryFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/MilitaryFamilyTests.cs)
asserts that hold while admitting an independent pest hunt.
[AdminFamily.Propose](../../brain/src/RimGovernor.Brain.Policy/ColonyAdmin.cs)
holds precautionary creepjoiner weapon drops. These gaps are tracked in
[#4008](https://github.com/davidarcher/rimgovernor/issues/4008) and
[#4010](https://github.com/davidarcher/rimgovernor/issues/4010). Immediate combat
and food-specific hunting prerequisites have separate supported paths.
Ordinary repairs were held by HomeUpkeepFamily
([#4006](https://github.com/davidarcher/rimgovernor/issues/4006)); milestone 5 compares
and orders them instead
([H12](habitat.md#h12--servicing-and-power-restoration-compared-as-measured-work)).

### P6 — Civilian exposure and forecast evidence

[HarmExposureStateTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HarmExposureStateTests.cs)
checks shared live/sweep roles and finite civilian fight, care and raid prices,
including propagation of empirical and sparse-data findings.
[EmpiricalExposureTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/EmpiricalExposureTests.cs)
checks whole-raid correlation, consistent fixture weights, deterministic
regeneration and actual zero-hit observations. Four pawns in one fight do not
become four independent raids. Missing cells or fewer than 20 recorded raids refuse.

The native calibration recorded 280 civilian fights across five raid-size bins;
80 were additional checks after the first 200. The committed
[evidence and raw rows](../../brain/src/RimGovernor.Brain.Acceptance/Sweep/calibration/exposure/README.md)
retain both failed historical stability rounds, source revisions, game hash and
sampling provenance. All 80 additional fights resolved; one original fight reached
its unchanged tick limit and its bounded observations remain included.

Following explicit user approval, all recorded rows now fit 22 qualified empirical
cells, covering the three civilian states in every bin alongside existing combatant
coverage. Every reading reports empirical estimation and unconfirmed stability,
with sparse/zero-hit findings where applicable. Whole raids are resampled within
fixtures with fixed mixture weights. The hit and down ranges are descriptive;
they do not certify accuracy or supply a valid upper risk bound for unseen events.
Observed zero hits is not immunity. A dead pawn alone has an exact no-future-hits reason.

These checks and the final full gate at `d0fe7897d` provide executed native sample
and policy evidence, not a guarantee of survival or accuracy on other layouts.
The [combat exposure contract](../developers/contracts/forecast-contracts.md#combat-exposure-states)
owns the forecast qualifications.
