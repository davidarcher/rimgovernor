# Pilot review and investment decisions

[Collection](README.md) · [Research requirements and evidence](research-goals.md)

This is an intent/evidence review queue. GitHub owns actionable deferred work;
the items below link it rather than duplicating an implementation backlog.
Requirement candidates await human review. No product code or tests changed.

## Review queue

| Item | Sources in disagreement and inspection finding | Decision still needed; tracking |
| --- | --- | --- |
| Q1 — Priority, state and ETA (RG-RES-003/004) | [Player goals](../players/goals.md#importance-and-deadline) says priority/deadline do not affect choices and its status section says no Infeasible or estimated day. [Goals contract](../developers/contracts/goals-contracts.md#importance-and-deadline) describes valuation/Infeasible, but retains a future-tense ETA sentence; [architecture](../developers/architecture/player-goals.md#value) also postpones ETA. E6/E9 show current value and conditional ETA/state paths; #3503/#3505 record those changes. | Confirm the player-facing promise: influence on feasible effort, conditional estimates, no promised deadline. Reconcile documentation without implying global project preemption. [#3996](https://github.com/davidarcher/rimgovernor/issues/3996), P2 |
| Q2 — Research control and proof (RG-RES-002) | [Research contract](../developers/contracts/research.md#prerequisite-graph-and-selection) says vanilla priorities schedule work, selection is player-owned unless verified, and native checks `expectedCurrent`. E3 holds any current ordinary project; E4 carries only ProjectDef, with native startability validation; E5 supplies explicit work. AGENTS.md says Auto has full control and stale-read guards are not ownership. The contract's acceptance section omits E7/E8, whose existence is not a pass. | Decide whether finishing an active project is intended continuity, and what change-of-current protection is required under general authority guards. Do not declare a race or remove protection merely from this sample. Correct execution/evidence descriptions. [#3997](https://github.com/davidarcher/rimgovernor/issues/3997), P2 |
| Q3 — Stop and unsaved progress (map-level finding) | [Save/resume](../players/save-and-resume.md#restart-a-session) says Stop leaves the game running; [launch](../players/launch.md) says it closes it. [MainForm](../../tools/launcher/MainForm.cs) maps both `stopController` and `closeGame` to [LauncherApp.CloseGameAsync](../../tools/launcher/LauncherApp.cs), which stops the private game; Restart closes then plays. | Align instructions and preserve explicit save guidance. This is sampled lifecycle code, not an audited stop/restart capability. [#3998](https://github.com/davidarcher/rimgovernor/issues/3998), P1 |

These issues were created after all-state and open-issue searches for goals
documentation/importance/deadline, research ownership/contracts and launcher Stop.
Related closed issues #3506, #3503, #3505, #3504 and #3527 were not treated as open
repair work. [#2351](https://github.com/davidarcher/rimgovernor/issues/2351) covers
campaign evidence and was explicitly parked by its 2026-10-18 comment; this pilot
does not authorize reopening or running it.

## Ranked implementation and documentation investments

1. **Correct lifecycle instructions (Q3; conflicting documentation).** Highest
   player consequence among these findings: following the wrong Stop advice can
   end a session before the player saves. Broadly affects every session and needs
   no gameplay change. Verify UI wiring and update the guide in #3998.
2. **Reconcile research authority and execution claims (Q2; unresolved intent and
   conflicting documentation).** This determines whether preserving or changing
   an active project is correct behavior and which boundary assertions matter.
   Review the general authority path before proposing an additional guard. No
   missing behavior has been demonstrated; #3997 is a decision/contract task.
3. **Correct priority and prediction guidance (Q1; conflicting documentation).**
   It affects how players choose goals and interpret lack of progress. The code
   already supplies a narrower capability than the guide acknowledges. Settle
   RG-RES-004 and update #3996 before adding priority/preemption functionality.

No product implementation fix is recommended as established fact from this
pilot. Unrun cases are **missing evidence for potentially correct behavior**,
not missing behavior. If a focused run fails, diagnose the native observation
and then file a specific behavior issue with its result. Broader unsupported
goal kinds are unassessed here, not automatically promised or permanently excluded.

## Ranked evidence investments

These are review proposals, not newly accepted test-writing tasks. Do not file
coverage bugs or add duplicate tests simply because no result was examined.
First search retained results and existing test bodies for the exact claim.

| Rank | Requirement and exact claim | Smallest useful setup and pass/fail observation | Cheapest suitable layer; why existing evidence is insufficient |
| --- | --- | --- | --- |
| T1a | RG-RES-002/003: an unfinished goal reaches native completion and Met through the served path | Reuse `goals/research-goal`: seeded goal, usable bench, Brewing near completion, goals/research/work only. Pass requires native Finished and Pursuing before Met; fail on missing outcome within its existing tick budget. | Execute the existing native case, retaining revision/environment/result.json. Unit tests cannot show game labor. Its source and original not-run note are not a pass. This is the first evidence investment because it checks the central promised outcome cheaply. |
| T1b | RG-RES-002: explicit work advances research without vanilla Research priority | Reuse `production/research-block`, priority zero, staged unfinished project/bench. Pass requires the project current with progress above the initial read. | Existing native case; no new test first. Complements T1a, whose priority-one setup alone cannot isolate explicit work. It proves progress only, not durable completion. Run after confirming source/build/result staleness per the charter. |
| T2 | RG-RES-001: a player edit reaches demand, and removing it withdraws only that goal's request | One loaded test world, host editor, ordinary goal, one review; observe stored record and demand inclusion, then removal and absence from demand when no other source asks. Invalid edit must retain the previous record. | Boundary integration through real editor/store/review with typed native facts. Existing HostGoals tests end at store/wake and the served case seeds the blob. Inspect existing integrated tests before adding one; no UI automation needed for this seam claim. Manual and native save/reload remain separate claims, requiring separate investigation. |
| T3 | RG-RES-003: a missing fact never becomes success and a subsequent valid review restores truthful status | One unfinished goal and test status board; make research census unavailable, then supply finished research. Observe required-fact failure without a false Met, then Met after the successful review. | Boundary fault test of review/status publication, after surveying existing tests. The pure missing-census assertion does not establish whether stale status is exposed or recovered in the runtime. No arbitrary latency assertion. |
| T4 | RG-RES-004: approved priority semantics influence the actual selected task without bypassing a floor | After Q1, use identical observed pawn/bench/task facts with Low and High goal values, plus a competing wellbeing task; assert the agreed winner and unchanged refusal for a floor-breaking route. | Pure policy selection test, only if current GoalValue/coupled tests do not already assert this path. Current sampled tests compare synthetic consequence vectors; a new arithmetic mirror adds no value. Q2 must be resolved before testing project replacement. |
| Later | RG-RES-002 across competing needs: research continues to useful unlocks without sustained essential-need neglect | An ordinary colony campaign with declared goals and native completion/need-state observations; no post-setup interventions. Agree scenario and acceptance claims before spending run time. | Campaign evidence, not a longer staged case. E7/E8 isolate three families and cannot establish sustained autonomy. Coordinate with parked #2351; no new duration or performance target is invented here. |

For T1, obey the existing [acceptance charter](../developers/testing/acceptance-guide.md#charter):
stage preconditions, observe native effects, use existing tick bounds, and diagnose
stalls rather than enlarging budgets. Registration is distinct from execution and
passing. UI rendering, save/reload, laboratory acquisition and prerequisite chains
would each need their own claim; combining them into T1 would obscure failures.

## Review checkpoint

Approve or amend the proposed outcome map and entry format; review RG-RES-004 and
Q2's authority question explicitly. Then authorize expansion across the remaining
capabilities. None of that scope has been audited yet. The next pass should retain
this distinction between intent, reachable implementation and observed proof.
