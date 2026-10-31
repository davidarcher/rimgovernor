# Keep the colony fed

[Collection](README.md) · [Standing food specification](../../openspec/specs/food-security/spec.md) · [Review queue](review-queue.md)

## Intent and evidence ownership

Routine cooking execution now follows the [native cooking chain milestone](colony-labor.md#native-cooking-chain-milestone): ordinary bills and work settings,
with conditional recipe forecasts kept separate from dispatch. Its revised
productive-cooking cases have no new gameplay result here; historical exact-Method
results below do not establish that migration's physical outcome. Confirmed food
requirements are unchanged.

The [native-led labor change](../../openspec/changes/native-led-labor-execution/proposal.md) supersedes the unlanded baby change's advance caregiver selection/release. Checkpoint `ff5cfc77b` and later worktree edits are implementation evidence only: the executed feeding cases below now establish their scoped outcomes; #4040 remains open until the verified implementation lands. Earlier bottle fixtures never obtained their unrelated owned-research precondition; those results do not establish a feeding failure. The new change owns the remaining proof.

The first deterministic live-journal bottle run (`.rimgovernor/baby-native-bottle/child/coordinated-bottle-feeding/result.json`, 2026-10-26, dirty `ff5cfc77b`, source tree `5eb1ca56eb5d6536cb79c307d0e0b433d2eee633c28c1199bebe21bf68f16900`) observed the competing Method, native takeover, actual intake, no malnutrition and care-time dispatch rejection. It failed the complete case: research occupied the first 4,000 ticks and feeding was still running at the fixed 8,000-tick limit. A safety job observed before feeding is not proof of post-feeding return. The fixture now stages a small harvest and requires return after feeding, without increasing the budget; its executed results follow.

The revised cases both passed on 2026-10-26 with the private native package at dirty
`ff5cfc77b`, source tree `f8b7bc3f1f433ae51a7c934204ff9350eb3268d579aa27363e0aedf2ff054b0d`.
The fixture admits one harvest Method to the live journal after an enabled work
review; normal worker/Hands dispatch starts it. Hunger is staged once after that
ownership is observed. No post-setup harness release or feeding order is issued.

| Native claim | Result and local provenance | Observed outcome |
| --- | --- | --- |
| `child/coordinated-bottle-feeding` | PASS, 24.94 s; `.rimgovernor/baby-native-harvest-bottle/child/coordinated-bottle-feeding/result.json` | Native bottle feeding after harvest; food 0.902, no malnutrition, post-feeding return complete; stale dispatch refused during feeding and return. |
| `child/coordinated-nursing` | PASS, 23.751 s; `.rimgovernor/baby-native-nursing-meals/child/coordinated-nursing/result.json` | Native nursing with usable lactation; food 0.988, no malnutrition, post-feeding return complete; stale dispatch refused during feeding and return. |

The first nursing attempt was stopped at tick zero: its fixture omitted adult
food, causing the ordinary review's `meal_service_unavailable` blocker before
admission. The successful revision adds adult meals and retains native nursing.
Both passing cases use the original 8,000-tick budget. They establish these scoped
feeding lifecycles, not all RG-FOOD obligations or the general labor migration.
An unrelated layout outcome logging exception from the earlier bottle attempt is
tracked as [#4041](https://github.com/davidarcher/rimgovernor/issues/4041).

Reconciliation retains canonical hunger/current-care facts, usable nursing and provisioning assessment, active-care admission, dependent-pawn model corrections, the held-layout-plan handoff and non-harvest research-catalog fixes. Later edits also retain the nonempty baby-social array codec regression and no-dispatch selection diagnostics. Superseded work is the advance `MakeRoomForBabyCare` / `ReleaseForBabyCare` path, greedy caregiver allocation/withholding and research-goal test setup; removal is tracked in the labor change. Historical tests and results below retain their original scope and provenance. Confirmed RG-FOOD requirement text is unchanged.

The [standing specification](../../openspec/specs/food-security/spec.md) owns
RG-FOOD-001–013. Its policy was confirmed by the maintainer in this chat on
2026-10-25; the [decision record](../../openspec/changes/redefine-food-requirements/proposal.md#decision-provenance-and-review-boundary)
records the direction and authorization to replace the recovered baseline.
RG-FOOD-005/007/008 are no longer inferred candidates. The detailed scenarios
formalize the agreed direction and do not constitute new gameplay evidence.

This page owns historical implementation and test evidence. The support labels
below describe the narrower paths previously inspected, not a fresh audit or
certification of the expanded requirements. No new implementation gaps or test
passes are inferred from this requirements rewrite. The new obligations need
claim-by-claim implementation assessment before anyone can report them supported.

The initial trace follows an unarmed colonist obtaining and eating wild berries,
with cooking and recovery as supporting examples. Its code revision was
`06db87fb4deb9df2626e1dfbc7059e0655379463`, on 2026-10-24.
Growing, preservation, hunting choices and special feeding were sampled at the
[collection revisions](README.md). F1–F14 retains that inspection unchanged.
A native pawn eating berries does not by itself establish a useful autonomous
food-management contribution; evidence must be interpreted against the new claim.

## Historical evidence by requirement

### RG-FOOD-001 — Feed colonists autonomously

**Requirement and scenarios:** [Standing specification](../../openspec/specs/food-security/spec.md#requirement-rg-food-001--feed-all-colony-dependents).

**Historical implementation assessment:** Supported for the inspected ordinary food path. Hunger
and food observations lead to work and eating assignments. The
[explanations page](explanations.md) traces blockers to the player view.

**Evidence:** F1–F3 trace the code; F5 checks berry designation and consumption.
Its isolated setup supplies no sustained-feeding or malnutrition-recovery result.

**Open work:** [T1](review-queue.md#t1--feeding-from-an-unarmed-start) checks the
central outcome. [Q1](review-queue.md#q1--who-chooses-food-work) addresses conflicting
execution descriptions.

### RG-FOOD-002 — Replace food before the colony runs out

**Requirement and scenarios:** [Standing specification](../../openspec/specs/food-security/spec.md#requirement-rg-food-002--replace-food-before-the-colony-runs-out).

**Historical implementation assessment:** Supported for inspected replacement decisions. The
calculations account for consumption, arrival time, labor and spoilage, and reopen
shortfalls as coverage drops. Farming, fishing, trade, hunting and refrigeration
have distinct outcome limits recorded in F7–F13 and the supply page. Sustained
sufficiency has no current executed evidence here.

**Evidence:** F1/F2 and F4 cover calculations and selected decisions. The forage
unit test starts with berries already selected in a constructed food plan. Winter
survival and repeated disruptions require broader evidence.

**Open work:** [T3](review-queue.md#t3--food-arrives-before-the-shortage) targets the
timing decision; [T5](review-queue.md#t5--continued-food-replacement) covers sustained
sufficiency.

### RG-FOOD-003 — Turn available ingredients into useful food

**Requirement and scenarios:** [Standing specification](../../openspec/specs/food-security/spec.md#requirement-rg-food-003--turn-available-ingredients-into-useful-food).

**Historical implementation assessment:** Supported for exact recipe execution and the inspected
planning connections, including cooking-facility selection (F14). Recovery after
losing a stove has no current native outcome evidence.

**Evidence:** F2/F3 connect selected cooking to game work. Cooking runs through
native bills and work settings; F6's exact recipe case was retired with the exact
recipe path. Autonomous recovery and eating need additional evidence.

**Open work:** [T2](review-queue.md#t2--recover-and-get-fed) targets recovery through
to a meal eaten.

### RG-FOOD-004 — Reassess interrupted food work

**Requirement and scenarios:** [Standing specification](../../openspec/specs/food-security/spec.md#requirement-rg-food-004--reassess-interrupted-food-work).

**Historical implementation assessment:** Supported for the inspected continuation decisions and
missing-information checks. Autonomous recovery through a later meal still needs
native outcome evidence.

**Evidence:** F4 supplies observations to check changed ingredients, actual meal
output and release of failed work. The connection from failure to an autonomously chosen
replacement remains the main evidence gap.

**Open work:** [T2](review-queue.md#t2--recover-and-get-fed) checks that connection;
[T4](review-queue.md#t4--recover-from-missing-information) checks reporting after
missing data.

### RG-FOOD-005 — Grow food for the coming shortage

**Requirement and scenarios:** [Standing specification](../../openspec/specs/food-security/spec.md#requirement-rg-food-005--grow-food-for-the-coming-shortage).

**Historical implementation assessment:** Supported for seasonal targets, field planning, exact sowing
and harvest-to-eating work. Sowing and harvesting have separate native cases;
a complete growing season is an evidence gap. Evidence F7/F11; proposed T3/T5.

### RG-FOOD-006 — Preserve food that the colony will need

**Requirement and scenarios:** [Standing specification](../../openspec/specs/food-security/spec.md#requirement-rg-food-006--preserve-food-that-the-colony-will-need).

**Historical implementation assessment:** Supported for storage demand, cooler construction and measured
chilling. Long-term loss prevention and recovery from power loss need execution
evidence. Evidence F8/F12; proposed T5/T12.

### RG-FOOD-007 — Account for the cost and danger of hunting

**Requirement and scenarios:** [Standing specification](../../openspec/specs/food-security/spec.md#requirement-rg-food-007--account-for-the-cost-and-danger-of-hunting).

**Historical implementation assessment:** Supported for inspected alternative selection. Native hunting
safety and completion across changing conditions need broader evidence. Evidence F9
and the additional tribal case below.

### RG-FOOD-008 — Feed people who need suitable food or help

**Requirement and scenarios:** [Standing specification](../../openspec/specs/food-security/spec.md#requirement-rg-food-008--feed-people-who-need-suitable-food-or-help).

**Historical implementation assessment:** Partial: dietary eligibility and baby-food
production demand are supported. Assisted feeding relies on native work; the food
assessment does not certify that a dependent was fed. Evidence F10/F13;
proposed T12 and [care](colonist-wellbeing.md).

**Scoped baby audit — [#4040](https://github.com/davidarcher/rimgovernor/issues/4040).**
The original count-only assessment could hide a hungry baby, and an installed
governor activity suppressed vanilla job selection even with Childcare enabled.
The implementation now reads hunger and care from the canonical needs census,
keeps hungry babies unmet, withholds current caregiver capacity in shared
admission and revises competing Methods through journal/Hands release. Vanilla
chooses and executes feeding and safe return. Carried babies remain consumers.

[BabyFeedingResponseTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/BabyFeedingResponseTests.cs)
covers usable/depleted nursing, unsuitable or unavailable care, finite caregiver
capacity, interruption, delayed bottle ingestion and protection after intake.
[ActivityContinuationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ActivityContinuationTests.cs)
exercises caregiver/dependent withdrawal through the actual Method journal,
exact revisions, stale-owner refusal and unchanged unrelated participants.

One additional execution guard addresses a concrete race in the old ownership
boundary: every unowned colonist was preemptible, so a previously prepared work
order could interrupt newly started feeding or its safe return before another
review could react. Admission now rechecks actual native care occupation. This
does not bind food stacks, predict chairs or add a milk/food reservation model.

The first local bottle case (`.rimgovernor/baby-bottle-run`, 2026-10-26 UTC)
stopped at tick 0: `PawnCycle.Split` demanded a joy need from the baby and faulted
every review before competing work could start. No feeding claim passed in that
run. A later run (`.rimgovernor/baby-bottle-current`) reached the review but held
tick 0 because the baby had no independent meal service. The correction keeps
babies out of the labor-cycle model and retains their nutrition in the existing
other-consumer demand path. `ColonyModelInputTests` verifies valid input, unchanged
adult labor and retained baby food demand, without inventing joy or a meal service.
The case explicitly stages one superpawn caregiver rather than the default crew.
The next run (`.rimgovernor/baby-bottle-dependent`) exposed the same assumption
in pawn valuation: it requested free working hours for the dependent baby and
disabled review authorization before the clock could advance. `PawnValueTests`
now exercises that review with a baby present; valuation excludes dependent
babies while retaining the existing missing-hours error for actual workers.
The valuation retry (`.rimgovernor/baby-bottle-valuation`) advanced 8,083 ticks
but established no competing research Method. Its replan also read an absent
layout from review facts despite receiving the held plan. `TombTests` reproduces
that handoff; burial demand now uses its supplied plan. The fixture seeds an
explicit research goal through the normal goal owner so the contention
precondition does not depend on optional research winning selection.
That goal-driven retry (`.rimgovernor/baby-bottle-goal`) exposed a catalog reader
demanding harvested products from buildings; it was stopped after diagnosis,
without a feeding claim. `WornCountGoalTests` now includes a researched building
with no harvest. Goal research prerequisites are derived only from definitions
with an observed harvested product.

Native lactation has a positive activation threshold: a little charge with
future refill can be observed while nursing cannot start. `ReadyToFeed` reads
native `CanActivate` separately from charge/refill; the depleted-nursing regression
rejects that false cover and selects a usable bottle alternative. Future nursery
exposure bounds retain their original charge/refill observations.

| Native case | Claim | Execution evidence |
| --- | --- | --- |
| `child/coordinated-bottle-feeding` | Normal governor research first owns the caregiver; native bottle-feeding then raises food before malnutrition and completes safe return, with reassignment refused during both phases. | Registered OnDemand; local verification in progress. |
| `child/coordinated-nursing` | The same contention/release lifecycle with usable native lactation and actual nursing intake. | Registered OnDemand; not yet executed. |

Both [cases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Child/ChildFeedingResponse.cs)
stage hunger once behind observed governor research, assign neither feeding nor
release, and stop on the one feeding lifecycle. Setup enables Childcare but
disables vanilla Research priority: native research without a governor Method
does not establish the contention precondition. Expected wall time is about one
minute each, under two with startup. Code-gate result on the implementation:
PASS (2m10s), 4,062 domain tests and 30 workflow tests, native builds and contract
probes; contract orphan checking found none.
Native execution is required before #4040 closes. These checks establish neither
sustained colony feeding nor the remaining patient, prisoner and animal portions
of RG-FOOD-008. Historical run 40193999645's first-child failure remains historical
evidence, without an inferred cause or attribution to this gap.

### Native-led food execution (labor change task 5.1)

Ordinary harvest, cooking, delivery and eating are native work. The shared
comparison still models them, and the food plan, restrictions, reserve and
intake reads are unchanged, but a modeled meal, cooking or harvest alone
dispatches nothing and creates no food crisis
(`NativeBaselineComparisonTests.AModeledMealAloneIsNeitherACrisisNorADispatch`,
`NativeFoodAssessmentTests.ModeledNativeFoodProductionAloneForcesNoDispatch`).
A pawn's own meal is ordered only when its food is observed unanswered at zero
(`InterventionHandsTests.AnUnansweredHungerReachesHandsAsAJustifiedEatFromTheGenerator`).
Native harvest or cooking is ordered only under a food risk native settings
answer too late
(`InterventionHandsTests.ACredibleFoodRiskReachesHandsAsAJustifiedHarvestFromTheGenerator`).
Both cases run through Hands with a bounded purpose.

| Native case | Claim | Execution evidence |
| --- | --- | --- |
| `food/ordinary-cooking` | The served bill, cooking and work families create a Target-mode (FoodTarget) cooking bill and leave Cooking enabled. Native pawns cook from them without any exact order, and the colony stock gains usable meals. | PASS, 33.4 s (wall 33284 ms), 2026-10-31 (task 6.1 rerun); meals 0 → 4; `.rimgovernor/task61-final-food-ordinary-cooking/food/ordinary-cooking/result.json` (fixture build of dirty `3e719c20a`, source tree `f0c717fa…422d`, save `0cbccd31…c618`, fixture `60eca9a3c6e03991`). Earlier: PASS, 38.0 s, `.rimgovernor/ordinary-cooking-0224/` (build from `27f8c15c` plus the 5.1/5.2 edits, save `5c4cf06d…`). |
| `coordination/food-delegated` | A credible food risk (no stock, native recovery known too late) with one ripe wild potato no native setting harvests: the served comparison selects the harvest (`food_risk_supply` stage), the gate justifies it as `CredibleRisk`, and it reaches Hands as one exact Harvest with its `FoodRisk` purpose; the native game harvests it and the participant's ownership ends. | PASS, 17.8 s (wall 17754 ms), 2026-10-31; tick 398, physical food 13, finished `Work:Harvest`, purposes `[FoodRisk]`, active 0, participant released; `.rimgovernor/task61-final-coordination-food-delegated/coordination/food-delegated/result.json` (same build and save). |
| `coordination/food-carried-recovery` | The same justified harvest, its job ended once: the failed step ends its attempt, no later revision reissues it, and the participant's ownership ends. | PASS, 17.2 s (wall 17141 ms), 2026-10-31; tick 61, interrupted, not recovered, purposes `[FoodRisk]`, active 0, participant released; `.rimgovernor/task61-final-coordination-food-carried-recovery/coordination/food-carried-recovery/result.json` (same build and save). |

The domain tests above and these short executed cases are the evidence; full
provenance and the remaining limits are in [labor task 6.1](colony-labor.md#task-61-executed-native-results).
The staged harvest cases prove one preventive intervention's path and its
release, not that a starving colony recovers. They do not establish sustained
colony feeding or any RG-FOOD outcome in game.

## Additional confirmed requirements awaiting implementation assessment

The following entries have confirmed intent in the same decision record. Existing
F1–F14 observations may inform a later assessment, but they do not establish full
support or execution of these claims. No new coverage audit was performed here.

| Requirement | Standing requirement | Implementation assessment |
| --- | --- | --- |
| RG-FOOD-009 | [Eating times and portions](../../openspec/specs/food-security/spec.md#requirement-rg-food-009--choose-eating-times-and-portions-for-useful-intake) | Unassessed against the confirmed requirement. |
| RG-FOOD-010 | [Restrictions and emergency exceptions](../../openspec/specs/food-security/spec.md#requirement-rg-food-010--respect-restrictions-and-weigh-emergency-exceptions) | Unassessed against the confirmed requirement. |
| RG-FOOD-011 | [Scarcity priorities](../../openspec/specs/food-security/spec.md#requirement-rg-food-011--prioritize-dependents-under-unavoidable-scarcity) | Unassessed against the confirmed requirement. |
| RG-FOOD-012 | [World travel](../../openspec/specs/food-security/spec.md#requirement-rg-food-012--feed-dependents-during-world-travel) | Unassessed against the confirmed requirement. |
| RG-FOOD-013 | [Dining conditions](../../openspec/specs/food-security/spec.md#requirement-rg-food-013--provide-worthwhile-dining-conditions) | Unassessed against the confirmed requirement. |

## From hunger to food eaten

1. **Assess the colony.** The regular review reads stocks, consumers and available
   sources. `ReviewFoodPlan` calculates supply and ways to replace it. `FoodRules`
   assesses supply and cooking capacity (F1).
2. **Choose useful work.** The acquisition planner keeps forage sources selected
   by the supply plan even when no hunter is armed. The shared work planner
   compares harvest, cooking, delivery and eating options, including food readiness
   and spoilage (F2).
3. **Issue assignments.** `ColonyReview.AdmitCoupledSelection` records selected work and
   its resource commitments. Hands dispatches the assignments. Game code checks
   the targets again and creates ordinary harvest, recipe or eating jobs (F3).
4. **Observe and continue.** Continuation reads actual output before advancing.
   The berry test waits for `RawBerries` recorded as `food_eaten` (F3–F5). The
   consumption record becomes available when the current recorded hour closes.

Q1 in the review queue covers the remaining uncertainty about designation and
explicit assignment in the berry scenario.

## Evidence and inspection coverage

Evidence status: source and assertions inspected; case registration checked in
source. Tests were not executed during this work. The historical issue comments
examined below contain no current result bundle.

**F1 — Assess food needs.** Examined selected parts of
[Food.cs](../../brain/src/RimGovernor.Brain.Policy/Food.cs),
[FoodForecast.cs](../../brain/src/RimGovernor.Brain.Policy/FoodForecast.cs),
[FoodPlan.cs](../../brain/src/RimGovernor.Brain.Policy/FoodPlan.cs),
[FoodPlan.cs](../../brain/src/RimGovernor.Brain.Runtime/Review/ColonyReview.FoodPlan.cs)
(`ReviewFoodPlan`) and
[FoodReview](../../brain/src/RimGovernor.Brain.Runtime/Review/Food.cs)
(`ReadAsync`, `WorkAsync`). These connect colony observations to food requests and
possible supply. Coverage is limited to the functions named here.

**F2 — Choose concrete food work.** Examined
[Acquisition](../../brain/src/RimGovernor.Brain.Runtime/Review/AcquisitionPlanner.cs)
(`FoodPlanAcquisition`, `GoalSelection`),
[FoodMethodCandidates.GenerateCoupled](../../brain/src/RimGovernor.Brain.Policy/Methods/Generation/CoupledFoodMethodCandidates.cs)
and its caller in
[ForecastCoupledCandidates](../../brain/src/RimGovernor.Brain.Policy/ForecastCoupledCandidates.cs).
The food candidates include harvest, recipe conversion and eating with known food
readiness. Options with unread shelf life are omitted with a finding. Selection
and completion require further evidence.

**F3 — Execute and observe.** The shared assignment/dispatch path was sampled in
[Coupled](../../brain/src/RimGovernor.Brain.Runtime/Review/ColonyReview.Coupled.cs),
[Hands advancement](../../brain/src/RimGovernor.Brain.Executor/ActionAdvancement.cs) and
[NativePawnActivities](../../integrations/rimgovernor-native/src/Bridge/NativePawnActivities.cs).
The Eat branch checks availability and starts `JobDefOf.Ingest` with the selected
count. [ConsumptionHooks](../../integrations/rimgovernor-native/src/Bridge/ConsumptionHooks.cs)
records ingested counts, and
[NativeConsumptionTool](../../integrations/rimgovernor-native/src/Bridge/Protocol/NativeConsumptionTool.cs)
returns recorded hours. Detailed inspection of executor faults, recipe internals
and save/reload remains pending.

**F4 — Unit and boundary assertions.**

- [FoodForageTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodForageTests.cs):
  `WithNoArmedHunterTheFoodNeedSelectsTheForageRow` preserves a selected forage
  source when there are no hunt slots. It begins with a constructed food plan.
- [FoodFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodFamilyTests.cs):
  `TheSupplyNeedIsAPlainThresholdOverItsChecks` checks that shortages reopen the
  need and unread checks cannot establish sufficiency.
  `TheCookingNeedIsMetWhileAMealCanBeMadeAndReopensWhenTheKitchenIsLost` checks
  the decision to restore cooking capacity, using supplied facts.
- [FoodRunwayReachTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodRunwayReachTests.cs):
  `AnAbsentFoodPlanOrRunwayRaisesAtTheReaderThatNeedsIt` checks explicit failure
  for missing planning information.
- [FoodCookingContinuationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodCookingContinuationTests.cs):
  `AnEndedIngredientDeliveryReturnsToNativeWithoutCreditingMeals` and
  `FreshDemandCanRetireProductiveWork` check that a re-admitted ingredient
  delivery returns to native work against supplied observations; the bill it
  feeds runs through native bills and work settings (task 5.3 of
  `native-led-labor-execution`). Automatic recovery in game needs separate
  execution evidence.

**F5 — Existing game test: `food/forage-unarmed`.**
[FoodForageUnarmed](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Food/FoodForageUnarmed.cs)
is registered through
[FoodCases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Food/FoodCases.cs) and
[CaseCatalog](../../brain/src/RimGovernor.Brain.Acceptance.Cases/CaseCatalog.cs),
as OnDemand. It stages one unarmed colonist, no edible stock and six mature berry
bushes; enables acquisition/work and keeps hunger active. It asserts at least one
staged bush was designated and berries were eaten within its existing tick budget.
Malnutrition recovery and repeated food replacement are outside its assertions.
[#3668](https://github.com/davidarcher/rimgovernor/issues/3668) reports landing
`07f32fe50`.

**F6 — Retired game test: `control/pawn-activity-cooking`.**
The case directed exact recipe attempts with selected ingredients. Bill work no
longer runs as an exact order (task 5.3 of `native-led-labor-execution`), so the
case and its fixture were deleted with the exact recipe path. Native cooking is
observed by `production/development-lifecycle` and the productive cooking cases;
a stalled bill re-admits only its ingredient delivery
(`InterventionHandsTests.AStalledBillWithIngredientsPendingReachesHandsAsAJustifiedIngredientDelivery`).

**Additional sampled case:**
[FoodStarvingRecovery](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Food/FoodStarvingRecovery.cs)
is registered Nightly as `food/starving-tribal-recovery`. It asserts crafted ranged
weapon, wild kill, chained hunt job, butchery and meat eaten on a prepared tribal
start. Its wider chain makes it a less focused first check than F5.

**F7 — Seasons and fields.**
[SeasonalFoodThresholdTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/SeasonalFoodThresholdTests.cs)
checks that reserve targets increase toward winter and stay continuous across the
last growing day. [FieldPlanner](../../brain/src/RimGovernor.Brain.Runtime/Review/FieldPlanner.cs)
reads crops and current colony facts, verifies control and review identity, and
routes food fields under the food supply need. It reports a missing layout anchor.
These samples cover target policy and work preparation; no harvest result was examined.

**F8 — Storage and refrigeration.**
`FoodReserveStateTests` checks precautionary release from accessible runway and
conditional recovery leads, including a native-committed late crop and scoped
uncertainty. `FoodStoresFamilyTests` takes that release through current-review
admission, journal and Hands when stock protection is inactive. Its boundary
acknowledges access only; it does not prove production or consumption in game.
[FoodStoresFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodStoresFamilyTests.cs)
checks exposure and temperature thresholds, pending reserve access, missing facts
and conflicting stack claims. [FoodStoresReview](../../brain/src/RimGovernor.Brain.Runtime/Review/FoodStoresReview.FoodStores.cs)
reads storage and cooling rooms, proposes reserve changes and routes refrigeration
building work. Registered OnDemand
[lab/food-storage-routes](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Lab/FoodStorageRoutes.cs)
checks that native food-path options exist and path-search counts stay bounded
as stockpile size changes. It performs no preservation work.

**F9 — Hunting alternatives.**
[HuntAlternativesTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HuntAlternativesTests.cs)
checks eligible rosters, harm-before-yield ordering, absence of surplus reward and
refusal of missing estimates/routes. The estimates are supplied by the test.
Their accuracy and the native response to a failed hunt need separate evidence.

**F10 — Special food and source observations.**
[ReviewBabyFeeding](../../brain/src/RimGovernor.Brain.Runtime/Review/ColonyReview.BabyFeeding.cs)
feeds the active Biotech baby census into
[BabyFeedingRules](../../brain/src/RimGovernor.Brain.Policy/BabyFeeding.cs).
The rule counts stock with baby eater eligibility, derives a target from demand
and rejects missing consumer rows. Current usable nursing contributes provision;
capability counts no longer exempt every baby. Each observed hungry baby remains
unmet until its hunger is relieved. See the scoped #4040 audit below for verification.
Registered OnDemand
[tools/foodchannels](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Tools/ToolsFoodChannels.cs)
checks berry sources, absence of Odyssey fishing water in a core-only setup and
a cow's native milk-fullness row. It checks observations, with feeding and milk
collection outside the assertions.

**F11 — Planting and repeated meals.**
[SowingProviderTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/SowingProviderTests.cs)
checks eligible workers, exact cells, work time and invalid mechanics.
[NativeSowingActivities](../../integrations/rimgovernor-native/src/Bridge/NativeSowingActivities.cs)
observes the legal per-cell sowing for the what-if comparison; sowing, harvest and
ingestion remain native, and no exact order sows (task 5.6 deleted the exact Sow
primitive and its `production/sow-block` case). Registered OnDemand
[production/coupled-needs](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionCoupledNeeds.cs)
runs the governor against ripe rice and now requires actual usable harvest output
and unowned native ingestion, sleep and recreation, followed by another native
meal. The fixture injects rested state and a fresh shortage between cycles.
This revised native-job assertion is registered and compiled; earlier exact-Method
results do not verify it. Crop maturation and seasonal survival are separate claims.

**F12 — Cooler construction through chilling.**
[Refrigeration](../../brain/src/RimGovernor.Brain.Runtime/Review/BuildingPlanner.Refrigeration.cs)
reads rooms and food temperature, selects construction or a setpoint change and
reports enclosure, power and heat-rejection blockers. Destroyed equipment can
create new work. Registered Nightly
[refrigeration/build](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Refrigeration/RefrigerationBuild.cs)
requires one built cooler, correct hot/cold sides, the target setting and meat
temperature below the policy exit threshold. It supplies the powered room and
materials. Food losses over time are outside its assertions.

**F13 — Diets and dependent consumers.**
[FoodSupplyFacts.Read](../../integrations/rimgovernor-native/src/Bridge/FoodSupplyFacts.cs)
retains every consumer's demand and credits stocks using native diet, restriction,
reach and area checks. Downed consumers receive no independent-access credit.
Held food belongs to its holder.
[PreferencesFactsTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PreferencesFactsTests.cs)
checks vegetarian, insect-meat and human-meat choices and forbidden methods.
[BabyFoodDeclarer](../../brain/src/RimGovernor.Brain.Runtime/Review/Food.cs)
passes shortage to shared production; BabyFeedingRules selects baby-edible recipes.
[WorkAssignment](../../brain/src/RimGovernor.Brain.Policy/WorkAssignment.cs) pins
Childcare. FoodFamilyTests and BabyFeedingResponseTests distinguish provisioning
from hunger, active care and native safe return. Registered native feeding cases
below measure one actual bout per feeding mode when executed.

**Other sources.** [FoodChannelPolicyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodChannelPolicyTests.cs)
checks fishing capacity, regeneration and seasonal gaps; F10 checks forage and
milk observations. [AnimalFeedRunway](../../brain/src/RimGovernor.Brain.Policy/AnimalFeedRunway.cs)
projects pasture, edible stock and known herd growth. AnimalsReader uses that
shortfall; resource planning provisions feed. [Commitments](colony-commitments.md)
owns herd decisions and [supply](development-and-supply.md) owns trade execution.

**Proof limits after inspection:** current native results, completed assisted
feeding, food-loss measurements, hunting-estimate accuracy and seasonal autonomy.
The [review queue](review-queue.md) distinguishes those evidence investments from
confirmed behavior gaps.

**F14 — Cooking facilities and acquisition designations.**
[BuildingPlanner](../../brain/src/RimGovernor.Brain.Runtime/Review/BuildingPlanner.Selection.cs)
requires food-plan support before selecting cooking capacity, recognizes usable
or pending facilities, and can select a campfire when capacity is missing.
[Sleeping](../../brain/src/RimGovernor.Brain.Runtime/BuildingPlanner.cs)
connects facility work to planned placement and existing construction claims.
The general room/construction cases establish prepared building effects; they
do not prove a zero-stock kitchen bootstrap (tracked in #3641, supply S6).
[AcquisitionPlanner](../../brain/src/RimGovernor.Brain.Runtime/Review/AcquisitionPlanner.cs)
selects food sources and commits acquisition actions;
[NativeAcquire](../../integrations/rimgovernor-native/src/Bridge/Protocol/NativePlantAcquisition.cs)
maps plant acquisition to native harvest designations. This is the reachable
designation path behind F5. Exact work assignments are another supported path;
[Q1](review-queue.md#q1--who-chooses-food-work) records the conflicting descriptions.
