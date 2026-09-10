# Medical care contracts

[Documentation](../README.md) · [Controller contracts](controller-contracts.md)

`CriticalMedical` selects native-approved doctor/patient pairs by bleeding deadline,
native life-threatening state and stable identity. It preserves `NoCare`, player
work overrides and self-tend policy. Completed treatments permit a later tend in the
same goal episode; persisted and archived method evidence prevents duplicate work.
The current doctor's exact job target distinguishes treatment from tending somebody
else. A receipt does not establish completed treatment.

`MaintainMedicalCare` retains native visible condition identities, severity, immunity,
retend timing, care policy and medical-rest state. It enables ordinary Patient and
PatientBedRest work when available and not disabled by a player override. Native jobs choose
beds. Missing observations, unavailable work or player restrictions produce explicit
blockers. Chronic conditions remain visible without automatically choosing elective
operations. An absent tracked patient cannot certify recovery. These observations
are current facts, not disease-prognosis estimates.

Confirmed interrupted tending may resume within the existing recovery bound. An
unavailable doctor can be replaced through a newly validated shared plan step;
the original action and receipt remain retained. The current load, native tick,
player direction and native ordered-job generations must match. Unknown write
results and changed native player orders do not authorize retries.

## Surgery

`home/medical_operations` discovers current patient recipes, body-part indices,
native ingredient definitions and counts, practitioner skill requirements, and
current operation bills. Its catalog is patient-specific. Available ingredients
and doctors do not certify bed access, sufficient reachable medicine or eventual
success; normal native work selection still checks those conditions.

`RequestSurgery` requires an explicit player choice of the inspected patient,
recipe and part. It creates a `PLAYER` action executed by Hands. Runtime dispatch
requires its recorded semantic intent, matching arguments, current direction/load
and unchanged health identity and care policy. No autonomous elective-surgery method
exists. The native operation rechecks eligibility in its main-thread transaction
and queues an ordinary medical bill. Existing patient bills are preserved and prevent
adding competing work. Patient policies, beds and native medicine restrictions remain
in force. Recipes requiring extra dialogs, faction violations or unsupported health
effects remain inspection-only.

Completion requires the expected newly added condition on the exact body part or
observed removal of the targeted condition. Bill removal, delivery receipts and elapsed
ticks are insufficient. Failure, cancellation, suspension, death and missing health
remain explicit; failed operations are never automatically repeated. Surgical health
changes and postoperative recovery are separate outcomes.

The native setup used by [medical acceptance](../how-to/medical-care-acceptance.md)
is excluded from production builds and from the model execution surface.

The supervised clock accepts short-lived surgical recovery IDs only from confirmed,
current-direction patient operations in the current colony/load/map. Native sweeps
permit downing only while the identified patient is alive, anesthetized, in a bed,
not bleeding, not dangerously ill and above half health. Injury and death guards
remain active. The allowance expires at the configured work window; it is not a
general exemption for downed patients. Changed context invalidates surgical
completion tracking, and stalled operations expose a bounded no-progress failure.
