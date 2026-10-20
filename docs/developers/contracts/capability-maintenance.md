# Assigned capability maintenance

[Contract map](README.md) · [Skill mechanics](skill-learning.md) · [Pawn activities](pawn-activities.md)

An active, actually held Ideology role establishes its native `RoleRequirement_MinSkillAny`
requirements. Each requirement preserves every OR branch, including noncombat skills;
separate requirements remain AND. Unfilled assignments are acquisition. Defense improvement
without an assigned requirement is unsupported here. Current level, historical maxima and
unlocked weapon ceilings never establish a target. No target is saved.

Normal coupled generation proposes bounded range sessions and exact two-pawn sparring
sessions. A partner can support a holder without having a maintenance requirement of their
own. The common schedule charges both participants' travel, waiting and work, holds exact
stands/dummies/markers, and compares the same needs and mood trajectory as other work.
Observed hostiles, critical patients or exhausted food/rest postpone practice; unknown
hostile/critical-care evidence cannot admit it. Native drafting and existing safeguards
interrupt installed work.

For each actual requirement, `ForecastMaintenance` projects native decay and callbacks from
supported scheduled normal work once. Conditional practice callbacks join that same stream.
The comparator minimizes the native XP still needed to meet a required branch, after urgent
care, physiological and mood consequences. XP above the floor has no value. A practice group
must improve an assigned shortfall relative to the same portfolio without its callbacks;
sufficient ordinary learning therefore suppresses redundant practice. Missing OR-branch
mechanics are unknown unless another branch is known to satisfy the requirement. The
projection ends before the earliest native reset for the skill, at most one day.

Native observations quote practice verb cycles on detached, unequipped objects using native
methods. They neither swap live equipment nor change skills. Those are conditional successful
cycles: misses, movement, repairs, injuries and interrupted work can yield less learning.
Sparring's native gathering delay consumes time before its callbacks and recreation. Its joy
rate uses the current native tolerance; future tolerance is unassessed. Company memories,
injury probability and recovery duration remain explicit unknowns. Accounted schedule labor
excludes unassessed injury recovery; it is not a zero-risk estimate. Existing pain, bleeding,
exchange, ceiling and daily-budget stops are unchanged.

The existing `PawnActivities` Work step carries `PawnActivityPractice`: skill, actual role,
requirement index and beneficiary, exact partner and partner marker, and bounded duration.
Domain and native validation require reciprocal paired tasks. Hands installs the exact roster when its selected Work starts
in the existing transient bout registry. Controlled native WorkGivers no longer choose a skill
or initiate a roster. Native drivers still own attacks, learning, practice gear and restoration.
Manual/uncontrolled scanning retains its previous behavior. No new action kind, scheduler,
reservation store or saved goal is introduced.

Fresh continuation reads the beneficiary's actual assigned requirement and native mechanics.
Loss/reassignment or known satisfaction releases the coordinated attempt for ordinary
reconsideration; unknown evidence does not certify satisfaction. A bounded session's success
is not evidence that a skill floor was restored. Actual skill readback owns that fact.

`coupled_selection` consequence vectors include `maintenance` rows with requirement identity,
conditional `missing_xp` and findings. Existing OTLP timeline readers display them. Prepared
`production/maintenance-range` and `production/maintenance-sparring` require normal Method
journal attribution, observed recovery, draft/real-weapon restoration, and release after role
removal. They complement the native budget, ceiling, damage and bystander laboratory cases.
Registration and compilation are distinct from executed native acceptance.

## Practice surgery (#3637)

A doctor can practice Medicine on a held prisoner through a practice-cycle step: installing a
material part on a missing limb, or removing an installed material part (`RecipeFacts.PracticeCycleOf`,
the same slot `RoundsColonyModel.PracticeOf` models). `PracticeMethodCandidates` proposes one candidate
per doctor × prisoner × step × medicine kind the prisoner's care allows. Each carries the native
Medicine callback (`ProductiveSkillWork` at each skill level), the walk and fetch, the work duration
and its medicine consumption as a task input. The gates are legality only
(`PracticeSurgeryRules.LegalSurgeries`): the admission practice uses; the player not forbidding organ
harvest; no prisoner surgery queued or in flight (`OrganHarvestRules.SurgeryInFlight`, so RoundsSurgery's
harvest and recovery go first and the two never queue on one prisoner); the ingredients on the map and no
violation; and the doctor's native failure chance with that medicine at most `SurgeryRules.RestoreFailureCap`.
Whether the practice is worth the doctor's time and the medicine is the Investment term's alone: the
Medicine XP credit (`skill/<pawn>/Medicine`) against the medicine charge (`medicine/<kind>`). A
selection whose practice surgeries net no credit is refused (`practice_without_value` evidence), so with zero
marginals practice never wins. The part itself is a one-time cost and is not charged; its fetch is not in
the approach (`practice_surgery_part_fetch_unassessed`). One practice surgery runs at a time. Binding and
release are described in [pawn activities](pawn-activities.md); `medical/practice-surgery-binding` checks the
applied step's doctor-restricted bill and its native Medicine callback. Three served cases check the
selection (#3638), each on a Medicine-3 doctor and a prisoner missing a leg: `medical/practice-surgery-demand`
(a raid threat in a calibrated exposure cell selects practice and the native callback pays),
`medical/practice-surgery-no-demand` (a quiet world refuses it) and `medical/practice-surgery-cheaper-learning`
(a patient needing tending is selected and practice is not). Practice surgery is the only doctor-training path.

## Practice slots (#3639)

A held prisoner with no practice-cycle step can be given one: removing a natural limb, or installing a
material part over it (`RecipeFacts.PracticeSlotOf`), is a practice-surgery step on the same Surgery variant.
The native read offers these only for a prisoner with no cycle step, on the first present natural part of
each part def a stocked artificial-part install applies to. Beyond the practice-surgery gates the step is
legal only when it is not lethal, the part is not one a prisoner keeps (`RecipeFacts.KeptPart`), the
install's learning is read, and the population policy is not recruiting the prisoner
(`PopulationRules.Recruiting`, the Recruit/Convert intent the Recruit need and `PrisonerRecruitDeficit`
drive; unknown holds it with `practice_slot_recruiting_unknown`). A goodwill violation is allowed.
Once one limb is gone the prisoner has a slot and cycles it, so a second limb is never taken; feeding a
legless prisoner is the warden's work in the forecast, not a rule here.

The step is valued by the slot's tier-2 counterfactual: `ColonyModelInput.Openings` lists one slot per
material install recipe a surgery could open, and `ProjectMarginalValue` prices `practice_slot/<recipe>`
(`ProjectGraph.SlotKey`) as the run with that slot's practice node less the run without, once per review;
a value that leaves a skill or medicine stock unpriced names `practice_slot_unpriced:<recipe>` instead.
The task carries that key as project progress at the native success chance per work tick, so a completed
step credits the chance times the slot's worth: a failed surgery opens nothing, and the prisoner a fatal
failure loses is worth only what `recruit_option_value_unassessed`, `prisoner_goodwill_unassessed` and
`prisoner_sale_value_unassessed` leave out. The colonists' mood is priced through the mood forecast only:
the native read lists, per row that removes a clean natural part, the unattached memories the native
harvest events (HarvestedOrganFromGuest, and HarvestedOrgan with Ideology) give each spawned free colonist
through its ideoligion's self-took and knows precepts, after their own conditions and `CanGetThought`
(none for a psychopath or a precept that approves); the commitment grants them as mood memories.
`medical/practice-amputation` checks a limb taken and a cycle step on that part following.
