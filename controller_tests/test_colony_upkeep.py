from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.colony_plan import ColonyGoal, ColonyPlan, PlanSpec, PlanStep, StepProgress
from rimbot.colony_skills import SkillBlocked
from rimbot.colony_upkeep import evidence, upkeep_nodes, upkeep_method, reconcile_upkeep


def facts():
    return {'tick': 10, 'colonists': 0, 'resources': {}, 'upkeep': {'version': 1, 'tick': 10, 'errors': {},
        'items': [], 'structures': [], 'fires': [], 'filth': [], 'storageCells': [], 'beds': [], 'people': [], 'animals': []}}


def item(**changes):
    return dict(dict(id='Thing_Medicine1', roofed=False, inStorage=False, forbidden=False,
                deteriorationRate=1, count=10, medicine=True), **changes)


def runtime(action='haul'):
    step = PlanStep(id='upkeep', title='Upkeep', goal_id='SecureSupplies', completion_criteria='Native protected target', action={
        'kind': 'native_operation', 'tool': 'home/order', 'completion': 'upkeep_target',
        'arguments': dict(action=action, pawn='Thing_Human1', target='Thing_Medicine1', dryRun=False)})
    plan = ColonyPlan(spec=PlanSpec(steps=[step]), progress={'upkeep': StepProgress(state='waiting',
        issued={'0': dict(confirmed=True, load_token='colony:1:load', player_direction=0)})},
        colony_goals={'SecureSupplies': ColonyGoal(priority_class=3, steps=['upkeep'],
            evidence={'upkeep_orders': {'upkeep': item()}})})
    return SimpleNamespace(current_plan=plan, context_token='colony:1:load', chat_revision=0)


def test_unknown_does_not_clear_existing_risk_or_invent_emergency():
    f, control = facts(), {}
    assert upkeep_nodes(f, control) == []
    f['upkeep']['fires'] = [{'id': 'Thing_Fire1', 'home': True, 'size': .2}]
    assert ('MaintainFireSafety', 1) in upkeep_nodes(f, control)
    f['upkeep']['fires'] = None
    assert ('MaintainFireSafety', 1) in upkeep_nodes(f, control)
    assert ('MaintainFireSafety', 4) in upkeep_nodes(facts() | {'upkeep': {}}, {})


def test_failed_and_stale_sections_cannot_prove_recovery():
    f = facts()
    f['upkeep']['errors']['items'] = 'Native exception'
    assert evidence(f)['vulnerable'] is None
    f['upkeep']['tick'] = 9
    assert all(v is None for v in evidence(f).values())


def test_roofed_supplies_outside_valid_storage_remain_a_deficit():
    f = facts()
    f['upkeep']['items'] = [item(roofed=True, inStorage=False, deteriorationRate=0, baseDeteriorationRate=6)]
    assert evidence(f)['vulnerable']
    f['upkeep']['items'][0]['inStorage'] = True
    assert evidence(f)['vulnerable'] == []


def test_forbidden_items_and_non_home_filth_are_preserved():
    f = facts()
    r = item(); r['forbidden'] = True
    f['upkeep']['items'] = [r]
    f['upkeep']['filth'] = [{'id': 'Thing_Filth1', 'home': False}]
    assert evidence(f)['vulnerable'] == []
    assert evidence(f)['filth'] == []


def test_haul_receipt_does_not_complete_until_native_protected_storage():
    rt, f = runtime(), facts()
    f['upkeep']['items'] = [item()]
    reconcile_upkeep(rt, f)
    assert rt.current_plan.progress['upkeep'].state == 'waiting'
    f['upkeep']['items'][0].update(roofed=True, inStorage=True)
    reconcile_upkeep(rt, f)
    assert rt.current_plan.progress['upkeep'].state == 'complete'


def test_missing_haul_target_is_not_success():
    rt = runtime()
    reconcile_upkeep(rt, facts())
    assert rt.current_plan.progress['upkeep'].failure.code == 'upkeep_target_missing'


@pytest.mark.parametrize('change', ['load', 'direction'])
def test_changed_ownership_blocks_existing_order(change):
    rt, f = runtime(), facts()
    if change == 'load': rt.context_token = 'new-load'
    else: rt.current_plan.control['player_direction'] = 1
    reconcile_upkeep(rt, f)
    assert rt.current_plan.progress['upkeep'].failure.code == 'upkeep_ownership_changed'


def test_clean_home_removal_is_not_completion():
    rt, f = runtime('clean'), facts()
    f['upkeep']['filth'] = [{'id': 'Thing_Medicine1', 'home': False}]
    reconcile_upkeep(rt, f)
    assert rt.current_plan.progress['upkeep'].state == 'waiting'
    f['upkeep']['filth'] = []
    reconcile_upkeep(rt, f)
    assert rt.current_plan.progress['upkeep'].state == 'complete'


