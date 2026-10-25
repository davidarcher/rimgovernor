# Keep colonists healthy and able to work

[Collection](README.md) · [Review queue](review-queue.md)

Care has to reach the patient. Rest, recreation and equipment also affect whether
people can safely carry out the work the colony depends on.

## RG-HEALTH-001 — Treat urgent patients

The governor should detect urgent patients, select eligible doctors or rescuers
and follow the work until its outcome is observed. It should reconsider care when
the patient's condition or the available workers change. This protects people
whose condition cannot wait for ordinary development work.

With two patients and two available doctors, each doctor should receive one
patient. If a doctor becomes unavailable, a fresh eligible pairing should be
considered. A missing health observation should leave care unresolved. Completion
requires a change in the patient's treatment state; recovery may take longer.

**Intent:** Documented in [medical care](../developers/contracts/medical-care.md).
**Implementation:** Supported for the sampled pairing and native tending dispatch;
completed autonomous treatment and replacement-doctor recovery remain unassessed.
The contract's eligible-patient and care-policy conditions apply. Evidence W1–W3.

## RG-HEALTH-002 — Sustain recovery and everyday wellbeing

The governor should arrange ongoing care, medicine, rest, recreation and suitable
equipment as needs arise. Observed improvement should determine whether the need
has been met. This keeps chronic illness and routine deprivation from undermining
the colony after an immediate emergency has passed.

A recovering patient should retain care while their condition needs it. A tired
worker should get useful rest; a pawn exposed to dangerous temperatures should
have suitable clothing or protection. When the necessary service is unavailable,
the need should stay open and identify the obstacle. Later availability should
allow useful work to resume.

**Intent:** Documented in [medical care](../developers/contracts/medical-care.md),
[mood](../developers/contracts/mood-control.md) and
[equipment upkeep](../developers/contracts/equipment-upkeep.md).
**Implementation:** Unassessed across this outcome. The medical family exposes
care, reserves and surgery needs. Recovery, rest and equipment outcomes require
further inspection. Evidence W1–W2 covers the medical subset.

## Evidence and inspection coverage

- **W1 — Urgent care path.** [MedicalRounds](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsMedicalFamily.cs)
  reads emergency patients and worker facts, routes tending/rescue through the
  critical patient's need and admits work with claims. Its ordinary pass also
  routes hospital, medicine and surgery work. [NativeTendOperations](../../integrations/rimgovernor-native/src/Bridge/Protocol/NativeTendOperations.cs)
  checks the doctor and patient, obtains a legal tending job and verifies that
  the ordered job started. Patient improvement needs a later observation.
- **W2 — Pairing and missing facts.** [MedicalFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/MedicalFamilyTests.cs)
  checks per-patient needs, exclusive doctor/rescuer pairing, busy-worker
  exclusion and missing-health exceptions. Care-policy and work eligibility
  conditions are explicit in these tests. They use supplied observations.
- **W3 — Native eligibility.** Registered OnDemand
  [medical/tend-gates](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Medical/TendGates.cs)
  checks control eligibility and pairwise reachability on native pawn reads.
  It requires at least two reach-checked colonists. It performs no treatment.

Dependent patients follow the population and medical contracts. Patient feeding,
babies, special diets and animal food connect to [food](food-security.md).
Disease progression, surgery outcomes, mental-break recovery, clothing manufacture
and recreation effects remain unassessed. See
[T7](review-queue.md#t7--finish-shelter-and-essential-care).
