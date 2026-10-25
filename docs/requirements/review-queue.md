# Questions and recommended work

[Collection](README.md) · [Research requirements and evidence](research-goals.md)

The pilot found three documentation disagreements and several places where more
evidence would help. They are different kinds of work: a misleading guide needs
correction, an unresolved product choice needs a decision, and an unrun test needs
a result before it can establish a bug. No product code or tests changed.

## Review queue

### Q1 — What should importance, deadlines and estimates mean?

Affects RG-RES-003 and RG-RES-004.

The [player guide](../players/goals.md#importance-and-deadline) says importance
and deadlines do not affect choices. It also says there is no Infeasible state or
estimated completion day. The [goals contract](../developers/contracts/goals-contracts.md#importance-and-deadline)
describes implemented priority effects and Infeasible, but still says the estimate
is future work. The [architecture note](../developers/architecture/player-goals.md#value)
also postpones estimates.

The code has all three features, with limits: priorities influence work choices,
estimates require a suitable prediction, and Infeasible refers to the options
evaluated. E6/E9 in the research pilot identify that code; #3503/#3505 record its
introduction. This does not establish that the governor will switch projects or
meet a deadline.

**Recommendation:** Describe priorities as influencing effort and dates as
estimates. Confirm that product promise before reconciling the documents.
Tracked in [#3996](https://github.com/davidarcher/rimgovernor/issues/3996), P2.

### Q2 — When should the governor change the current research project?

Affects RG-RES-002.

The [research contract](../developers/contracts/research.md#prerequisite-graph-and-selection)
says RimWorld's ordinary work priorities schedule research. It also describes the
selected project as player-owned unless the governor selected it, and says a
command checks that the current project has not changed since planning.
AGENTS.md says Auto has full control of the colony and checks for outdated
information must not be used as ownership markers.

The inspected code leaves any current ordinary project running. It also assigns
research work directly. Its selection command names the requested project and
checks whether RimWorld allows it; it does not carry the contract's described
`expectedCurrent` field. Other controls on when orders may execute were not fully
examined, so this difference alone does not establish a race or an unsafe command.
The contract also omits the two existing game tests, E7/E8. Those tests exist,
but we have no current passing result for them.

**Recommendation:** Decide whether completing the active project before switching
is intended behavior, independently of who selected it. Then check what protection
is needed if the current project changes between planning and execution. Correct
the contract's descriptions of work assignment and test evidence.
Tracked in [#3997](https://github.com/davidarcher/rimgovernor/issues/3997), P2.

### Q3 — Does Stop leave the game running?

This was found while reading the player guides; it is outside the research pilot.

[Save and resume](../players/save-and-resume.md#restart-a-session) says Stop leaves
RimWorld running. [Launch](../players/launch.md) says it closes the game.
[MainForm](../../tools/launcher/MainForm.cs) connects both Stop and Close game to
[LauncherApp.CloseGameAsync](../../tools/launcher/LauncherApp.cs), which stops the
private game. Restart closes the game and then starts it again.

**Recommendation:** Align the guides and make the need to save before stopping
clear. The button wiring was inspected; the full stop/restart behavior was not
run. Tracked in [#3998](https://github.com/davidarcher/rimgovernor/issues/3998), P1.

These issues were filed after checking open and closed issues for matching reports
about goals documentation, importance/deadlines, research ownership/contracts and
launcher Stop. Related closed issues #3506, #3503, #3505, #3504 and #3527 do not
track these outstanding corrections.

## Ranked implementation and documentation investments

1. **Correct Stop and save instructions (Q3).** This comes first because the wrong
   advice can end a session before the player saves. It affects every session and
   requires no gameplay change.
2. **Resolve research control and update its contract (Q2).** Decide whether keeping
   or replacing an active project is correct before changing the implementation
   or writing tests that assume an answer. Inspect the existing order protections
   before proposing another one.
3. **Correct priority and estimate guidance (Q1).** The disagreement affects how
   players express their wishes and interpret delays. Agree the intended behavior
   before adding any new priority or project-switching behavior.

The pilot has not demonstrated a missing or broken gameplay feature that warrants
an implementation fix. Unrun tests leave an evidence gap. If a focused run fails,
diagnose the observed failure and file a specific issue with the result. Other
goal kinds have not been assessed here; this pilot neither promises them nor rules
them out permanently.

## Ranked evidence investments

These are proposals for review, not authorization to add or run tests. First look
for existing results and tests that establish the same claim. Do not create a bug
or a duplicate test solely because this pilot has not examined the evidence.

### T1a — Show that a requested project actually finishes

**Claim:** RG-RES-002/003 — the governor completes unfinished research and reports
Met after Pursuing.

**Smallest useful check:** Run the existing `goals/research-goal` game test. It
starts with a goal, a usable bench and Brewing nearly complete, with only the
goals, research and work features enabled. Pass requires the game to report the
project finished and the recorded goal status to change from Pursuing to Met
within the existing game-tick budget.

**Why this layer and priority:** A unit test cannot show colonists doing real game
work. This existing acceptance case checks the central promised outcome without
a new test or a long colony run. Its source and original not-run note are not a
passing result. Retain the revision, game environment and `result.json`.

### T1b — Show that assigned research works without ordinary work priorities

**Claim:** RG-RES-002 — the governor's explicit assignment advances research even
when the colonist's ordinary Research priority is off.

**Smallest useful check:** Run the existing `production/research-block` game test.
It starts with an unfinished project and bench, and Research priority set to zero.
Pass requires the project to be current and its progress to rise above the initial
reading.

**Why this layer:** This complements T1a, whose enabled Research priority does not
by itself distinguish the governor's assignments from ordinary scheduling. It
proves progress, not completion. Check whether existing results and builds are
still applicable before running it.

### T2 — Follow an edit through to a research request

**Claim:** RG-RES-001 — adding a goal makes the governor request that research;
removing it withdraws that request unless another colony need still requires it.

**Smallest useful check:** Use one test colony, the real goal editor and storage,
and one planning review. Add an ordinary goal and check both its stored record and
the research request. Remove it and check that the request disappears when no
other source asks for it. An invalid edit must leave the previous record intact.

**Why this layer:** An integration test with supplied game observations can check
these connections without launching RimWorld or automating the window. The
inspected editor tests stop at saving and requesting a review; the game test
inserts a goal directly into saved state. Look for an existing test covering the
connection before adding one. Auto-off and game save/reload behavior are separate
claims that still need investigation.

### T3 — Recover truthful status after a failed observation

**Claim:** RG-RES-003 — missing research information never becomes success, and a
later valid observation restores the correct status.

**Smallest useful check:** Start with one unfinished goal. Make the research list
unavailable, then supply a reading showing the project finished. Pass requires a
missing-information error without a false Met, followed by Met after the successful
review.

**Why this layer:** An integration test can exercise failure and recovery in status
reporting. The inspected unit assertion only shows that missing information raises
an error; it does not establish what status remains visible afterward. Survey
existing failure tests first. No response-time target is proposed.

### T4 — Check the agreed effect of research priority

**Claim:** RG-RES-004 — the approved priority behavior changes the chosen work
without weakening safety checks.

**Smallest useful check:** After Q1 is resolved, present the same colonist, bench
and possible tasks with a Low research goal and then a High one, alongside a
competing wellbeing task. Check the agreed choice and that an unsafe option is
still refused.

**Why this layer:** A policy test can check the decision without a game run. Add it
only if the current goal-value and work-selection tests do not already cover the
claim. The sampled tests compare constructed estimates of outcomes; repeating
their arithmetic would add little. Resolve Q2 before testing project replacement.

### Later — Assess research during sustained colony management

**Claim:** RG-RES-002 across competing needs — research reaches useful unlocks
without prolonged neglect of essential colony needs.

Use an ordinary colony campaign with declared goals, observed research completion
and recorded colony conditions. Agree the scenario and pass/fail claims first,
and make no interventions after setup. The short tests enable only three features
and cannot establish long-term autonomous behavior.

This belongs with [#2351](https://github.com/davidarcher/rimgovernor/issues/2351),
which was explicitly parked in its 2026-10-18 comment. This pilot does not authorize
reopening or running it, and proposes no new duration or performance target.

For T1, follow the [acceptance charter](../developers/testing/acceptance-guide.md#charter):
prepare the starting conditions directly, observe real game effects, and diagnose
stalls instead of increasing the budget. Window rendering, save/reload, building
a laboratory and completing prerequisites each need a separate claim; combining
them would make failures harder to interpret.

## Review checkpoint

Review the proposed capability map, the requirement format, RG-RES-004 and Q2's
question about switching projects. Expansion still requires explicit approval.
The remaining capabilities have not been audited. Keep intended behavior, current
implementation and test results distinct as the collection grows.
