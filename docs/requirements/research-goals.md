# Achieve a player's research goal

[Collection and coverage](README.md) · [Questions and recommended work](review-queue.md)

The player should be able to request a research project, let the governor arrange
work toward it, and see whether it is finished or what is preventing progress.
This pilot describes those expectations and checks how far the code and tests
support them.

The inspection follows ordinary research at a bench, with a colony loaded, Auto
on, and goal tracking, research planning and work assignment enabled. The code
connects goal entry, project selection, researcher
assignments and progress reporting. **We have not run that whole sequence in the
game.** Research tiers, Anomaly study, building a laboratory from scratch, other
DLC combinations and long-term colony development still need examination.

## Requirements

### RG-RES-001 — Keep the player's research request and act on it

**Statement:** The player can add, edit and remove a research goal. Valid changes
appear in the goals window and tell the governor what research is wanted. An
invalid change leaves the previous goal intact and explains what went wrong.

**Purpose:** Let the player direct the colony without losing a request to an
invalid edit or mistaking a saved request for completed research.

**Conditions:** A colony is loaded, the brain is running and the goal names an
ordinary research project. Auto must be enabled for the governor to act on it.
Whether goals survive saving and reloading is a separate question.

**Acceptance examples:**

- Add an unfinished project: the window retains it, and the governor includes it
  when next deciding what research is needed.
- Submit an invalid edit: the previous goal remains, with a refusal reason.
- Remove a goal: it stops requesting that research. Another colony need may still
  justify the project, and removing the goal need not cancel research already begun.
- Turn Auto off: the guide says goals remain saved and visible but are not pursued.

