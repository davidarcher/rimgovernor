# Keep the colony fed

[Collection](README.md) · [Standing food specification](../../openspec/specs/food-security/spec.md) · [Review queue](review-queue.md)

## Intent and evidence ownership

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

**Evidence:** F2/F3 connect selected cooking to game work. F6 checks ingredient
consumption, food creation and two failures. The test directs each attempt and
ends at food production. Autonomous recovery and eating need additional evidence.

**Open work:** [T2](review-queue.md#t2--recover-and-get-fed) targets recovery through
to a meal eaten.

### RG-FOOD-004 — Reassess interrupted food work

**Requirement and scenarios:** [Standing specification](../../openspec/specs/food-security/spec.md#requirement-rg-food-004--reassess-interrupted-food-work).

**Historical implementation assessment:** Supported for the inspected continuation decisions and
missing-information checks. Autonomous recovery through a later meal still needs
native outcome evidence.

**Evidence:** F4 supplies observations to check changed ingredients, actual meal
output and release of failed work. F6 exercises failures in the game with
test-directed recovery. The connection from failure to an autonomously chosen
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
  `SupplyRebindsChangedIngredientThenCookingRequiresObservedMealForDelivery`,
  `InterruptedCookingLostBenchAndChangedRecipeCannotReplayConsumption` and
  `FreshDemandCanRetireProductiveWork` check continuation against supplied
  observations. Automatic recovery in game needs separate execution evidence.

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

**F6 — Existing game test: `control/pawn-activity-cooking`.**
[ControlCooking](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Control/ControlCooking.cs)
is registered in CaseCatalog as OnDemand. It stages a stove, competing bills and
selected/unselected ingredients. Two test-directed attempts lose an ingredient or
usable bench and must fail without producing meals or consuming the remaining
inputs. A third must make exactly the expected output while leaving the other
bill and unselected stack alone. Recovery is directed by the test; eating is
outside its assertions. [#3398](https://github.com/davidarcher/rimgovernor/issues/3398)
records the porting work.

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
and rejects missing consumer rows. Its model treats a present breastfeeder as
covering breastfeeding. F13 traces provisioning and native eligibility; the model
does not demonstrate timely caregiver availability or completed feeding.
Registered OnDemand
[tools/foodchannels](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Tools/ToolsFoodChannels.cs)
checks berry sources, absence of Odyssey fishing water in a core-only setup and
a cow's native milk-fullness row. It checks observations, with feeding and milk
collection outside the assertions.

**F11 — Planting and repeated meals.**
[SowingProviderTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/SowingProviderTests.cs)
checks eligible workers, exact cells, work time and invalid mechanics.
[NativeSowingActivities](../../integrations/rimgovernor-native/src/Bridge/NativeSowingActivities.cs)
provides legal per-cell work to NativePawnActivities, which repeats sowing and
dispatches harvest and ingestion. Registered OnDemand
[production/sow-block](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionSowBlock.cs)
requires exactly four planted cells and completed execution for a fixture-installed
order. Registered OnDemand
[production/coupled-needs](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionCoupledNeeds.cs)
runs the governor against ripe rice and requires output binding, eating and sleep,
then a new method and another meal. The fixture injects rested state and a fresh
shortage between cycles. Crop maturation and seasonal survival are separate claims.

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
Childcare. FoodFamilyTests checks the provisioning need and breastfeeder assumption.
These decisions stop short of measuring completed assisted feeding.

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
