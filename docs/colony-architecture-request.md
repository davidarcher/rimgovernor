Inspect the existing RimWorld AI codebase and improve the colony-management architecture so the system behaves like a robust game-playing agent rather than a collection of reactive LLM calls.

Important: we probably already have some of these mechanisms. Do not assume they are missing, and do not build duplicate parallel systems.

First inspect the current implementation, map each requested capability to what already exists, then:

* keep good existing mechanisms
* extend incomplete ones
* consolidate overlapping ones
* remove obsolete paths where safe
* implement missing pieces
* update tests and telemetry
* run the relevant test suite and fix regressions

Do not stop at an architecture report. Perform the implementation unless there is a genuine blocker.

The target architecture is roughly:

```text
RimWorld
   ↓
State / Event Adapter
   ↓
Derived World Model
   ↓
Deterministic Alerts + Prediction
   ↓
Domain Managers
   ↓
Proposal Bus
   ↓
Colony Administrator
   ↓
Deterministic Constraint / Resource Arbitration
   ↓
Persistent Goals / Plans
   ↓
Semantic Skills
   ↓
Validation + Execution
   ↓
Postcondition Verification
   ↺
```

There may also be side systems such as:

```text
BaseArchitect (VL)
CombatDirector
StrategicCritic
Playbook / Strategy Retrieval
Long-Term Memory
Replay / Evaluation Harness
```

Step 1: inspect the existing architecture

Trace the current control loop end to end.

Find and understand:

* game-state extraction
* derived/semantic state
* event processing
* manager scheduling
* all domain managers
* top-level administrator/arbitrator
* proposal models and queues
* task/goal tracking
* action/tool execution
* action status tracking
* failure handling
* cooldowns
* resource accounting
* work-priority logic
* construction planning
* combat behavior
* memory/state persistence
* model escalation
* prompts/tool schemas
* telemetry/logging
* replay/test infrastructure

Produce a short internal mapping of:

```text
capability -> existing implementation -> gap
```

Use that to drive the refactor.

Do not create a second implementation of something that already exists.

1. Derived world model

Managers should not repeatedly infer critical facts from raw RimWorld objects.

Inspect existing derived-state code and centralize/extend it as needed.

Useful derived facts include:

```text
foodRunwayDays
medicineRunway
powerHeadroom
batteryRunway
bedCapacity
medicalBedCapacity
freezerUtilization
storageUtilization
laborCapacity
workBacklogByType
constructionBacklog
expectedHarvestYield
expectedHarvestDate
raidReadiness
defenseCoverage
moodRisk
breakRisk
wealthTrend
resourceDeficits
```

Do not hard-code every possible metric into one giant object. Use the project's existing state/query patterns.

Derived metrics should be deterministic when possible.

Prefer giving managers actionable facts such as:

```text
food_runway = 4.2 days
next_harvest = 6.1 days
```

rather than forcing them to interpret:

```text
raw_food = 2317
```

2. Event-driven managers

Inspect whether managers currently poll continuously or already have triggers.

Move routine manager invocation toward meaningful state changes.

Examples:

```text
food runway falls below threshold
power reserve crosses threshold
pawn becomes incapacitated
new colonist joins
raid starts
major construction becomes blocked
research completes
season/winter deadline approaches
storage becomes saturated
```

Add hysteresis where thresholds would otherwise flap.

Example:

```text
food risk starts below 10 days
food risk clears above 14 days
```

Avoid invoking every manager every cycle unless there is a concrete reason.

3. Persistent goals

Add or strengthen explicit persistent goals.

Managers should work toward durable objectives rather than repeatedly generating isolated actions.

A goal should conceptually support:

```text
id
type
owner
priority
reason
target
deadline if applicable
status
progress
dependencies
createdAt
updatedAt
```

Example:

```text
Have at least 15 days of food before winter.
```

Possible statuses:

```text
proposed
active
blocked
suspended
completed
failed
cancelled
```

Do not force this exact schema if equivalent infrastructure already exists.

The administrator should be able to inspect, prioritize, suspend, resume, and retire goals.

4. Action / task lifecycle

Inspect how actions are currently tracked.

Every non-trivial action should have an explicit lifecycle rather than disappearing after tool invocation.

Support concepts equivalent to:

```text
planned
accepted
started
blocked
completed
failed
cancelled
```

Skills/actions should expose, where useful:

```text
preconditions
estimated resource cost
expected result
expected duration
postconditions
failure conditions
timeout/staleness policy
```

Example:

```text
expand_freezer

preconditions:
  construction material available
  planned region valid

expected:
  freezer capacity increases

failure:
  construction blocked
  region invalidated
```

This must prevent repeated duplicate orders caused by managers failing to realize that work is already underway.

5. Postcondition verification

After meaningful execution, verify whether the expected effect actually occurred.

The system should distinguish:

```text
command issued successfully
```

from:

```text
goal actually achieved
```

Examples:

```text
build order placed but construction never started
pawn assignment made but pawn became incapacitated
farm expanded but crop cannot grow due to season
```

