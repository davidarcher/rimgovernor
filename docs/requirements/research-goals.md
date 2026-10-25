# Achieve a player's research goal

[Collection and coverage](README.md) · [Review queue and investments](review-queue.md)

Pilot scope: an ordinary `ResearchProject` goal on a loaded colony with Auto and
the goals, research and work paths enabled. A reachable route exists from the
in-game editor to goal demand, project selection, explicit pawn work, native
research completion and status readback. That trace is **not a witnessed run**.
Research tiers, knowledge-category study, laboratory construction from scratch,
all DLCs and sustained development are outside this pilot's support assessment.

## Requirements

### RG-RES-001 — Retain and apply a valid research request

- **Statement:** The player can add, edit and remove a research goal. Accepted
  edits appear in the goals view and feed research demand while Auto is active;
  a rejected edit explains the refusal and leaves the previous request intact.
- **Purpose:** Let the player direct development without losing intent to an
  invalid form submission or confusing a saved request with completed work.
- **Conditions:** A loaded colony and running brain; structurally valid ordinary
  project goal. Native game-save durability is a separate continuity obligation.
- **Acceptance examples:** Add an unfinished listed project: it is retained and
  requests research after review. Submit an invalid record: previous goal remains,
  with a reason. Remove the goal: subsequent demand no longer includes it solely
  on that goal's behalf; removal need not cancel already-current research. With
  Auto off, the guide promises retention/display without pursuit.
