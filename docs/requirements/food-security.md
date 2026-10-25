# Keep the colony fed

[Collection](README.md) · [Review queue and recommended checks](review-queue.md)

The governor handles the colony's food supply: finding food, preparing it when
useful, feeding colonists and replacing what they consume. It makes these decisions
from the colony's condition and the options available in the game.

The initial trace follows an unarmed colonist obtaining and eating wild berries,
with cooking and recovery as supporting examples. Its code revision was:
`06db87fb4deb9df2626e1dfbc7059e0655379463`, on 2026-10-24.
Growing, preservation, hunting choices and special feeding were sampled at the
[expansion revision](README.md). Evidence F7–F10 records that added inspection.

## Requirements

### RG-FOOD-001 — Feed colonists autonomously

**Statement:** While Auto is on, the governor should notice hunger and arrange
food acquisition, preparation and eating as needed. Completion requires observed
consumption.

**Purpose:** Keep colonists alive and able to work while the governor manages meals.

**Conditions:** Automatic control and the food/work features are enabled. The
inspected path covers mobile colonists, ordinary edible food and usable food
sources. Special feeding situations are listed in the coverage inventory below.

**Acceptance examples:**

- An unarmed colonist with no food and reachable ripe berry bushes obtains berries
  and eats them autonomously.
- A hungry colonist with suitable food available eats while the governor also
  arranges future supplies.
- When every available source is unusable, report the obstacle and retain the
  food shortfall.