Failures should feed back into goals/plans instead of blindly retrying the same action.

6. Interrupt and priority model

Implement or refine explicit urgency classes.

Something similar to:

```text
P0 survival emergency
P1 combat / immediate medical emergency
P2 imminent colony risk
P3 strategic work
P4 comfort / optimization
```

High-priority events should suspend lower-priority work without destroying the underlying goals.

When the emergency ends, appropriate suspended work should resume.

Avoid hard-coding every priority decision. The administrator can still make tradeoffs within policy bounds.

7. Commitment and anti-thrashing

Inspect current cooldown/debounce behavior.

LLM managers should not continuously reverse accepted decisions.

Once work is accepted, retain it for a reasonable commitment period unless:

```text
the assumptions changed
a higher-priority emergency occurred
the action became impossible
the cost materially changed
the expected value materially dropped
```

Use existing game-time abstractions.

Do not create arbitrary real-time sleeps or timers.

8. Deterministic resource arbitration

Managers should be allowed to propose competing uses of limited resources.

The LLM administrator should not be solely responsible for arithmetic or enforcing hard budgets.

Proposals should expose costs such as:

```text
steel
wood
components
medicine
silver
labor
power
space
```

Then deterministic code should prevent impossible combinations.

At minimum:

```text
total allocated resources <= available resources - required reserves
```

Start simple if necessary.

A greedy priority/utility allocator is fine if the current architecture does not justify a full optimizer.

The important property is that five managers cannot all independently spend the same 300 steel.

9. Work / colonist assignment optimization

Inspect current pawn work-priority logic.

Where exact pawn/job allocation is currently delegated entirely to an LLM, move the mechanical part toward deterministic scoring/optimization.

Inputs may include:

```text
skill
passion
health
movement
work restrictions
required coverage
current backlog
travel distance
role criticality
```

The LLM should preferably set policy:

```text
construction is temporarily high priority
always preserve doctor coverage
do not exhaust the only cook
```

Then deterministic code chooses exact pawn assignments.

Do not rewrite this if a good optimizer already exists.

10. Prediction / forward-looking state

Add lightweight prediction where the required inputs are available.

Examples:

```text
food exhaustion ETA
battery depletion ETA
expected harvest
construction completion estimate
season deadline
storage saturation
likely labor deficit
```

Do not pretend predictions are exact.

Expose uncertainty where useful.

Simple deterministic projections are preferable to asking the LLM to perform arithmetic repeatedly.

11. Uncertainty and confidence

Inspect existing proposal schemas.

Allow managers to communicate uncertainty where it improves arbitration.

Conceptually:

```json
{
  "confidence": 0.58,
  "uncertainties": [
    "harvest estimate may be wrong",
    "raid approach direction unknown"
  ]
}
```

Do not blindly trust self-reported LLM confidence as calibrated probability.

Use it as one signal together with deterministic conditions.

Uncertainty can trigger:

```text
additional observation
plan review
larger-model escalation
```

12. Strategic playbooks

Inspect whether RimWorld-specific strategy knowledge already exists.

Create or improve a reusable playbook/strategy layer for recurring situations such as:

```text
winter preparation
heat wave
cold snap
toxic fallout
food collapse
power crisis
disease outbreak
manhunter pack
siege
mech cluster
major casualty event
mental-break cascade
post-raid recovery
```

Do not encode every response as rigid automation.

The goal is to provide domain expertise that a small model can adapt to the current colony.

Prefer retrieval of the relevant playbook over stuffing every strategy into every prompt.

13. Semantic skill layer

Inspect the current action API.

Move manager-facing actions toward semantic skills where possible.

Prefer:

```text
stabilize_food_supply
prepare_for_winter
expand_power_capacity
recover_after_raid
establish_defensive_position
integrate_new_colonist
```

over forcing high-level models to orchestrate dozens of tiny UI/game operations.

Skills may decompose into smaller existing tools.

Preserve the low-level tools where needed for execution, but avoid exposing unnecessary mechanics to every manager.

14. Combat as a separate regime

Inspect combat behavior carefully.

Do not force routine colony administration and active tactical combat through the exact same slow planning loop.

Prefer a split like:

```text
Colony Administrator
   ↓
strategic combat decision

CombatDirector
   ↓
fast tactical decisions

validated combat skills
   ↓
pawn commands
```

The administrator should make decisions like:

```text
fight
retreat
shelter civilians
activate fallback position
```

Lower-level combat logic should handle exact tactical execution.

Reuse existing combat code if it already does this.

15. Logistics / travel cost

Make hauling and travel cost visible to relevant systems.

Where cheap enough, expose estimates for common chains such as:

```text
farm -> freezer
storage -> workbench
workbench -> output storage
bedrooms -> dining
hospital access
```

The BaseArchitect, ConstructionManager, and work-assignment system should be able to use these costs.

Avoid expensive all-pairs pathfinding every AI tick.

Cache or approximate where appropriate.

16. RimWorld wealth pressure

Make sure strategic/economy logic understands that additional wealth is not always beneficial.