@pytest.mark.asyncio
async def test_unavailable_workers_do_not_change_priorities():
    rt, f = runtime(), facts()
    rt.inspect_native = AsyncMock()
    f['upkeep']['items'] = [item()]
    upkeep_nodes(f, rt.current_plan.control)
    with pytest.raises(SkillBlocked, match='No available enabled worker'):
        await upkeep_method(rt, 'SecureSupplies', f, [])
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_unsafe_fire_holds_without_native_write():
    rt, f = runtime(), facts()
    rt.current_plan.colony_goals['MaintainFireSafety'] = ColonyGoal(priority_class=1)
    rt.inspect_native = AsyncMock()
    f['upkeep']['fires'] = [dict(id='Thing_Fire1', home=True, size=1.5)]
    upkeep_nodes(f, rt.current_plan.control)
    with pytest.raises(SkillBlocked, match='exceeds bounded'):
        await upkeep_method(rt, 'MaintainFireSafety', f, [])
    rt.inspect_native.assert_not_awaited()


def test_paired_state_roundtrip_retains_ownership_and_pending_order():
    rt = runtime()
    rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    reconcile_upkeep(rt, facts() | {'upkeep': {}})
    assert rt.current_plan.progress['upkeep'].state == 'waiting'
    assert len(rt.current_plan.spec.steps) == 1


def test_partial_stack_delivery_does_not_certify_whole_target():
    rt, f = runtime(), facts()
    r = item(); r.update(roofed=True, inStorage=True, count=2)
    f['upkeep']['items'] = [r]
    reconcile_upkeep(rt, f)
    assert rt.current_plan.progress['upkeep'].state == 'waiting'


def test_stockpile_without_protection_does_not_clear_supply_deficit():
    f = facts()
    r = item(); r['inStorage'] = True
    f['upkeep']['items'] = [r]
    assert ('SecureSupplies', 3) in upkeep_nodes(f, {})


@pytest.mark.asyncio
async def test_safe_haul_compiles_one_guarded_shared_action():
    rt, f = runtime(), facts()
    f['upkeep']['items'] = [item()]
    f['upkeep']['storageCells'] = [dict(x=10, z=12, roofed=True)]
    upkeep_nodes(f, rt.current_plan.control)
    rt.inspect_native = AsyncMock(return_value=dict(success=True, diagnostics={'checks': {
        'betterStorageFound': True, 'storageCell': {'x': 10, 'z': 12}}}))
    people = [dict(thingId='Thing_Human1', health=dict(needsTend=False, bleeding=False),
        work={'types': [dict(name='Hauling', disabled=False, priority=1)]})]
    method, actions = await upkeep_method(rt, 'SecureSupplies', f, people)
    assert len(actions) == 1 and actions[0]['completion'] == 'upkeep_target'
    assert actions[0]['arguments']['requireSafeStorage'] is True
    assert actions[0]['arguments']['draft'] is False
    rt.current_plan.colony_goals['SecureSupplies'].evidence['methods'] = {method: ['upkeep']}
    assert await upkeep_method(rt, 'SecureSupplies', f, people) is None
    assert rt.inspect_native.await_count == 1


@pytest.mark.asyncio
async def test_controller_fire_preempts_development_and_recovery_reuses_goal():
    from test_colony_controller import Replay
    rt = Replay()
    rt.facts['upkeep'] = facts()['upkeep'] | {'tick': rt.facts['tick'], 'fires': [dict(id='Thing_Fire1', home=True, size=2)]}
    await rt.controller.cycle()
    fire = rt.current_plan.colony_goals['MaintainFireSafety']
    assert fire.status == 'blocked' and rt.current_plan.control['execution_hold']
    assert all(g.status == 'suspended' for k, g in rt.current_plan.colony_goals.items()
               if g.priority_class > 1)
    rt.facts['upkeep']['fires'] = []
    await rt.controller.cycle()
    assert fire.status == 'complete'
    rt.facts['upkeep']['fires'] = [dict(id='Thing_Fire2', home=True, size=2)]
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['MaintainFireSafety'] is fire and fire.status == 'blocked'


@pytest.mark.asyncio
async def test_native_worker_refusal_checks_an_alternate_without_writing():
    from mcp.types import CallToolResult
    from rimbot.bridge import BridgeError
    rt, f = runtime(), facts()
    f['upkeep']['items'] = [item()]
    f['upkeep']['storageCells'] = [dict(x=10, z=12, roofed=True)]
    upkeep_nodes(f, rt.current_plan.control)
    rt.inspect_native = AsyncMock(side_effect=[
        BridgeError('home/order', CallToolResult(content=[], isError=True,
            structuredContent={'errorKind': 'job_refused', 'error': 'Reserved by another pawn'})),
        dict(success=True, diagnostics={'checks': {'betterStorageFound': True, 'storageCell': {'x': 10, 'z': 12}}})])
    people = [dict(thingId='Thing_Human'+str(i), health=dict(needsTend=False, bleeding=False),
        work={'types': [dict(name='Hauling', disabled=False, priority=1)]}) for i in (1, 2)]
    _, actions = await upkeep_method(rt, 'SecureSupplies', f, people)
    assert actions[0]['arguments']['pawn'] == 'Thing_Human2'
    assert all(call.args[1]['dryRun'] for call in rt.inspect_native.await_args_list)


