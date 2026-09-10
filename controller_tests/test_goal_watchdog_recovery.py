from copy import deepcopy

import pytest

from rimbot.colony_plan import ColonyPlan, Failure
from test_colony_controller import Replay


async def held_work():
    rt = Replay()
    await rt.controller.cycle()
    goal = rt.current_plan.colony_goals['EnsureWorkAssignments']
    first = rt.current_plan.progress[goal.steps[0]]
    first.state = 'waiting'
    first.issued['0'] = {'confirmed': True, 'receipt': {'target': 'Thing_Human0'}}
    await rt.controller.cycle()
    rt.facts['tick'] += rt.controller.policy.blocked_after_ticks + 1
    await rt.controller.cycle()
    assert goal.status == 'blocked'
    assert 'watchdog' in goal.evidence
    return rt


async def test_delayed_work_resumes_same_dependency_chain_after_persisted_watchdog_hold():
    rt = await held_work()
    plan = rt.current_plan
    goal = plan.colony_goals['EnsureWorkAssignments']
    identities = list(goal.steps)
    methods = deepcopy(goal.evidence['methods'])
    receipts = deepcopy(plan.progress[identities[0]].issued)
    rt.current_plan = ColonyPlan.model_validate_json(plan.model_dump_json())
    goal = rt.current_plan.colony_goals['EnsureWorkAssignments']
    # Simulated native work finishes although further controller orders are held.
    rt.labor()
    await rt.controller.cycle()
    assert goal.status == 'active'
    assert goal.steps == identities
    assert goal.evidence['methods'] == methods
    assert rt.current_plan.progress[identities[0]].issued == receipts
    assert [s.id for s in rt.current_plan.ready() if s.goal_id == 'EnsureWorkAssignments'] == [identities[1]]
    assert 'watchdog' not in goal.evidence
    rt.labor()
    await rt.controller.cycle()
    rt.labor()
    await rt.controller.cycle()
    assert goal.status == 'complete'
    assert goal.steps == identities
    assert all(rt.current_plan.progress[s].state == 'complete' for s in identities)


async def test_delayed_shell_completion_can_continue_to_functional_sleeping_goal():
    rt = Replay()
    for _ in range(5):
        await rt.controller.cycle()
        goal = rt.current_plan.colony_goals.get('EnsureInitialShelter')
        if goal and goal.steps:
            break
        rt.labor()
    shell_id = goal.steps[0]
    assert rt.current_plan.spec.steps[-1].action.kind == 'build_room_shell'
    rt.current_plan.progress[shell_id].state = 'waiting'
    rt.current_plan.progress[shell_id].issued['0'] = {'confirmed': True}
    await rt.controller.cycle()
    rt.facts['tick'] += rt.controller.policy.blocked_after_ticks + 1
    await rt.controller.cycle()
    assert goal.status == 'blocked' and 'watchdog' in goal.evidence
    rt.labor()
    await rt.controller.cycle()
    assert goal.status == 'active'
    assert rt.current_plan.progress[shell_id].state == 'complete'
    assert len(goal.steps) == 2
    sleeping = next(s for s in rt.current_plan.spec.steps if s.id == goal.steps[-1])
    assert sleeping.action.kind == 'place_buildings'
    assert all(p.def_name == 'SleepingSpot' for p in sleeping.action.placements)
    await rt.controller.cycle()
    assert len(goal.steps) == 2


@pytest.mark.parametrize('condition', ['unchanged', 'stock_changed', 'rewind', 'cancelled_goal',
                                      'failed_step', 'cancelled_step', 'different_blocker', 'manual'])
async def test_watchdog_does_not_release_without_safe_new_completion(condition):
    rt = await held_work()
    plan = rt.current_plan
    goal = plan.colony_goals['EnsureWorkAssignments']
    if condition not in ('unchanged', 'stock_changed'):
        plan.progress[goal.steps[0]].state = 'complete'
    if condition == 'stock_changed':
        rt.facts['resources']['WoodLog'] += 100
    if condition == 'rewind':
        rt.facts['tick'] = goal.evidence['watchdog']['tick'] - 1
    if condition == 'cancelled_goal':
        goal.cancelled = True
    if condition == 'failed_step':
        plan.progress[goal.steps[1]].state = 'blocked'
        plan.progress[goal.steps[1]].failure = Failure(code='native_failure', detail='Unknown write')
    if condition == 'cancelled_step':
        plan.progress[goal.steps[1]].state = 'cancelled'
    if condition == 'different_blocker':
        goal.reason = 'Player construction needs attention'
    if condition == 'manual':
        rt.mode = 'manual'
    await rt.controller.cycle()
    assert goal.status == 'blocked'
    assert 'watchdog' in goal.evidence
    assert not any(s.goal_id == 'EnsureWorkAssignments' for s in plan.ready())


async def test_emergency_still_suspends_goal_released_by_observed_completion():
    rt = await held_work()
    goal = rt.current_plan.colony_goals['EnsureWorkAssignments']
    rt.current_plan.progress[goal.steps[0]].state = 'complete'
    rt.batch.summary.hostile_count = 2
    await rt.controller.cycle()
    assert goal.status == 'suspended'
    assert not list(rt.current_plan.ready())