**Intent status:** Documented intent in the [player guide](../players/README.md),
[controls](../players/controls.md#planned-work-and-needs) and
[pawn activity contract](../developers/contracts/pawn-activities.md#needs-forecast-support-matrix).
The user explicitly confirmed full autonomy on 2026-10-24. The detailed acceptance
examples remain proposals for review.

**Implementation status:** Supported for the inspected ordinary food path. Hunger
and food observations lead to work and eating assignments. Player-visible
explanations of unsupported situations remain unassessed.

**Evidence:** F1–F3 trace the code; F5 checks berry designation and consumption.
Its isolated setup leaves sustained feeding and malnutrition recovery unassessed.

**Open work:** [T1](review-queue.md#t1--feeding-from-an-unarmed-start) checks the
central outcome. [Q1](review-queue.md#q1--who-chooses-food-work) addresses conflicting
execution descriptions.

### RG-FOOD-002 — Replace food before the colony runs out

**Statement:** The governor should compare food on hand with expected consumption
and arrange replacement food early enough to be useful. Planning should account
for delivery time, labor and spoilage, with separate records of expected and
observed supply.

**Purpose:** Keep the colony fed while crops mature and other sources take time
to deliver.

**Conditions:** Food stocks, consumers and relevant production conditions can be
read. Reserve quantities follow the owning policy and contracts.

**Acceptance examples:**

- If stored food will run out before a crop matures, arrange a source that can
  deliver sooner.
- An unarmed start with usable wild plants can forage.
- Falling stocks or disrupted production trigger a fresh assessment.
- Missing information leaves adequacy unresolved. Harvest designations remain
  expected supply until output is observed.

**Intent status:** Documented intent in the [supply model](../developers/architecture/supply-model.md#demands)
and [food observation contract](../developers/contracts/controller-contracts.md#observation).

**Implementation status:** Unassessed for sustained replacement. The inspected
calculations account for consumption, arrival time, labor and spoilage, and reopen
shortfalls as coverage drops. Farming, fishing, trade, hunting and refrigeration
still need to be followed through to their delivered output.

**Evidence:** F1/F2 and F4 cover calculations and selected decisions. The forage
unit test starts with berries already selected in a constructed food plan. Winter
survival and repeated disruptions require broader evidence.

**Open work:** [T3](review-queue.md#t3--food-arrives-before-the-shortage) targets the
timing decision; [T5](review-queue.md#t5--continued-food-replacement) covers sustained
sufficiency.

### RG-FOOD-003 — Turn available ingredients into useful food

**Statement:** When cooking is a suitable way to feed the colony, the governor
should arrange preparation and cooking through to food produced in the game.

**Purpose:** Make ingredients useful to hungry colonists.

**Conditions:** A supported recipe, usable equipment, eligible cook and suitable
ingredients are available or can be provided. The governor chooses these details
and can choose raw food when that better meets the need.

**Acceptance examples:**

- With a usable stove and ingredients, cooking consumes the chosen quantities
  and produces the expected food.
- A lost ingredient or unusable stove triggers failure reporting and reassessment
  of the remaining inputs.
- Losing a kitchen renews the need for cooking capacity. Recovery is complete
  when useful cooking becomes available again.

**Intent status:** Documented intent in [food work](../developers/contracts/pawn-activities.md#live-one-iteration-cooking)
and [cooking evidence](../developers/contracts/pawn-activities.md#observation-and-tests).

**Implementation status:** Supported for exact recipe execution and the inspected
planning connections. Autonomous kitchen construction and recovery after losing a
stove remain unassessed.

**Evidence:** F2/F3 connect selected cooking to game work. F6 checks ingredient
consumption, food creation and two failures. The test directs each attempt and
ends at food production. Autonomous recovery and eating need additional evidence.

**Open work:** [T2](review-queue.md#t2--recover-and-get-fed) targets recovery through
to a meal eaten.

### RG-FOOD-004 — Reassess interrupted food work

**Statement:** When food work fails or conditions change, the governor should read
the actual outcome, reassess the remaining need and continue with a suitable plan.
Progress records must distinguish work in progress, completed output and food eaten.

**Purpose:** Keep plans responsive and food accounting accurate during disruption.

**Conditions:** Automatic control remains active and fresh observations are available.
The inspected behavior covers task continuation within a running session.

**Acceptance examples:**

- After harvesting or cooking, the next step uses the output observed in the game.
- Failed cooking ends that attempt. Further work uses a fresh assessment of the
  remaining ingredients.
- A colonist whose hunger has been satisfied has future meal work reconsidered.
- A missing observation produces an explicit limitation and leaves the shortfall
  unresolved.

**Intent status:** Documented intent in [food continuation](../developers/contracts/pawn-activities.md#live-one-iteration-cooking)
and the [food progress contract](../developers/contracts/controller-contracts.md#priority-evaluation).

**Implementation status:** Supported for the inspected continuation decisions and
missing-information checks. Recovery across the complete running system remains
unassessed.

**Evidence:** F4 supplies observations to check changed ingredients, actual meal
output and release of failed work. F6 exercises failures in the game with
test-directed recovery. The connection from failure to an autonomously chosen
replacement remains the main evidence gap.

**Open work:** [T2](review-queue.md#t2--recover-and-get-fed) checks that connection;
[T4](review-queue.md#t4--recover-from-missing-information) checks reporting after
missing data.

### RG-FOOD-005 — Grow food for the coming shortage

The governor should size and revise food growing work using expected consumption,
seasonal availability and the time until harvest. The purpose is a dependable
replacement supply as current stock is eaten or spoils. Planting should leave the
immediate shortage visible until food can actually arrive.

For example, reserve targets should build before the growing season ends. A crop
that matures too late should leave demand for an earlier food source. Lost field
capacity or a changed colony population should prompt a new assessment. A missing
layout or unusable growing site should explain why field work cannot proceed.

**Intent:** Inferred candidate from the food/supply path and seasonal policy.
**Implementation:** Supported for sampled
seasonal target adjustment and field-work routing. Sowing-to-harvest completion,
crop failure and winter sufficiency remain unassessed. Evidence F7; proposed T3/T5.

### RG-FOOD-006 — Preserve food that the colony will need

The governor should arrange suitable storage and cooling for food at risk, while
keeping food accessible when it is needed. The purpose is to turn acquired food
into usable reserves. Actual storage and temperature should determine whether
preservation needs remain open.

Warm perishable stock above the policy threshold should create cooling work.
Food left exposed should create storage demand. If power fails or a storage route
becomes unusable, reassessment should retain the threatened supply and seek useful
work. A planned cooler alone should leave the temperature outcome pending.

**Intent:** Documented in the storage family and
[spatial contract](../developers/contracts/spatial-contracts.md).
**Implementation:** Supported for sampled storage/cooling need derivation and
work routing. Completed hauling, freezer construction and reduced spoilage in
game remain unassessed. Evidence F8; proposed T5/T12.

### RG-FOOD-007 — Account for the cost and danger of hunting

When hunting is considered, the governor should account for eligible hunters,
expected harm, usable yield and retrieval. The purpose is to obtain food without
discarding the risks and work required to bring it home. Missing estimates or
routes should prevent that option from being treated as ready.

An unarmed or busy pawn should be excluded from the inspected hunting choices.
Lower expected harm should take precedence over extra meat in those choices.
After a target or route changes, the governor should reassess. A successful kill
still leaves butchery, retrieval and eating to complete where required.

**Intent:** Inferred candidate from hunting policy and its tests; full autonomy
is approved, while these detailed tradeoffs have not received separate approval.
**Implementation:** Supported for inspected alternative selection. Native hunting
safety and completion across changing conditions remain unassessed. Evidence F9
and the additional tribal case below.

### RG-FOOD-008 — Feed people who need suitable food or help

Food adequacy should account for who can eat the available food and who needs
another pawn to feed them. The purpose is to avoid overlooking a hungry baby or
patient in a colony that has plenty of unsuitable or inaccessible food.

With babies and no breastfeeder, the colony should provide enough baby-edible
nutrition and complete feeding. A downed patient should receive usable food.
Changed diet eligibility or an unavailable caregiver should keep the affected
need visible and trigger reassessment when conditions improve.

**Intent:** Inferred candidate, with baby-food provisioning documented in the
inspected code. **Implementation:** Partial: baby-food stock assessment is present;
completed assisted feeding, individual dietary coverage and patient feeding remain
unassessed. Evidence F10; proposed T12 and [care](colonist-wellbeing.md).

## From hunger to food eaten

1. **Assess the colony.** The regular review reads stocks, consumers and available
   sources. `ReviewFoodPlan` calculates supply and ways to replace it. `FoodRules`
   assesses supply and cooking capacity (F1).
2. **Choose useful work.** The acquisition planner keeps forage sources selected
   by the supply plan even when no hunter is armed. The shared work planner
   compares harvest, cooking, delivery and eating options, including food readiness
   and spoilage (F2).
3. **Issue assignments.** `Rounder.AdmitCoupledSelection` records selected work and
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
[RoundsFoodPlan.cs](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsFoodPlan.cs)
(`ReviewFoodPlan`) and
[FoodRounds](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsFood.cs)
(`ReadAsync`, `WorkAsync`). These connect colony observations to food requests and
possible supply. Coverage is limited to the functions named here.

**F2 — Choose concrete food work.** Examined
[RoundsAcquisition](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsAcquisition.cs)
(`FoodPlanAcquisition`, `GoalSelection`),
[FoodMethodCandidates.GenerateCoupled](../../brain/src/RimGovernor.Brain.Policy/Methods/Generation/CoupledFoodMethodCandidates.cs)
and its caller in
[ForecastCoupledCandidates](../../brain/src/RimGovernor.Brain.Policy/ForecastCoupledCandidates.cs).
The food candidates include harvest, recipe conversion and eating with known food
readiness. Options with unread shelf life are omitted with a finding. Selection
and completion require further evidence.

**F3 — Execute and observe.** The shared assignment/dispatch path was sampled in
[RoundsCoupled](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsCoupled.cs),
[Worker](../../brain/src/RimGovernor.Brain.BuildingRuntime/Worker.cs) and
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
  observations. Automatic recovery in game remains unassessed.

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
last growing day. [RoundsFieldPlanner](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsField.cs)
reads crops and current colony facts, verifies control and review identity, and
routes food fields under the food supply need. It reports a missing layout anchor.
These samples cover target policy and work preparation; no harvest result was examined.

**F8 — Storage and refrigeration.**
[FoodStoresFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodStoresFamilyTests.cs)
checks exposure and temperature thresholds, pending reserve access, missing facts
and conflicting stack claims. [FoodStoresRounds](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsFoodStores.cs)
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
[ReviewBabyFeeding](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsBabyFeeding.cs)
feeds the active Biotech baby census into
[BabyFeedingRules](../../brain/src/RimGovernor.Brain.Policy/BabyFeeding.cs).
The rule counts stock with baby eater eligibility, derives a target from demand
and rejects missing consumer rows. Its model treats a present breastfeeder as
covering breastfeeding; caregiver availability over time was not traced.
Registered OnDemand
[tools/foodchannels](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Tools/ToolsFoodChannels.cs)
checks berry sources, absence of Odyssey fishing water in a core-only setup and
a cow's native milk-fullness row. It checks observations, with feeding and milk
collection outside the assertions.

**Remaining inspection:** sowing and harvest completion, individual diets and
assisted feeding, animal feed, freezer construction, spoilage prevention in game,
trading for food, hunting prediction accuracy, visible blockers, save/reload and
sustained colony results. See [the review queue](review-queue.md) for prioritized
evidence work. No gameplay defect is inferred solely from these unexamined paths.