- **Intent status:** Documented intent — [goals guide](../players/goals.md#add-a-goal)
  and [goals contract](../developers/contracts/goals-contracts.md#needs).
- **Implementation status:** Supported for editor/store/demand seams under the
  stated active conditions. Manual suppression and native save/reload are
  unassessed here, not inferred from the store tests.
- **Evidence:** E1 (editor and host), E2 (demand) below; `HostGoalsTests.SaveIsVisibleOnTheNextViewAndInTheStoreAndWakesTheGoalsPlanner`
  and `InvalidRecordIsRefusedAndLeavesTheStoredRecordUnchanged` assertion bodies
  inspected. `GoalsFamilyTests.AResearchProjectGoalIsUnmetAndWantedUntilTheProjectIsFinished`
  checks goal demand and finished-state withdrawal.
- **Evidence limits:** Host tests use a test journal/world, not a clicked Verse
  window or reloaded RimWorld save. The native case seeds the blob, bypassing edits.
  No claim that unsaved edits survive a crash; see [persistence ownership](../developers/contracts/persistence-contracts.md).
- **Gaps/decisions:** UI-to-native and Manual/reload evidence remain unassessed;
  investment T2 is conditional on examining existing continuity evidence first.

### RG-RES-002 — Turn research demand into legal, completed research

- **Statement:** When an ordinary research goal is actionable and research is
  selected for available colony labor, the governor selects its needed project
  or prerequisite and assigns eligible work toward native completion. Selecting
  a project alone must not satisfy the goal.
- **Purpose:** Deliver the unlock the player requested, rather than merely an
  accepted command or an optimistic progress label.
- **Conditions:** Observed project/prerequisite graph, supported startable project,
  usable reachable bench, capable available pawn and admitted work. Other colony
  needs can postpone research; this is not an unconditional completion deadline.
- **Acceptance examples:** With an empty ordinary research slot and nearly
  finished Brewing at a usable bench, Auto finishes it and then reports Met.
  With an unfinished prerequisite, select that prerequisite but keep the original
  goal unmet. A missing bench requests capacity or reports the block; a missing
  prerequisite fact does not certify the unlock. An accepted selection receipt
  with no subsequent pawn work leaves the goal unmet.
- **Intent status:** Documented intent — [research completion](../developers/contracts/research.md#completion)
  and [research goal predicate](../developers/contracts/goals-contracts.md#needs).
- **Implementation status:** Supported for the staged ordinary-project path.
  Prerequisite selection and bench requests are inspected policy, but full
  prerequisite/laboratory delivery is unassessed. Active-project replacement is
  deliberately unresolved in [Q2](review-queue.md#review-queue).
- **Evidence:** E2–E5 trace selection and execution; E7 and E8 are registered
  native cases asserting completion/status and actual research progress respectively.
  `EconomyFamilyTests.TheFamilyProposesTheSelectionOrHoldsTheNeed` checks a selection
  action versus the current-project hold. `ResearchMethodCandidatesTests.ClaimedPawnsAndBenchesAreSkipped`
  checks candidate exclusions, not native reservation success.
- **Evidence limits:** No current native pass. E7 stages Brewing 90% complete and
  does not exercise prerequisite work or build the bench. E8 measures progress,
  not completion. Neither tests all goals, normal competing families or sustained
  autonomy. Native legality may still refuse an assignment after planning.
- **Gaps/decisions:** Evidence investment T1 before assuming a gameplay defect;
  [#3997](https://github.com/davidarcher/rimgovernor/issues/3997) owns research contract
  reconciliation. No unconditional promise of a safe, starvation-free research run.

### RG-RES-003 — Explain completion, inability and predictions honestly

- **Statement:** Report Met only when the requested project is observed finished.
  Otherwise distinguish an owner pursuing it, a reported blocker, and an unavailable
  target. Missing required facts must not fabricate success. A displayed completion
  estimate must remain a prediction, not a promised finishing date.
- **Purpose:** Let the player diagnose stalled development without trusting a
  selection receipt, absent observation or model forecast as an achieved unlock.
- **Conditions:** Goal review with observed research facts; status display may
  lag an edit until review. ETA is conditional on the model having a completion path.
- **Acceptance examples:** Finished target becomes Met. Known census without the
  target gives No method/unknown target. Unread census raises the missing-fact
  path rather than producing Met. A blocked owner supplies a reason; a newly
  added goal is Pending before review. No modeled ETA leaves the estimate absent.
- **Intent status:** Documented intent, with conflicting descriptions of Infeasible
  and ETA explicitly recorded in Q1 rather than silently resolved.
- **Implementation status:** Supported for the inspected research predicate and
  status/view seams. The complete player experience during failed reviews is
  unassessed; throwing a required-fact exception alone does not prove a useful UI.
- **Evidence:** E2/E6; the research-goal test named under RG-RES-001 checks missing
  target versus missing census. `GoalEtaTests.AGoalsEstimatedDayIsDerivedIntoItsStatusAndShownOnThePursuingRow`
  injects day 45 and asserts the displayed detail, also checking absent ETA.
  E7 checks Pursuing before Met in telemetry.
- **Evidence limits:** The ETA test supplies the estimate; it does not calibrate
  prediction accuracy. Pursuing can mean an owner has no reported block, not that
  a pawn is currently researching. Telemetry order is not UI rendering evidence.
- **Gaps/decisions:** [#3996](https://github.com/davidarcher/rimgovernor/issues/3996)
  tracks contradictory state/ETA guidance. T3 would assess recovery of the visible
  explanation, after surveying existing failure-path tests.

### RG-RES-004 — Let expressed priority influence effort without overriding safety

- **Statement:** Candidate for review: effective goal importance and deadline
  should influence the value of progress on a supported research goal while
  retaining the same safety floors; neither is a guarantee that it finishes first
  or by the requested day.
- **Purpose:** Make player direction meaningful without spending essential colony
  wellbeing merely to satisfy an ambitious request.
- **Conditions:** An unmet goal with a forecast route and feasible evaluated work;
  this concerns effort allocation, not automatic replacement of the current project.
- **Acceptance examples:** In an otherwise equal policy setup, High research can
  outrank idle or a lesser valued wellbeing improvement where Low does not. A
  floor-violating route is not authorized by increasing importance. A deadline may
  change modeled progress value but does not turn an unavailable route into one.
- **Intent status:** Inferred candidate requiring human review because the player
  guide contradicts the implemented/developer-described behavior. [#3503](https://github.com/davidarcher/rimgovernor/issues/3503)
  and [#3505](https://github.com/davidarcher/rimgovernor/issues/3505) document the
  design, but this recovered requirement has not received explicit approval.
- **Implementation status:** Partial relative to broad priority control: valuation
  exists in coupled comparison, not Need ordering or current-project preemption.
  This is an observed scope limit, not a permanent product constraint.
- **Evidence:** E9; `GoalValueTests.AHighGoalOutranksIdleLaborWhileALowOneLosesToAWellbeingGainTheCurrencyPricesAboveIt`
  compares synthetic consequence vectors. `ThePastDeadlineValueDecaysPerDayToTheFloor`
  checks the flat-value policy. Tier-2 marginal value is a separate modeled route.
- **Evidence limits:** Synthetic ranking is not live project choice, calibrated
  ETA, or comprehensive safety evidence. The forecast considers bounded routes;
  Infeasible does not mean mathematical impossibility in all future game states.
- **Gaps/decisions:** Q1 resolves the intended promise before any implementation
  investment; no new numerical targets are proposed. Preserve the existing tests
  rather than creating another arithmetic mirror.

## Implementation trace

1. **Player intent:** `GoalsWindow` supplies `GoalsPanel` with the host's editor
   and view. The panel queues edits; `HostGoals.SaveAsync` validates, stores and
   wakes the goals planner (E1). This is the production entry seam that E7 bypasses.
2. **Demand and policy:** `Rounder` loads goals, calls `GoalRules.DemandOf`, replaces
   `_goalResearch` and merges it via `CensusResearchNeeds`. `EconomyRounds.ReadAsync`
   reads the native research census. `EconomyRules.ResearchStepOf/ResearchNext`
   choose a prerequisite, hold an existing current project, or request a bench.
   `EconomyFamily.Propose` produces the selection Method (E2/E3).
3. **Selection execution:** The selection action maps through
   `Orders.ResearchAction` to `ResearchIntent`. `ResearchActionHandler` checks
   native startability, calls `SetCurrentProject` and verifies the selected slot.
   That receipt establishes selection only (E4).
4. **Actual labor:** `NativeResearchActivities.Read` observes eligible pawn/bench
   pairs for the current project. `ResearchMethodCandidates.Generate` offers
   research blocks to `ForecastCoupledCandidates`; shared selection and
   `CoordinatedActivityBinding.StartSelected` bind the chosen tasks.
   `Rounder.AdmitCoupledSelection` commits a claimed pawn-activity Method under
   work assignments. The worker dispatches journal actions through Hands;
   native Work steps obtain the game's `WorkGiver_Researcher` job (E5).
   A candidate or an admitted activity still does not prove completed labor.
5. **Observable outcome:** The native research read supplies finished projects;
   `GoalRules.Evaluate` derives status, `GoalsRounds` publishes the status board
   and `goal_review`, and the host/view model present it (E6). E7 waits for the
   native finished predicate and Met, rather than merely accepting the order.

## Evidence ledger

All paths below are relative links to the examined baseline's files. Symbols are
locators, not separate requirements. Source-only registration was inspected;
`acceptance list` and the cases were not executed for this pilot.

| Key | Examined source and symbols | What it establishes; limit |
| --- | --- | --- |
| E1 | [GoalsWindow](../../integrations/rimgovernor-native/src/Bridge/Ui/GoalsWindow.cs), [GoalsPanel](../../brain/src/RimGovernor.Brain.PlayerApi/GoalsPanel.cs) `PumpAsync/SaveAsync`, [HostGoals](../../brain/src/RimGovernor.Brain.Host/PlayerProviders.cs) `Current/SaveAsync`, [HostGoalsTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HostGoalsTests.cs) | Reachable editor/store/wake seam and inspected assertions; no real UI run or native save/load |
| E2 | [GoalsFamily](../../brain/src/RimGovernor.Brain.Policy/GoalsFamily.cs) `Evaluate/DemandOf`, [Rounder](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/Rounder.cs) goal-demand injection, [RoundsFishing](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsFishing.cs) `CensusResearchNeeds`, [GoalsFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/GoalsFamilyTests.cs) research tests | Finished predicate, missing-fact distinction and demand plumbing; no live demand latency measurement |
| E3 | [Economy](../../brain/src/RimGovernor.Brain.Policy/Economy.cs) `Propose/ResearchStepOf/ResearchNext`, [RoundsResearch](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsResearch.cs) `ReadAsync/WorkAsync`, [EconomyFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/EconomyFamilyTests.cs) selection/hold assertions | Policy and bench-request path; not a completed laboratory or whole prerequisite chain |
| E4 | [Orders](../../brain/src/RimGovernor.Brain.Bridge.Contracts/Actions/Orders.cs) `ResearchAction`, [native selection](../../integrations/rimgovernor-native/src/Bridge/Protocol/NativeResearchSelectOperations.cs) `ResearchActionHandler` | ProjectDef intent, native validation and selected-slot readback; no research completion |
| E5 | [native research read](../../integrations/rimgovernor-native/src/Bridge/NativeResearchActivities.cs) `Read`, [candidate generation](../../brain/src/RimGovernor.Brain.Policy/Methods/Generation/ResearchMethodCandidates.cs) `Generate`, [coupled candidates](../../brain/src/RimGovernor.Brain.Policy/ForecastCoupledCandidates.cs), [binding](../../brain/src/RimGovernor.Brain.Policy/Methods/Binding/CoordinatedActivityBinding.cs) `StartSelected`, [RoundsCoupled](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsCoupled.cs) `AdmitCoupledSelection`, [Worker](../../brain/src/RimGovernor.Brain.BuildingRuntime/Worker.cs) dispatch loop, [native activities](../../integrations/rimgovernor-native/src/Bridge/NativePawnActivities.cs) Work job branch, [candidate tests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ResearchMethodCandidatesTests.cs) | Sampled path from observed eligibility to journaled work and native jobs; shared executor internals/fault recovery not fully audited |
| E6 | [RoundsGoals](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsGoals.cs) `ReadAsync/Record/ReviewDecision`, [GoalsViewModel](../../brain/src/RimGovernor.Brain.PlayerApi/GoalsViewModel.cs), [GoalEtaTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/GoalEtaTests.cs) display assertion | Native-derived status producer and display seam; not an executed UI or prediction calibration |
| E7 | [GoalsResearch](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Goals/GoalsResearch.cs), [GoalsCases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Goals/GoalsCases.cs), [CaseCatalog](../../brain/src/RimGovernor.Brain.Acceptance.Cases/CaseCatalog.cs): `goals/research-goal`, OnDemand | Inspected and registered in source. Seeds goal, bench and 90%-done Brewing; asserts finished research and Pursuing before Met, plus model rows. `selected` accepts Current **or Finished**, so it need not witness a current-project intermediate read. Not executed here |
| E8 | [ProductionResearchBlock](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionResearchBlock.cs), [CaseCatalog](../../brain/src/RimGovernor.Brain.Acceptance.Cases/CaseCatalog.cs): `production/research-block`, OnDemand | Inspected and registered in source. Seeds 50%-done Brewing and Research priority zero; asserts current project progress rises. Does not require finishing or independently assert assignment telemetry. Not executed here |
| E9 | [GoalValues](../../brain/src/RimGovernor.Brain.Policy/GoalValues.cs) `ValuesOf/Priced`, [GoalValueTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/GoalValueTests.cs), [GoalEtaTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/GoalEtaTests.cs), `Rounder.GoalEta.Set` | Goal value plumbing and sampled policy/view assertions; no observed live priority comparison or ETA accuracy |

Historical provenance: [#3504](https://github.com/davidarcher/rimgovernor/issues/3504)
reports `719239fcb` registered the goal cases and **neither ran**;
[#3527](https://github.com/davidarcher/rimgovernor/issues/3527) reports `e2943ff39`
was not run natively and still depended on T5 at that time. Those old dependency
remarks do not establish today's failure. [#3503](https://github.com/davidarcher/rimgovernor/issues/3503)
reports valuation landed at `51bf1917f`, with nothing run natively;
[#3505](https://github.com/davidarcher/rimgovernor/issues/3505) reports ETA/marginal
value at `b65db4b9c`. No retained `result.json` from a current run was examined.
