# Keep the colony fed

[Collection](README.md) · [Review queue and recommended checks](review-queue.md)

The governor handles the colony's food supply: finding food, preparing it when
useful, feeding colonists and replacing what they consume. It makes these decisions
from the colony's condition and the options available in the game.

This pilot follows an unarmed colonist obtaining and eating wild berries, with
cooking and recovery as supporting examples. Code revision examined:
`06db87fb4deb9df2626e1dfbc7059e0655379463`, on 2026-10-24.

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

**Not yet examined:** all food-source alternatives, seasonal farming, animal feed,
babies/patients, dietary restrictions, nutritional edge cases, storage/freezer
construction, spoilage prevention in game, trading for food, detailed hunting
safety, visible blocker reporting, save/reload and sustained colony results.