**Intent status:** Documented intent in the [goals guide](../players/goals.md#add-a-goal)
and [goals contract](../developers/contracts/goals-contracts.md#needs).

**Implementation status:** Supported for editing, storing and passing the request
to research planning. The inspection did not establish behavior with Auto off or
after a game save is reloaded.

**Evidence and limits:** [E1 and E2](#evidence-ledger) follow those connections and
identify tests for valid edits, rejected edits and research requests. The tests
were read, not run. The game test inserts a goal directly into saved governor
state, so it does not test the window. No evidence here establishes that unsaved
edits survive a crash; see [save ownership](../developers/contracts/persistence-contracts.md).

**Open work:** T2 in the [recommended checks](review-queue.md#ranked-evidence-investments)
would check the connection between an edit and research planning. Auto-off and
save/reload behavior need their own investigation.

### RG-RES-002 — Carry research through to completion

**Statement:** When the player requests a research project, the governor should
work toward its prerequisites and assign researchers when the colony can spare
them. The goal is complete only when RimWorld reports the project finished.

**Purpose:** Deliver the requested unlock, rather than merely selecting a project
or issuing an order.

**Conditions:** The governor can read the project's requirements, the game allows
research to proceed, and a capable colonist can reach a usable bench. Research
competes with other colony work; this requirement promises no completion date.

**Acceptance examples:**

- With no project selected, a usable bench and Brewing nearly finished, Auto
  completes Brewing and reports the goal as Met.
- If a prerequisite is unfinished, research it first. The original goal stays unmet.
- If a bench is missing, request one or explain why it cannot be provided.
- If prerequisite information cannot be read, do not assume the unlock is available.
- If a project is selected but no colonist completes the work, keep the goal unmet.

**Intent status:** Documented intent in the [research completion contract](../developers/contracts/research.md#completion)
and [research goal definition](../developers/contracts/goals-contracts.md#needs).

**Implementation status:** Supported for the ordinary-project scenario described
above. The code also chooses prerequisites and requests benches, but this pilot
has not established that it can complete the entire chain or build a laboratory.
Whether it should replace a project already in progress remains an open question.

**Evidence and limits:** [E2–E5](#evidence-ledger) trace the code from request to game
orders. E7 describes an existing game test for completion; E8 checks research
progress when RimWorld's usual Research work priority is off. Both are registered,
but no current passing result was examined. E7 starts with Brewing 90% complete
and the bench already built. E8 checks progress, not completion. Neither establishes
reliable research across a full colony's competing needs. The game can also refuse
an assignment if conditions change after it was planned.

**Open work:** Run the existing focused tests before assuming research is broken
(T1). [Q2](review-queue.md#review-queue) and
[#3997](https://github.com/davidarcher/rimgovernor/issues/3997) track the conflicting
descriptions of who chooses research and whether an active project may be replaced.

### RG-RES-003 — Tell the player what has happened and what is blocked

**Statement:** Report Met only when the requested project is observed finished.
Otherwise explain whether it is being pursued, something is blocking it, or the
governor has no way to pursue it. Missing information must not be treated as
success. A predicted completion date must be presented as an estimate.

**Purpose:** Help the player understand stalled research without confusing a
selected project, missing observation or prediction with a completed unlock.

**Conditions:** The governor has reviewed the goal using current research
information. The window may show Pending between adding a goal and that review.
A completion estimate is available only when the prediction model can calculate one.

**Acceptance examples:**

- A finished project becomes Met.
- A project absent from the game's research list shows No method, with an unknown
  target explanation.
- If the governor cannot read the research list, it reports the missing information
  rather than declaring success.
- If work is blocked, show the reason. If no completion date can be estimated,
  leave the estimate absent.

**Intent status:** Documented intent. The documents disagree about the Infeasible
state and completion estimates; [Q1](review-queue.md#review-queue) preserves that
disagreement for review.

**Implementation status:** Supported for deciding research status and preparing
it for display. The player experience when a review fails has not been assessed:
an error in the code does not by itself establish that the window explains it well.

**Evidence and limits:** [E2 and E6](#evidence-ledger) cover missing projects, missing
research information and status display. The estimate test supplies day 45 and
checks that it appears; it does not test prediction accuracy. E7 checks that the
recorded status changes from Pursuing to Met, not that the window renders it.
Pursuing can mean no blocker has been reported; it does not guarantee a colonist
is researching at that moment.

**Open work:** [#3996](https://github.com/davidarcher/rimgovernor/issues/3996) tracks
the conflicting guidance. T3 would check how status reporting recovers after
missing information becomes available, if existing tests do not already cover it.

### RG-RES-004 — Respect research priorities without overriding safety

**Statement — candidate for review:** The importance and deadline the player gives
a research goal should influence how much effort the governor puts into it.
They must not weaken the colony's safety rules. They do not guarantee that the
project finishes first or by the requested day.

**Purpose:** Make the player's priorities matter without neglecting essential
colony needs to meet an ambitious request.

**Conditions:** The goal is unfinished, the governor can estimate progress toward
it, and there is work it can safely assign. This requirement concerns researcher
time; it does not decide when to replace the current research project.

**Acceptance examples:**

- Given the same choices, a High research goal can justify spending time on
  research when a Low goal would lose out to a greater wellbeing benefit.
- Raising importance does not allow work that violates a safety rule.
- A deadline can change the value of making progress today, but it does not make
  an unavailable research option possible.

**Intent status:** Inferred candidate requiring human review. The player guide
contradicts the developer documents and code. Issues
[#3503](https://github.com/davidarcher/rimgovernor/issues/3503) and
[#3505](https://github.com/davidarcher/rimgovernor/issues/3505) describe the design;
this recovered requirement has not received explicit approval.

**Implementation status:** Partial. Importance and deadlines affect the comparison
between possible work assignments. They do not change the ordering of the colony's
needs or automatically replace its current research project. These are observed
limits of the implementation, not proposed permanent restrictions.

**Evidence and limits:** [E9](#evidence-ledger) identifies tests that compare research
with other work using constructed examples. They do not establish actual game
choices, accurate completion dates or safety in every situation. Infeasible means
the options the governor evaluated failed its safety checks; it does not mean
success is impossible under every future circumstance.

**Open work:** Resolve Q1 before changing priority behavior. T4 proposes a focused
check of the agreed behavior, only if existing tests do not already cover it.

## Implementation trace

This section is for readers checking the connection to code. The evidence entries
below provide file links and exact test names.

1. **Save the player's edit.** The game window passes edits through `GoalsPanel`
   to `HostGoals.SaveAsync`, which validates and stores the goal, then asks the
   governor to review it (E1). The existing game test skips this step by inserting
   a goal directly into saved governor state.
2. **Decide what research is needed.** `Rounder` reads the goals and adds their
   projects to the colony's research requests. `EconomyRounds.ReadAsync` reads the
   game's research state. `EconomyRules.ResearchStepOf/ResearchNext` choose a
   prerequisite, leave an active project running, or request a bench. The result
   becomes a proposed plan through `EconomyFamily.Propose` (E2/E3).
3. **Select the project in RimWorld.** `Orders.ResearchAction` translates the
   plan into a game command. `ResearchActionHandler` checks that the game allows
   the selection, calls `SetCurrentProject`, and checks the result. This confirms
   selection, not completed research (E4).
4. **Assign a researcher.** `NativeResearchActivities.Read` finds colonists and
   benches that can do the work. Research joins the other possible assignments
   in the shared work comparison. The selected assignment names the colonist,
   bench and task; the governor records it before its executor, Hands, issues the
   order. The game then supplies the ordinary `WorkGiver_Researcher` job (E5).
   Even an accepted assignment does not establish that the colonist finished it.
5. **Report the result.** The governor reads the game's finished-project list.
   `GoalRules.Evaluate` decides the goal's status, and `GoalsRounds` makes it
   available to the window and records it in `goal_review` events (E6). The
   completion test waits for both finished research and Met status (E7).

## Evidence ledger

These references let an engineer check the claims above. Test names identify
assertions that were read; they do not report passing runs. Catalog registration
was checked in source. Neither `acceptance list` nor these game tests was run
for the pilot.

### E1 — Editing and storing the goal

The code connects editing, storage and a request to review the goal. Tests were read, not run. No window interaction or game save/reload was tested.

**Sources:** [GoalsWindow](../../integrations/rimgovernor-native/src/Bridge/Ui/GoalsWindow.cs), [GoalsPanel](../../brain/src/RimGovernor.Brain.PlayerApi/GoalsPanel.cs) `PumpAsync/SaveAsync`, [HostGoals](../../brain/src/RimGovernor.Brain.Host/PlayerProviders.cs) `Current/SaveAsync`, [HostGoalsTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HostGoalsTests.cs)

Inspected assertions: `HostGoalsTests.SaveIsVisibleOnTheNextViewAndInTheStoreAndWakesTheGoalsPlanner` and `InvalidRecordIsRefusedAndLeavesTheStoredRecordUnchanged`.

### E2 — Turning the goal into a research request

The code distinguishes finished projects, missing projects and missing information, and passes research requests into planning. Time from an edit to a planning decision was not measured.

**Sources:** [GoalsFamily](../../brain/src/RimGovernor.Brain.Policy/GoalsFamily.cs) `Evaluate/DemandOf`, [Rounder](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/Rounder.cs) goal-demand injection, [RoundsFishing](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsFishing.cs) `CensusResearchNeeds`, [GoalsFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/GoalsFamilyTests.cs) research tests

Inspected assertions: `GoalsFamilyTests.AResearchProjectGoalIsUnmetAndWantedUntilTheProjectIsFinished` checks requests, completion, an unknown target and an unread research list.

### E3 — Choosing a project or prerequisite

The code chooses research and requests benches. This does not establish that a laboratory or the entire prerequisite chain gets completed.

**Sources:** [Economy](../../brain/src/RimGovernor.Brain.Policy/Economy.cs) `Propose/ResearchStepOf/ResearchNext`, [RoundsResearch](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsResearch.cs) `ReadAsync/WorkAsync`, [EconomyFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/EconomyFamilyTests.cs) selection/hold assertions

Inspected assertions: `EconomyFamilyTests.TheFamilyProposesTheSelectionOrHoldsTheNeed` checks selecting a project versus leaving the current project running.

### E4 — Selecting research in the game

The command names the project; the game checks whether selection is allowed and confirms which project is selected. It does not confirm finished research.

**Sources:** [Orders](../../brain/src/RimGovernor.Brain.Bridge.Contracts/Actions/Orders.cs) `ResearchAction`, [native selection](../../integrations/rimgovernor-native/src/Bridge/Protocol/NativeResearchSelectOperations.cs) `ResearchActionHandler`

### E5 — Assigning and executing research work

The inspected code connects eligible workers to recorded assignments and ordinary game jobs. The shared executor and its recovery from failures were not fully audited.

**Sources:** [native research read](../../integrations/rimgovernor-native/src/Bridge/NativeResearchActivities.cs) `Read`, [candidate generation](../../brain/src/RimGovernor.Brain.Policy/Methods/Generation/ResearchMethodCandidates.cs) `Generate`, [coupled candidates](../../brain/src/RimGovernor.Brain.Policy/ForecastCoupledCandidates.cs), [binding](../../brain/src/RimGovernor.Brain.Policy/Methods/Binding/CoordinatedActivityBinding.cs) `StartSelected`, [RoundsCoupled](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsCoupled.cs) `AdmitCoupledSelection`, [Worker](../../brain/src/RimGovernor.Brain.BuildingRuntime/Worker.cs) dispatch loop, [native activities](../../integrations/rimgovernor-native/src/Bridge/NativePawnActivities.cs) Work job branch, [candidate tests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ResearchMethodCandidatesTests.cs)

Inspected assertions: `ResearchMethodCandidatesTests.ClaimedPawnsAndBenchesAreSkipped` checks that planning excludes colonists and benches already committed to other work; it does not test game reservations.

### E6 — Reporting status and estimates

The code calculates status from game observations and prepares it for display. No running window or estimate accuracy was tested.

**Sources:** [RoundsGoals](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsGoals.cs) `ReadAsync/Record/ReviewDecision`, [GoalsViewModel](../../brain/src/RimGovernor.Brain.PlayerApi/GoalsViewModel.cs), [GoalEtaTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/GoalEtaTests.cs) display assertion

Inspected assertions: `GoalEtaTests.AGoalsEstimatedDayIsDerivedIntoItsStatusAndShownOnThePursuingRow` supplies day 45 and checks the display, including the case with no estimate.

### E7 — Existing game test: research completion

Inspected and registered in source, not executed here. The test supplies a goal, bench and Brewing 90% complete. It checks finished research, Pursuing before Met, and prediction-model events. Its `selected` check accepts Current **or Finished**, so it need not observe the project while still in progress.

**Sources:** [GoalsResearch](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Goals/GoalsResearch.cs), [GoalsCases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Goals/GoalsCases.cs), [CaseCatalog](../../brain/src/RimGovernor.Brain.Acceptance.Cases/CaseCatalog.cs): `goals/research-goal`, OnDemand

### E8 — Existing game test: research progress

Inspected and registered in source, not executed here. The test supplies Brewing 50% complete and Research priority zero, then checks that the current project advances. It does not require completion or separately check recorded assignment events.

**Sources:** [ProductionResearchBlock](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionResearchBlock.cs), [CaseCatalog](../../brain/src/RimGovernor.Brain.Acceptance.Cases/CaseCatalog.cs): `production/research-block`, OnDemand

### E9 — Research priority and estimated completion

The code connects goal values to planning, with tests for selected calculations and display. No live comparison of priorities or estimate accuracy was observed.

**Sources:** [GoalValues](../../brain/src/RimGovernor.Brain.Policy/GoalValues.cs) `ValuesOf/Priced`, [GoalValueTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/GoalValueTests.cs), [GoalEtaTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/GoalEtaTests.cs), `Rounder.GoalEta.Set`

Inspected assertions: `GoalValueTests.AHighGoalOutranksIdleLaborWhileALowOneLosesToAWellbeingGainTheCurrencyPricesAboveIt` compares constructed outcomes; `ThePastDeadlineValueDecaysPerDayToTheFloor` checks the simpler deadline rule. The longer-term prediction model calculates progress value separately.

### Historical records

[#3504](https://github.com/davidarcher/rimgovernor/issues/3504)
reports `719239fcb` registered the goal cases and **neither ran**;
[#3527](https://github.com/davidarcher/rimgovernor/issues/3527) reports `e2943ff39`
was not run natively and still depended on T5 at that time. Those old dependency
remarks do not establish today's failure. [#3503](https://github.com/davidarcher/rimgovernor/issues/3503)
reports goal priority calculations landed at `51bf1917f`, with nothing run natively;
[#3505](https://github.com/davidarcher/rimgovernor/issues/3505) reports completion estimates and progress
value at `b65db4b9c`. No retained `result.json` from a current run was examined.