Expose wealth growth and, where practical, threat implications as strategic context.

The AI should be able to prefer:

```text
do not acquire/build this yet
```

when the benefit is low and the wealth/risk cost is high.

Do not create fake precision around RimWorld raid calculations if the codebase does not have reliable access to them.

17. Strategic critic / escalation

Inspect current use of larger models.

If a larger model is available, avoid using it for routine work.

Use it for cases like:

```text
novel crisis
multiple high-priority conflicts
repeated failed plans
colony health degrading despite apparently successful managers
major strategic transition
low-confidence administrator decision
```

Also support occasional strategic review.

Conceptually:

```text
Here is the current colony state, active goals,
current BasePlan, and recent significant decisions.
Identify systemic problems or bad strategic direction.
```

Do not invoke this constantly.

18. Evaluation / replay harness

This is a high-priority engineering feature.

Inspect what logging/replay/testing infrastructure already exists.

Record enough information around decisions to reproduce and evaluate them:

```text
game state snapshot or relevant normalized state
derived state
events
active goals
manager inputs
manager proposals
administrator decision
accepted/rejected proposals
executed skills
outcome/postconditions
```

Do not serialize the entire game world every decision if that is prohibitively expensive.

Build on existing save/snapshot infrastructure when possible.

The goal is to support repeatable scenario evaluation.

Create or extend regression scenarios such as:

```text
early food shortage
winter preparation
power collapse
raid during major construction
medical emergency
new colonist arrival
blocked freezer expansion
storage saturation
base expansion
wealth spike
multiple managers competing for steel
failed construction
```

Where practical, make these executable tests rather than prompt examples.

19. Telemetry

Add useful diagnostics at decision boundaries.

Examples:

```text
event triggered manager
manager invoked
manager skipped
proposal generated
proposal rejected and reason
goal created/updated/completed
action accepted
action blocked
action timed out
postcondition failed
resource arbitration conflict
priority interrupt
goal suspended/resumed
larger-model escalation
playbook selected
```

Avoid per-tick or per-cell noise.

Make logs useful for understanding why the AI made a bad decision.

20. Prompt simplification

After moving deterministic facts, state, arithmetic, lifecycle, and constraints into code, review manager prompts.

Remove responsibilities the model no longer needs.

Managers should receive compact, actionable context:

```text
relevant derived state
active goals
relevant alerts
current commitments
available semantic skills
relevant playbook
```

Do not repeatedly pass giant raw-state dumps when a smaller representation is sufficient.

Models should primarily perform:

```text
judgment
prioritization
tradeoffs
strategy adaptation
```

rather than:

```text
bookkeeping
arithmetic
state reconstruction
exact geometry
hard constraint enforcement
```

Implementation priorities

Do not attempt a destructive rewrite of everything at once.

After inspection, prioritize work roughly in this order, adjusting based on what already exists:

1. persistent goals and action lifecycle
2. derived-state metrics and prediction
3. deterministic resource/conflict arbitration
4. event-driven triggers and interrupt behavior
5. postcondition verification
6. anti-thrashing / commitment
7. work assignment optimization
8. playbooks and semantic skills
9. combat specialization
10. strategic escalation/critic
11. replay/evaluation improvements

If the first several items are already robust, move directly to the largest actual gaps.

Acceptance behavior

The finished system should be able to handle a scenario like:

```text
- Colony has 7 days of food.
- Harvest is 10 days away.
- Defense manager wants 250 steel.
- Construction manager wants 180 steel.
- Only 300 steel is safely spendable.
- Winter is approaching.
- A pawn becomes seriously injured.
```

Expected architectural behavior:

1. Derived state recognizes the food deficit and winter timing.
2. Food manager is triggered without polling every manager.
3. Existing food goal is created or updated rather than duplicated.
4. Defense and construction proposals expose resource costs.
5. Resource arbitration prevents both from spending the same steel.
6. Medical emergency interrupts lower-priority work.
7. Existing strategic goals are suspended rather than deleted.
8. Medical action is tracked until its postconditions are satisfied or it fails.
9. Suspended work resumes when appropriate.
10. The system does not repeatedly issue duplicate construction or food orders.
11. A larger model is not required unless the situation becomes genuinely ambiguous or repeatedly fails.

Engineering constraints

Do not:

* replace deterministic working systems with LLM decisions
* duplicate existing goal/event/task infrastructure
* create independent manager resource accounting
* make every state change trigger every manager
* let models bypass hard game constraints
* trust model arithmetic for resource budgets
* treat issuing a command as equivalent to completing a task
* add large-model calls to routine paths without evidence they are needed
* introduce giant prompts as a substitute for proper state modeling

Do:

* inspect first
* reuse existing abstractions
* centralize derived facts
* persist strategic intent
* make execution observable
* verify outcomes
* use deterministic code for constraints and optimization
* use small models for judgment
* make the architecture replayable and testable

Implement the changes that materially improve the current codebase. If an item is already well implemented, document that in code comments or the final change summary only if useful, and move on rather than rewriting it.
