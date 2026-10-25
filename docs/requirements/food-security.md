# Keep the colony fed

[Requirements collection](README.md) · [Open decisions and recommended checks](#what-to-do-next)

The governor is responsible for feeding the colony. The player should not have to
set a food goal, designate each berry bush, choose a recipe or tell a hungry
colonist to eat. Food work begins with the colony's condition and the options the
game actually offers.

This is the replacement requirements pilot, following the user's direction on
2026-10-24 that the bot has full autonomy and player-set research goals are to be
dropped. It examines revision `06db87fb4deb9df2626e1dfbc7059e0655379463`.
The detailed example is an unarmed colonist obtaining and eating wild berries;
cooking and recovery are supporting examples. **Code and test assertions were
inspected; no game run or current passing result was examined.**

## Requirements

### RG-FOOD-001 — Feed colonists without waiting for player instructions

**Statement:** While Auto is on, the governor should notice hunger and arrange
food acquisition, preparation and eating as needed, without a player-created goal.
Success means colonists actually eat; selecting work or issuing an order is not
enough.

**Purpose:** Keep colonists alive and able to work without requiring the player
to manage each meal.

**Conditions:** The colony is under automatic control, the relevant food and work
features are enabled, and there is a usable way to obtain food. This pilot covers
ordinary mobile colonists and ordinary edible food. It does not establish feeding
babies, downed patients or every special diet. Full autonomy assigns responsibility
to the governor; it does not make food available in every possible game situation.

**Acceptance examples:**

- An unarmed colonist with no food but reachable ripe berry bushes obtains berries
  and eats them, without receiving a player order after the starting conditions.
- When edible food is already available, arranging future production does not
  count as feeding a hungry colonist; eating must still occur.
- If no supported source is usable, the governor must not report the colony fed
  merely because it selected or designated work. It should expose the obstacle.

**Intent status:** Human-approved for autonomous responsibility, from the user's
2026-10-24 instruction that the bot has full autonomy. The specific feeding and
completion obligations are documented intent in the [player guide](../players/README.md),
[controls](../players/controls.md#planned-work-and-needs) and
[pawn activity contract](../developers/contracts/pawn-activities.md#needs-forecast-support-matrix).
This does not mark every acceptance example as human-approved.

**Implementation status:** Supported for the inspected ordinary food path. The
code connects observed hunger and food sources to work and eating assignments.
How clearly the player sees each unsupported situation is unassessed.

**Evidence and limits:** F1–F3 below trace the code. F5 is a registered game test
that checks berry designation and recorded consumption. That is a stronger claim
than an accepted order, but it has not been run here. It does not prove sustained
feeding, recovery from malnutrition, or every stage's assignment history.

**Open work:** Start with the existing berry test (T1). Reconcile the conflicting
descriptions of who chooses food work in Q1 before claiming that its current
assertions prove a particular scheduling mechanism.

### RG-FOOD-002 — Replace food before the colony runs out

**Statement:** The governor should compare food on hand with expected consumption
and arrange replacement food early enough to be useful. It should account for
how long food takes to obtain, the labor it needs and whether it will spoil before
use. Planned food is not food already delivered.

**Purpose:** Avoid a colony that eats its last meal while waiting for an immature
crop or a food source it cannot actually use.

**Conditions:** The governor can read food stocks, consumers and the relevant
production conditions. Numerical reserves remain in the owning policy and
contracts; this requirement introduces no new number of days to keep in stock.

**Acceptance examples:**

- When stored food will run out before a crop is ready, consider a source that can
  deliver sooner rather than counting the crop as today's supply.
- On an unarmed start, consider usable wild plants instead of treating hunting as
  the only way to obtain food.
- After stocks fall or production is disrupted, reassess the shortfall. Missing
  information must not be interpreted as sufficient food.
- Food merely designated for harvest or promised by a plan is not reported as
  observed delivery or consumption.

**Intent status:** Documented intent in the [supply model](../developers/architecture/supply-model.md#demands)
and [food observation contract](../developers/contracts/controller-contracts.md#observation).

**Implementation status:** Partial assessment. Supply planning accounts for
consumption, arrival time and labor; food-work planning also considers spoilage.
The shortfall checks reopen when coverage drops. This pilot has not followed
farming, fishing, trade, hunting or refrigeration through to sustained replacement
of the food the colony consumes.

**Evidence and limits:** F1/F2 and F4 cover calculations and selected decisions.
The forage unit test starts with a plan that already chose berries, so it does not
prove the full choice between farming, hunting and foraging. None of these inspected
assertions establishes survival over a winter or after repeated disruptions.

**Open work:** T3 checks the important timing decision if existing policy tests do
not already cover it. Long-term sufficiency belongs to campaign evidence (T5),
not a longer version of the berry test. No implementation bug is established by
this incomplete inspection.

### RG-FOOD-003 — Turn available ingredients into useful food

**Statement:** When cooking is a suitable way to feed the colony, the governor
should arrange the necessary preparation and cooking. A cooking order succeeds
only when the game produces the food; feeding succeeds only when it is eaten.

**Purpose:** Avoid treating raw ingredients, a stove or a queued recipe as a meal
that has already fed someone.

**Conditions:** A supported recipe, usable equipment, eligible cook and suitable
ingredients are available, or can be provided. The governor chooses these details;
they are not instructions the player must supply. Raw food may be the appropriate
choice when it can meet the need sooner.

**Acceptance examples:**

- With a usable stove and ingredients, the chosen cooking task consumes the
  selected quantities and produces the expected food.
- If an ingredient disappears or the stove becomes unusable, report failure and
  reassess; do not count a meal that was never made.
- A missing kitchen makes cooking capacity needed again. Merely requesting a new
  stove does not establish that it was built or that anyone was fed.

**Intent status:** Documented intent in [food work](../developers/contracts/pawn-activities.md#live-one-iteration-cooking)
and [cooking evidence](../developers/contracts/pawn-activities.md#observation-and-tests).

**Implementation status:** Supported for exact recipe execution and the inspected
planning connections. Fully autonomous kitchen construction and recovery after a
lost stove are unassessed. No inspected case proves the complete automatic
sequence from choosing to cook through a colonist eating the resulting meal.

**Evidence and limits:** F2/F3 connect selected cooking to ordinary game work. F6
checks exact ingredient consumption and food creation, including two failures.
Its test fixture selects each attempt, so it does not prove that the governor
chooses the recipe or recovers without intervention. It also does not prove eating.

**Open work:** T2 is the most useful additional game claim after surveying existing
cases: automatic recovery that ends with a meal eaten. Do not add another test
that only checks a recipe order was accepted.

### RG-FOOD-004 — Reassess interrupted work without inventing food

**Statement:** When food work fails or conditions change, the governor should read
what actually happened before continuing or choosing another option. It must not
count an unfinished harvest, failed cooking attempt or interrupted meal as food
successfully obtained or eaten.

**Purpose:** Prevent shortages from being hidden by optimistic progress reports,
repeated consumption of the same assumed ingredients, or plans that never adapt.

**Conditions:** Automatic control remains active and fresh observations are
available. This pilot assesses task continuation, not crash recovery or game
save/reload behavior.

**Acceptance examples:**

- After harvesting or cooking, use the output actually observed in the game for
  the next step, rather than assuming the original predicted stack exists.
- If cooking fails, stop that attempt and reassess its remaining inputs; do not
  blindly repeat its consumption step.
- If the colonist no longer needs the planned meal, reconsider future work.
- If a required observation is missing, expose that limitation and do not declare
  the shortfall resolved.

**Intent status:** Documented intent in [food continuation](../developers/contracts/pawn-activities.md#live-one-iteration-cooking)
and the [food progress contract](../developers/contracts/controller-contracts.md#priority-evaluation).

**Implementation status:** Supported for the inspected continuation decisions and
missing-information checks. Automatic recovery through all runtime steps remains
unassessed; stopping a failed attempt alone is not a completed recovery.

**Evidence and limits:** F4's tests use supplied observations to check that changed
ingredients are re-read, actual meal output is required and failed work is released.
F6 demonstrates game execution failures, but the test directs the recovery. Neither
is proof that an unassisted colony chooses a replacement and gets fed afterward.

**Open work:** T2 should establish that missing connection before a broad recovery
claim. T4 is a cheaper boundary check for truthful reporting after missing data.

## From hunger to food actually eaten

This trace follows decisions and effects, not just a list of classes.

1. **Notice the need.** The regular colony review reads stocks, consumers and
   available sources. `ReviewFoodPlan` calculates supply and possible ways to
   replace it. `FoodRules` assesses food supply and cooking capacity; no player
   research or food goal is required (F1).
2. **Choose useful work.** Supply planning can include forage. The acquisition
   planner keeps plants selected by that plan even when no hunter is armed.
   Separately, the shared work planner compares concrete harvest, cooking,
   delivery and eating options, including food readiness and spoilage (F2).
3. **Issue the selected work.** `Rounder.AdmitCoupledSelection` records the chosen
   assignments and their resource commitments. The existing executor, Hands,
   dispatches them. Game code checks the target again and creates ordinary
   harvest, recipe or eating jobs (F3). A designation or assignment receipt is
   still not a meal.
4. **Observe and continue.** Continuation reads actual outputs before advancing
   to cooking or delivery. The game records completed consumption; the berry
   test waits for `RawBerries` recorded as `food_eaten` (F3–F5). The test's
   observation can lag eating until the current recorded hour closes.

This trace is based on source inspection. The exact division between designation
and explicit work selection in the berry scenario needs the documentation review
in Q1; the test does not trace every assignment. It is not a witnessed game run.

## Evidence and inspection coverage

**F1 — Assess food needs.** Examined selected parts of
[Food.cs](../../brain/src/RimGovernor.Brain.Policy/Food.cs),
[FoodForecast.cs](../../brain/src/RimGovernor.Brain.Policy/FoodForecast.cs),
[FoodPlan.cs](../../brain/src/RimGovernor.Brain.Policy/FoodPlan.cs),
[RoundsFoodPlan.cs](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsFoodPlan.cs)
(`ReviewFoodPlan`) and
[FoodRounds](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsFood.cs)
(`ReadAsync`, `WorkAsync`). These connect colony observations to food requests and
possible supply. All alternative sources and their eventual output were not audited.

**F2 — Choose concrete food work.** Examined
[RoundsAcquisition](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsAcquisition.cs)
(`FoodPlanAcquisition`, `GoalSelection`),
[FoodMethodCandidates.GenerateCoupled](../../brain/src/RimGovernor.Brain.Policy/Methods/Generation/CoupledFoodMethodCandidates.cs)
and its caller in
[ForecastCoupledCandidates](../../brain/src/RimGovernor.Brain.Policy/ForecastCoupledCandidates.cs).
The food candidates include harvest, recipe conversion and eating with known food
readiness. Options with unread shelf life are omitted with a finding. This is code
for possible work, not evidence that a particular option wins or gets completed.

**F3 — Execute and observe.** The shared assignment/dispatch path was sampled in
[RoundsCoupled](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsCoupled.cs),
[Worker](../../brain/src/RimGovernor.Brain.BuildingRuntime/Worker.cs) and
[NativePawnActivities](../../integrations/rimgovernor-native/src/Bridge/NativePawnActivities.cs).
The Eat branch checks availability and starts `JobDefOf.Ingest` with the selected
count. [ConsumptionHooks](../../integrations/rimgovernor-native/src/Bridge/ConsumptionHooks.cs)
records ingested counts, and
[NativeConsumptionTool](../../integrations/rimgovernor-native/src/Bridge/Protocol/NativeConsumptionTool.cs)
returns recorded hours. Executor faults, all recipe internals and save/reload were
not fully audited.

**F4 — Inspected unit and boundary assertions; not executed here.**

- [FoodForageTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodForageTests.cs):
  `WithNoArmedHunterTheFoodNeedSelectsTheForageRow` preserves a selected forage
  source when there are no hunt slots. It begins with a constructed food plan.
- [FoodFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodFamilyTests.cs):
  `TheSupplyNeedIsAPlainThresholdOverItsChecks` checks that shortages reopen the
  need and unread checks cannot establish sufficiency.
  `TheCookingNeedIsMetWhileAMealCanBeMadeAndReopensWhenTheKitchenIsLost` checks
  the decision to restore cooking capacity, not construction or meals.
- [FoodRunwayReachTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodRunwayReachTests.cs):
  `AnAbsentFoodPlanOrRunwayRaisesAtTheReaderThatNeedsIt` checks explicit failure
  for missing planning information, not the player-visible explanation.
- [FoodCookingContinuationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FoodCookingContinuationTests.cs):
  `SupplyRebindsChangedIngredientThenCookingRequiresObservedMealForDelivery`,
  `InterruptedCookingLostBenchAndChangedRecipeCannotReplayConsumption` and
  `FreshDemandCanRetireProductiveWork` check continuation against supplied
  observations. They do not prove physical output or automatic recovery in game.

**F5 — Existing game test: `food/forage-unarmed`.**
[FoodForageUnarmed](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Food/FoodForageUnarmed.cs)
is registered through
[FoodCases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Food/FoodCases.cs) and
[CaseCatalog](../../brain/src/RimGovernor.Brain.Acceptance.Cases/CaseCatalog.cs),
as OnDemand. It stages one unarmed colonist, no edible stock and six mature berry
bushes; enables acquisition/work and keeps hunger active. It asserts at least one
staged bush was designated and berries were eaten within its existing tick budget.
The body does not prove improved malnutrition or repeated food replacement.
[#3668](https://github.com/davidarcher/rimgovernor/issues/3668) reports landing
`07f32fe50`; the inspected comment supplies no result bundle or explicit native
pass. Registered does not mean executed or passed.

**F6 — Existing game test: `control/pawn-activity-cooking`.**
[ControlCooking](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Control/ControlCooking.cs)
is registered in CaseCatalog as OnDemand. It stages a stove, competing bills and
selected/unselected ingredients. Two test-directed attempts lose an ingredient or
usable bench and must fail without producing meals or consuming the remaining
inputs. A third must make exactly the expected output while leaving the other
bill and unselected stack alone. It does not run automatic recovery or assert
consumption of the meal. [#3398](https://github.com/davidarcher/rimgovernor/issues/3398)
records porting work; the comments examined do not establish a current pass.

**Additional sampled case:**
[FoodStarvingRecovery](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Food/FoodStarvingRecovery.cs)
is registered Nightly as `food/starving-tribal-recovery`. It asserts crafted ranged
weapon, wild kill, chained hunt job, butchery and meat eaten on a prepared tribal
start. It does not assert recovery from malnutrition despite its name. Its wider
chain makes it a less focused first check than F5. No result was examined.

**Not yet examined:** all food-source alternatives, seasonal farming, animal feed,
babies/patients, dietary restrictions, nutritional edge cases, storage/freezer
construction, spoilage prevention in game, trading for food, detailed hunting
safety, visible blocker reporting, save/reload and sustained colony results.
The collection's broader capability map is still a proposal, not an exhaustive audit.

## What to do next

### Q1 — Reconcile descriptions of who chooses food work

The [controller contract](../developers/contracts/controller-contracts.md#priority-evaluation)
says vanilla work priorities schedule routine food work. The
[pawn activity contract](../developers/contracts/pawn-activities.md#needs-forecast-support-matrix)
says the governor chooses controlled eating and supported work explicitly.
The current code contains the latter path; the berry test and #3668 still describe
an older designation-plus-vanilla approach. Its assertions show outcomes, not who
selected each job.

The desired product outcome is autonomous feeding. Which code chooses a job is an
implementation question; the inspection has not established a current failure.
[#4000](https://github.com/davidarcher/rimgovernor/issues/4000) tracks tracing the
remaining paths and correcting the documentation. Duplicate searches found no
matching open issue. Resolve this before using the old wording to design new tests.

### Ranked implementation and documentation work

1. **Clarify the execution description (Q1, conflicting documentation).** It affects
   interpretation of food tests and where a real feeding failure should be fixed.
   Trace the current path and correct the contract; do not change gameplay just
   to match an old description.
2. **Investigate any failure of the focused outcome test (missing evidence first).**
   Food starvation has high severity, but no failing current run was observed.
   Run or find the evidence before proposing an implementation fix. File a bug
   only for a concrete failure with its inputs and observed result.
3. **Retire player-set research goals separately (product direction).**
   [#3999](https://github.com/davidarcher/rimgovernor/issues/3999) records the user's
   decision. Preserve autonomous research. No code deletion is part of this pilot,
   and other goals/preferences have not automatically been retired.

### Ranked evidence investments

**T1 — Eating from an unarmed start (RG-FOOD-001/002).** Reuse F5 first: one colonist,
ripe berries, no food or weapon. Pass requires actual recorded berry consumption
and designation of a staged source. A game test is necessary because policy tests
cannot prove harvesting or ingestion. Inspect retained results for the same code
and setup before running; preserve revision, environment and `result.json`.

**T2 — Automatic recovery through a meal eaten (RG-FOOD-003/004).** First search
existing cooking/coordination cases for this exact claim. If none covers it, stage
one hungry colonist, a supported cooking option and an alternative usable option;
remove the selected input after assignment as the one controlled disruption. Let
the governor recover without a test-selected replacement. Pass requires a new
usable choice, observed food output where cooking is chosen, and eating; fail if
it only reports a replacement order. Use a short native acceptance case because
both real game effects and the unassisted connection are the uncertainty. F6's
explicit fixture revisions do not establish this claim. Stage near completion
where appropriate; do not inflate a budget to hide a stall.

**T3 — Food arrives before the shortage (RG-FOOD-002).** Inspect existing supply
policy tests first. If missing, use a pure decision test with food that runs out
before a crop matures and one usable earlier source within known labor capacity.
Pass requires a plan that addresses the earlier shortage without crediting the
late crop as current food; a second setup with no timely source must retain the
gap. This isolates the timing decision cheaply. F4's forage test starts after the
food plan has already selected the source.

**T4 — Missing information and subsequent recovery (RG-FOOD-004).** If existing
boundary tests do not cover it, supply an unavailable food observation to a review,
then a valid observation showing a shortage. Check that the first review does not
publish adequate food and that the second resumes planning with the real shortfall.
Use a boundary test, not a game run: F4 proves an exception, not the published
status or next review. No response-time target is implied.

**T5 — Continued replacement, storage and resilience (RG-FOOD-001/002).** Campaign
evidence is needed for continuing to feed a colony across seasons and disruptions.
Agree the starting colony and exact acceptable food/health outcomes before a run,
with no post-setup interventions. Short prepared cases cannot establish this.
Coordinate with [#2351](https://github.com/davidarcher/rimgovernor/issues/2351),
previously recorded as parked; this pilot does not authorize a campaign or invent
a survival duration. Survey storage and other sources before treating them as covered.

These are proposed investments, not new tests written or executed. Follow the
[test-selection guide](../developers/testing/choose-tests.md) and
[acceptance charter](../developers/testing/acceptance-guide.md#charter). Missing
coverage alone is not a bug and has not been filed as one.
