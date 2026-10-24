# Assigned capability maintenance

[Contract map](README.md) · [Skill mechanics](skill-learning.md) · [Pawn activities](pawn-activities.md)

Colony defense is every capable pawn's (#3813). Every pawn the one practice eligibility rule
admits (`SkillLearning.PracticeCapable`, native `RangeTraining.CanTrain`: work tags allow the
skill) may practise Shooting or Melee, whatever role it holds, and the gain does not stop at a
role floor; only the native ceilings bound it. An active, actually held Ideology role still
establishes its native `RoleRequirement_MinSkillAny` requirements, each preserving every OR
branch; they feed the `maintenance_xp` tiebreak below. The role's skill requirement itself is
not modelled: each assignment is the finding `ideology_role_skill_requirement_not_modelled:<id>`.
Unfilled assignments are acquisition. No target is saved.

Normal coupled generation proposes bounded range sessions for each admitted pawn and exact
two-pawn sparring sessions for each admitted pair. A session competes with other work on value
(the skill XP its native callbacks apply, `ForecastMaintenancePolicy.SessionXp`, at the tier-2 price of the
pawn's Melee or Shooting stock: the raid harm its next level saves, #3852) like any other activity. A stock tier 2 leaves
at 0 (no raid to expect, as on a fresh colony) earns the session nothing, so it is not scheduled. The sparring ring's and the training range's room
credits price the facility by the harm its trainees' Melee or Shooting saves (#3813, #3846) ([forecast value](../architecture/forecast-value.md)).
The common schedule charges both participants' travel, waiting and work, holds exact
stands/dummies/markers, and compares the same needs and mood trajectory as other work.
Observed hostiles, critical patients or exhausted food/rest postpone practice; unknown
hostile/critical-care evidence cannot admit it. Native drafting and existing safeguards
interrupt installed work.

For each actual requirement, `ForecastMaintenance` projects native decay and callbacks from
supported scheduled normal work once. Conditional practice callbacks join that same stream.
The comparator breaks ties by the native XP still needed to meet a required branch, after
urgent care, physiological and mood consequences. Missing OR-branch
mechanics are unknown unless another branch is known to satisfy the requirement. The
projection ends before the earliest native reset for the skill, at most one day.

Native observations quote practice verb cycles on detached, unequipped objects using native
methods. They neither swap live equipment nor change skills. Those are conditional successful
cycles: misses, movement, repairs, injuries and interrupted work can yield less learning.
Sparring's native gathering delay consumes time before its callbacks and recreation. Its joy
rate uses the current native tolerance; future tolerance is unassessed. A completed spar gives
its `RimGovernor_TrainedWith` memory (`PracticeWorker.Thoughts`, #3858), so a pawn on a spar keeps its mood
forecast. A range drill gives it when another colonist is on a session at a stand or marker within chat
range as it finishes (`PracticeWorker.Company`, #3871); company outside sessions is
`practice_company_outside_sessions_not_modelled`. Injury probability and recovery duration remain unassessed. Accounted schedule labor
excludes unassessed injury recovery; it is not a zero-risk estimate. Existing pain, bleeding,
exchange, ceiling and daily-budget stops are unchanged.

The existing `PawnActivities` Work step carries `PawnActivityPractice`: skill, exact partner
and partner marker, and bounded duration (#3813 removed the role, requirement and beneficiary).
Domain and native validation require reciprocal paired tasks. Hands installs the exact roster when its selected Work starts
in the existing transient bout registry. Controlled native WorkGivers no longer choose a skill
or initiate a roster. Native drivers still own attacks, learning, practice gear and restoration.
Manual/uncontrolled scanning retains its previous behavior. No new action kind, scheduler,
reservation store or saved goal is introduced.

A session continues while its step stays legal (an unforbidden target); a role's loss no longer
releases it. A bounded session's success is not evidence that a skill floor was restored.
Actual skill readback owns that fact.

`coupled_selection` consequence vectors include `maintenance` rows with requirement identity,
conditional `missing_xp` and findings. Existing OTLP timeline readers display them. Prepared
`production/maintenance-range` and `production/maintenance-sparring` require normal Method
journal attribution, observed recovery and draft/real-weapon restoration. They complement the native budget, ceiling, damage and bystander laboratory cases.
Registration and compilation are distinct from executed native acceptance.

## No practice surgery (#3344)

Doctors are not trained by surgery on prisoners. Practice surgery (#3637), practice slots by amputation
(#3639) and the peg-leg control and reinstall cycle were deleted: one practice surgery nets about 1e-4
capability-days (its Medicine XP credit less the medicine it uses), while tending wounds without
medicine builds the same skill for free. Medicine XP still comes from ordinary tending and surgery. Tier 2 has no
practice node: the value of an XP is that of one Medicine level more over the XP it takes
([skill learning](skill-learning.md#skill-progress-is-capability)).
