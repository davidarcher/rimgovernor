# Requirements review queue

[Collection](README.md) · [Completion audit](completion-audit.md)

This queue separates confirmed implementation limits, conflicting descriptions,
product decisions and evidence investments. All nine mapped capabilities have
representative source/test traces. No gameplay pass is claimed from this work.

## Q1 — Who chooses food work?

The [controller contract](../developers/contracts/controller-contracts.md#priority-evaluation)
describes vanilla priorities scheduling routine food work. The
[pawn activity contract](../developers/contracts/pawn-activities.md#needs-forecast-support-matrix)
describes explicit governor choices for supported eating and work.

Both mechanisms are reachable. AcquisitionPlanner commits harvest
designations; NativeAcquire maps plant acquisition to HarvestPlant. Explicit
coupled activities also install exact work/eating assignments. The registered
`food/forage-unarmed` case serves acquisition and work, asserting designation and
berry consumption without identifying which scheduler chose each job.
`control/unassigned-runs-vanilla` separately requires vanilla work with Auto on
and no Hands assignment.

**Decision needed:** Describe the conditions for each mechanism consistently.
Full autonomous feeding is approved; source inspection does not make every job
an explicit governor assignment. [#4000](https://github.com/davidarcher/rimgovernor/issues/4000)
owns the contract/case-description correction. No new investigation is needed to
establish that the descriptions currently disagree.

## Q2 — What does Stop close?

The controls/save guides distinguish stopping the brain from closing the game.
MainForm binds Stop and Close game to LauncherApp.CloseGameAsync; Restart closes
the game and calls Play. The launch guide agrees with that code.

**Correction:** Align the guides with the actual buttons, or deliberately change
the lifecycle promise. Unsaved progress makes this the first documentation
priority. [#3998](https://github.com/davidarcher/rimgovernor/issues/3998), P1.

## Q3 — Confirmed autonomous-planning gaps

These paths explicitly report unsupported work. Native tools or fixture-driven
tests beneath them do not establish autonomous support. The desired admission
rules remain design decisions; today's holds are not permanent product limits.

| Affected outcome | Confirmed limit and issue | Why it matters |
| --- | --- | --- |
| Usable facilities, RG-HABITAT-002 | Ordinary repairs [#4006](https://github.com/davidarcher/rimgovernor/issues/4006); door-to-wall replacement [#4007](https://github.com/davidarcher/rimgovernor/issues/4007) | Damage and blocked enclosure changes can undermine essential rooms. |
| Safe workers, RG-PROTECT-001 | Defensive equipping [#4008](https://github.com/davidarcher/rimgovernor/issues/4008); precautionary disarming [#4010](https://github.com/davidarcher/rimgovernor/issues/4010) | Exposure to injury; immediate combat has a separate path. |
| Everyday wellbeing, RG-HEALTH-002 | Residual apparel replacement [#4009](https://github.com/davidarcher/rimgovernor/issues/4009); gatherings [#4001](https://github.com/davidarcher/rimgovernor/issues/4001) | Clothing production exists, but complete gear recovery and social gatherings are narrower. |
| Capacity and progression, RG-COMMIT-001/003 | Population capture/lance use [#4003](https://github.com/davidarcher/rimgovernor/issues/4003); rituals [#3580](https://github.com/davidarcher/rimgovernor/issues/3580); bestowing [#4002](https://github.com/davidarcher/rimgovernor/issues/4002); neuroformers [#4004](https://github.com/davidarcher/rimgovernor/issues/4004); monolith [#4005](https://github.com/davidarcher/rimgovernor/issues/4005) | Promised opportunities stop at explicit planner holds. DLC and colony conditions limit breadth. |
| Useful supply, RG-SUPPLY-001/002 | Zero-stock bootstrap [#3641](https://github.com/davidarcher/rimgovernor/issues/3641); selected trade forecast handoff [#3917](https://github.com/davidarcher/rimgovernor/issues/3917), [#3918](https://github.com/davidarcher/rimgovernor/issues/3918), [#3919](https://github.com/davidarcher/rimgovernor/issues/3919) | Prepared production and native deal tests leave these wider dependencies unresolved. |

The capability pages give code/test evidence for the holds. Existing bootstrap
and trade issues supply their own scoped findings and acceptance; they should be
revalidated against current code when implemented. Exact and broader open-issue
searches preceded filing the new hold issues; record-ownership work was not
treated as implementation of the held action.

**Implementation priority:** After correcting Q2/Q1, prioritize repair, useful
equipment and food/bootstrap dependencies because they affect survival and many
other capabilities. Social/DLC progression follows unless a particular colony
makes it urgent. Diagnose an observed feeding failure before choosing a food fix.
The issue links own implementation details; this collection does not duplicate
their backlog.

## Q4 — Manual-save freshness

SaveHandshake allows a manual game save after its flush acknowledgement times
out; it also proceeds when no waiter exists. The persistence contract describes
flushing at save time and warns about disconnection, but omits the connected
timeout limit. RG-CONTINUITY-001 therefore cannot promise fresh governor intent
for every manual save.

[#4011](https://github.com/davidarcher/rimgovernor/issues/4011) tracks the decision:
document bounded best-effort freshness, or require a visible failure when fresh
intent cannot be saved. A completed governed checkpoint has a stricter boundary.
This is a concrete source discrepancy and intent question, not an observed
corrupted-save incident.

## Q5 — Player-facing explanations

The current launcher exposes expected work, progress ticks, subjects and raw
blocker codes, with older “Concerns” labels. It does not offer a complete
predicted-versus-realized outcome comparison. RG-EXPLAIN-002's broader presentation
is a candidate for review. [#3433](https://github.com/davidarcher/rimgovernor/issues/3433)
already owns the product-interface redesign and requires design discussion.
Resolve the audience, wording and outcome comparison there; do not silently
treat the proposed in-game panel as already implemented.

## Recommended evidence investments

These recommendations are separate from implementation fixes. Reuse a compatible
retained result or an existing case before adding a test. No bug is inferred from
an unexecuted case. Native checks follow the [acceptance charter](../developers/testing/acceptance-guide.md#charter);
pure decisions and integration boundaries use the [test-selection guide](../developers/testing/choose-tests.md).

### T1 — Feeding from an unarmed start

**First:** RG-FOOD-001/002, because starvation is severe and the existing case is
small. Reuse `food/forage-unarmed`: one unarmed hungry colonist, ripe bushes and no
food. Pass requires designated berries and recorded eating within its existing
budget. Native acceptance is needed for harvesting/ingestion. Source F5 supplies
the assertions, with no current result bundle found in this worktree.

### T2 — Recover and get fed

**Second:** RG-FOOD-003/004. Stage a hungry colonist with a cooking option and a
usable alternative; remove the selected ingredient once after assignment.
Require a fresh autonomous choice, any necessary output, and eating. Native
acceptance is the cheapest layer for this combined effect. F6 directs its own
retries; continuation tests supply observations. F11 repeats successful meals
with an injected rest reset, so it does not cover this loss-and-recovery claim.

### T3 — Food arrives before the shortage

**Third:** RG-FOOD-002. Use a pure decision setup where current stock ends before
a crop matures and an earlier source is feasible. Require a timely selection;
with no timely source, require an explicit remaining shortage. Reuse supply and
channel tests for their existing timing claims. F4's forage check begins with a
selected source; native planting and eating cases do not establish this deadline
choice. Add only the uncovered selection assertion.

### T4 — Recover from missing information

**Fourth:** RG-FOOD-004. A boundary test should feed an unread food observation,
then fresh facts showing a shortage through review and status publication.
Require unresolved adequacy followed by renewed planning. Existing required-fact
and continuation checks reject missing data; the proposed claim joins that
refusal to the next published review.

### T5 — Continued food replacement

**Later:** RG-FOOD-001/002. Sustained seasonal sufficiency needs campaign evidence
with agreed colony conditions and observed food/health outcomes. Bounded cases
cannot establish it. [#2351](https://github.com/davidarcher/rimgovernor/issues/2351)
remains parked by user decision. No campaign run or invented duration is proposed
as part of this documentation task.

### T6 — Control and save boundaries

**After food, alongside Q2:** RG-CONTROL-002 and RG-CONTINUITY-001/002. Reuse
ResumePauseClockOwnerTests and native `lifecycle/checkpoint`/`lifecycle/load` for
their established control, replay and blob round-trip claims. A further native
claim would stage one saved layout and active assignment, load into a fresh
journal, and require restored layout plus refusal of old-load work. Existing
blob and supplied-observation cases do not prove that integrated reconstruction.
Keep native interruption and manual-save timeout as separate claims.

### T7 — Finish shelter and essential care

**Next survival priority:** RG-HABITAT-001 and RG-HEALTH-001. Reuse
`layout/first-bedroom-closes` and `layout/first-room-ice` for their prepared room
outcomes. For autonomous care, stage one tendable patient and eligible doctor;
require changed native treatment state within a short budget. Medical eligibility
and learning-callback tests do not assert the complete automatic treatment path.
Recovery from the disease is a separate, longer claim.

### T8 — Connect resource demand to delivery

**After basic survival:** RG-SUPPLY-001. Stage one deficit, reachable source,
eligible worker and useful destination. Require autonomous selection, yield and
arrival available to the consuming work. Native acceptance is required.
Promise tests cover accounting; mining cases remove rocks and drill reads expose
deposits. Neither asserts this full delivered-stock claim.

### T9 — Observe restored service after disruption

**After T7:** RG-PROTECT-002. Stage one damaged essential service, repairer and
materials; require native restoration and closure of its recovery deficit under
the governor. Reuse `recovery/service` for explicit native repair. Supplied-state
recovery tests cover unread service and work choice, but no sampled case joins
automatic choice to episode closure. Ordinary home repair depends on #4006.

### T10 — Carry one commitment through to its outcome

**Later:** RG-COMMIT-001. A policy/journal check with a supported short quest,
known home demand and enough crew should require affordable admission, retained
obligation while active, and release only after observed completion/failure.
QuestPolicyTests covers capacity and objective decisions; native hack/shuttle
fixtures cover prepared effects. The full obligation lifecycle is the added
claim. Remote travel requires separate native evidence.

### T11 — Read a blocker through the player view

**Alongside behavior diagnosis:** RG-EXPLAIN-001/002. Feed a blocked need through
projection and launcher rendering, replace its cause, then clear it. Require the
current subject/reason and cleared active blocker. A component test is cheapest;
one rendered inspection judges readability. E1–E3 trace the wiring and audit
typed causes but do not prove displayed transitions or comprehension.

### T12 — Preserve food and complete assisted feeding

**Before a campaign:** RG-FOOD-006/008. Reuse `refrigeration/build` for built
cooling and chilled meat. For storage movement, stage perishable food outside a
working cold store and require arrival at protective temperature. For assisted
feeding, separately stage a hungry baby, suitable food and caregiver without a
breastfeeder; require feeding and improved food need. Native acceptance is needed.
Diet/eligibility tests and the baby-food declaration establish usable supply,
without proving that care reaches the dependent.

## Scope

Implementation and new tests are follow-up investments. This task recovers and
audits requirements. It neither fixes runtime gaps nor authorizes campaign work.
Research goals are excluded throughout.