@pytest.mark.asyncio
async def test_worsening_rot_does_not_reset_upkeep_watchdog():
    from test_colony_controller import Replay
    rt = Replay()
    fixture = runtime()
    rt.current_plan = fixture.current_plan
    step = rt.current_plan.spec.steps[0]
    step.source = 'AUTOPILOT'
    goal = rt.current_plan.colony_goals['SecureSupplies']
    goal.evidence['remaining_work'] = 10
    goal.last_progress_tick = 100
    rt.facts['tick'] = 60200
    r = item(); r['rotTicks'] = 1
    rt.facts['upkeep'] = facts()['upkeep'] | {'tick': 60200, 'items': [r]}
    # Reach the upkeep node after the controller's one-commit-per-review work.
    for _ in range(8):
        await rt.controller.cycle()
        if goal.status == 'blocked': break
    assert goal.status == 'blocked' and 'No measurable progress' in goal.reason


def test_recurrent_maintenance_retires_only_verified_completed_order():
    from rimbot.colony_plan import CommitSteps
    from rimbot.config import ModelRole
    rt = runtime('repair')
    original = rt.current_plan.spec.steps[0]
    original.source = 'AUTOPILOT'
    next_step = original.model_copy(update={'id': 'repair-again'})
    proposal = CommitSteps(expected_revision=0, reason='Observed new damage', steps=[next_step])
    with pytest.raises(ValueError, match='identical intent'):
        rt.current_plan.commit(proposal.decision(rt.current_plan), actor=ModelRole.STRATEGIST, tick=100)
    rt.current_plan.progress['upkeep'].state = 'complete'
    rt.current_plan.commit(proposal.decision(rt.current_plan), actor=ModelRole.STRATEGIST, tick=100)
    assert [s.id for s in rt.current_plan.spec.steps] == ['repair-again']
    assert rt.current_plan.progress['repair-again'].state == 'pending'


@pytest.mark.asyncio
async def test_small_fire_waits_for_normal_native_workers_without_forced_order():
    rt, f = runtime(), facts()
    rt.current_plan.colony_goals['MaintainFireSafety'] = ColonyGoal(priority_class=1)
    rt.inspect_native = AsyncMock()
    f['upkeep']['fires'] = [dict(id='Thing_Fire1', home=True, size=.2, safeWorkers=['Thing_Human1'])]
    upkeep_nodes(f, rt.current_plan.control)
    people = [dict(thingId='Thing_Human1', health=dict(needsTend=False, bleeding=False),
        work={'types': [dict(name='Firefighter', disabled=False, priority=1)]})]
    assert await upkeep_method(rt, 'MaintainFireSafety', f, people) is None
    assert rt.current_plan.colony_goals['MaintainFireSafety'].evidence['waiting_for_native_fire']
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_controller_supervises_ordinary_fire_work_and_holds_when_it_stalls():
    from test_colony_controller import Replay
    rt = Replay()
    rt.people[0]['health'].update(needsTend=False, bleeding=False)
    rt.people[0]['work']['types'].append(dict(name='Firefighter', disabled=False, priority=1, priorityStored=1))
    rt.facts['upkeep'] = facts()['upkeep'] | {'tick': rt.facts['tick'], 'fires': [dict(
        id='Thing_Fire1', home=True, size=.2, safeWorkers=['Thing_Human0'])]}
    await rt.controller.cycle()
    goal = rt.current_plan.colony_goals['MaintainFireSafety']
    assert goal.status == 'active' and not goal.steps
    assert goal.evidence['waiting_for_native_fire'] and rt.current_plan.control['simulation_needed']
    rt.facts['tick'] += rt.controller.policy.blocked_after_ticks + 1
    rt.facts['upkeep']['tick'] = rt.facts['tick']
    rt.facts['upkeep']['fires'][0]['size'] = .3
    await rt.controller.cycle()
    assert goal.status == 'blocked' and goal.evidence.get('watchdog')
    assert rt.current_plan.control.get('execution_hold')
def test_retry_inputs_ignore_simulation_noise_but_preserve_eligibility_changes():
    from copy import deepcopy
    from rimbot.colony_upkeep import retry_signature
    state = dict(known=True, targets=[dict(id='Thing_Item1', rotTicks=1000, temperature=20, count=50)])
    people = [dict(thingId='Thing_Human1', position=dict(x=1, z=1), health=dict(needsTend=False, bleeding=False))]
    baseline = retry_signature(state, people, {}, {})
    state['targets'][0].update(rotTicks=999, temperature=20.1, count=49)
    people[0]['position']['x'] = 2
    assert retry_signature(state, people, {}, {}) == baseline
    changed = deepcopy(people)
    changed[0]['health']['needsTend'] = True
    assert retry_signature(state, changed, {}, {}) != baseline
    state['targets'][0]['forbidden'] = True
    assert retry_signature(state, people, {}, {}) != baseline
