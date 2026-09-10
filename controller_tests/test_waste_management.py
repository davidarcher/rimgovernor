from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

import pytest
from pydantic import ValidationError

from rimbot.colony_plan import ColonyPlan, ColonyGoal, NativeOperation, Failure
from rimbot.player_commands import CreateGoal
from rimbot.waste_management import outcome, pending_items, compile_method


def action():
    return NativeOperation(tool='home/manage_waste', arguments={'thingId': 'Thing_Corpse1', 'pawn': 'Thing_Pawn1'}, completion='waste_contained')


def test_receipts_cannot_complete_waste_work():
    with pytest.raises(ValidationError):
        NativeOperation(tool='home/manage_waste', arguments={'thingId': 'Thing_Corpse1', 'pawn': 'Thing_Pawn1'})
    with pytest.raises(ValidationError):
        NativeOperation(tool='home/order', arguments={}, completion='waste_contained')


def test_waste_deficit_enters_shared_development_admission():
    from rimbot.development_priorities import deficit
    from rimbot.colony_policy import ColonyPolicy
    goal = ColonyGoal(priority_class=3)
    assert deficit('MaintainWaste', goal, {}, ColonyPolicy()) is None
    goal.evidence['observation'] = {'success': True, 'items': []}
    assert deficit('MaintainWaste', goal, {}, ColonyPolicy()) == 0
    goal.evidence['observation']['items'] = [{'eligible': True, 'state': 'exposed'}]
    assert deficit('MaintainWaste', goal, {}, ColonyPolicy()) == 1


@pytest.mark.asyncio
async def test_native_deficit_creates_maintained_goal_and_cancellation_suppresses_it():
    from test_colony_controller import Replay
    rt = Replay()
    rt.facts['waste'] = {'success': True, 'items': [{'thingId': 'Thing_Corpse1', 'eligible': True, 'state': 'exposed'}]}
    await rt.controller.cycle()
    goal = rt.current_plan.colony_goals['MaintainWaste']
    assert goal.priority_class == 3 and goal.source == 'AUTOPILOT'
    goal.cancelled = True
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['MaintainWaste'] is goal
    assert goal.cancelled


@pytest.mark.parametrize('state', ['relocated', 'buried'])
def test_exact_containment_is_verified(state):
    assert outcome(action(), {}, {'success': True, 'items': [{'thingId': 'Thing_Corpse1', 'state': state}]}, []) == 'complete'


def test_missing_or_other_identity_is_never_destruction():
    for rows in ([], [{'thingId': 'Thing_Corpse2', 'state': 'buried'}]):
        assert isinstance(outcome(action(), {}, {'success': True, 'items': rows}, []), Failure)


def test_carried_is_waiting_not_completed():
    assert outcome(action(), {}, {'success': True, 'items': []},
                   [{'thingId': 'Thing_Pawn1', 'carriedThingId': 'Thing_Corpse1'}]) == 'waiting'


def test_unknown_is_not_clear():
    assert pending_items({}) is None
    assert pending_items({'success': True, 'items': []}) == []
    assert pending_items({'success': True, 'items': [{'eligible': False, 'state': 'exposed'}]}) == []
    assert outcome(action(), {}, {}, []) == 'waiting'


def test_burial_policy_cannot_complete_from_relocation():
    order = action()
    order.arguments['bury'] = 'Thing_Corpse1'
    assert isinstance(outcome(order, {}, {'success': True, 'items': [
        {'thingId': 'Thing_Corpse1', 'state': 'relocated'}]}, []), Failure)
    assert outcome(order, {}, {'success': True, 'items': [
        {'thingId': 'Thing_Corpse1', 'state': 'buried'}]}, []) == 'complete'


def test_unwanted_is_explicit_exact_policy():
    assert CreateGoal(kind='CreateGoal', goal='MaintainWaste').unwanted == []
    for goal, ids in [('MaintainWood', ['Thing_Wood1']), ('MaintainWaste', ['all corpses']), ('MaintainWaste', ['Thing_1,Thing_2'])]:
        with pytest.raises(ValidationError):
            CreateGoal(kind='CreateGoal', goal=goal, unwanted=ids)


@pytest.mark.asyncio
async def test_native_refusal_never_compiles_write():
    from rimbot.colony_skills import SkillBlocked
    plan = ColonyPlan()
    plan.colony_goals['MaintainWaste'] = ColonyGoal(priority_class=3)
    plan.control['waste'] = {'success': True, 'items': [{'thingId': 'Thing_Corpse1', 'eligible': True, 'state': 'exposed'}]}
    rt = SimpleNamespace(current_plan=plan, batch=SimpleNamespace(native={'pawns': {'pawns': [{'thingId': 'Thing_Pawn1'}]}}),
                         inspect_native=AsyncMock(return_value={'success': False, 'accepted': False}))
    with pytest.raises(SkillBlocked):
        await compile_method(rt, plan.colony_goals['MaintainWaste'])
    assert rt.inspect_native.await_args.args[1]['dryRun'] is True


@pytest.mark.asyncio
async def test_read_load_race_discards_observation():
    from rimbot.waste_management import refresh
    plan = ColonyPlan()
    plan.colony_goals['MaintainWaste'] = ColonyGoal(priority_class=3)
    rt = SimpleNamespace(current_plan=plan, context_token='before', chat_revision=1,
        game=SimpleNamespace(query=AsyncMock(return_value={'success': True, 'tick': 50, 'items': []})),
        sync_identity=AsyncMock())
    async def load_changed():
        rt.context_token = 'after'
    rt.sync_identity.side_effect = load_changed
    await refresh(rt)
    assert 'waste' not in plan.control


@pytest.mark.asyncio
async def test_candidate_selection_is_bounded_and_previews_only():
    from rimbot.colony_skills import SkillBlocked
    plan = ColonyPlan()
    goal = ColonyGoal(priority_class=3)
    plan.colony_goals['MaintainWaste'] = goal
    plan.control['waste'] = {'success': True, 'items': [
        {'thingId': f'Thing_Corpse{i}', 'eligible': True, 'state': 'exposed'} for i in range(20)]}
    rt = SimpleNamespace(current_plan=plan, batch=SimpleNamespace(native={'pawns': {'pawns': [
        {'thingId': f'Thing_Pawn{i}'} for i in range(20)]}}),
        inspect_native=AsyncMock(return_value={'success': False}))
    with pytest.raises(SkillBlocked):
        await compile_method(rt, goal)
    assert rt.inspect_native.await_count == 8


@pytest.mark.asyncio
async def test_refused_target_cannot_starve_later_accessible_waste():
    from rimbot.colony_skills import SkillBlocked
    plan = ColonyPlan()
    goal = ColonyGoal(priority_class=3)
    plan.colony_goals['MaintainWaste'] = goal
    plan.control['waste'] = {'success': True, 'items': [
        {'thingId': identity, 'eligible': True, 'state': 'exposed'} for identity in ('Thing_A', 'Thing_B')]}
    async def preview(_, args):
        return {'success': True, 'accepted': args['thingId'] == 'Thing_B'}
    rt = SimpleNamespace(current_plan=plan, batch=SimpleNamespace(native={'pawns': {'pawns': [
        {'thingId': f'Thing_Pawn{i}'} for i in range(8)]}}), inspect_native=preview)
    with pytest.raises(SkillBlocked):
        await compile_method(rt, goal)
    _, actions = await compile_method(rt, goal)
    assert actions[0]['arguments']['thingId'] == 'Thing_B'


@pytest.mark.asyncio
async def test_accepted_method_waits_for_containment_and_keeps_exact_policy():
    plan = ColonyPlan()
    goal = ColonyGoal(priority_class=3, target={'unwanted': ['Thing_Wood1']})
    plan.colony_goals['MaintainWaste'] = goal
    plan.control['waste'] = {'success': True, 'items': [
        {'thingId': 'Thing_Wood1', 'eligible': True, 'state': 'exposed'}]}
    rt = SimpleNamespace(current_plan=plan, batch=SimpleNamespace(native={'pawns': {'pawns': [{'thingId': 'Thing_Pawn1'}]}}),
        inspect_native=AsyncMock(return_value={'success': True, 'accepted': True}))
    _, actions = await compile_method(rt, goal)
    value = NativeOperation.model_validate(actions[0])
    assert value.completion == 'waste_contained'
    assert value.arguments == {'thingId': 'Thing_Wood1', 'pawn': 'Thing_Pawn1', 'unwanted': 'Thing_Wood1', 'bury': '', 'dryRun': False}


@pytest.mark.asyncio
@pytest.mark.parametrize('tick,direction,expected', [(10, 2, 'waiting'), (11, 2, 'complete'), (11, 3, 'blocked')])
async def test_durable_order_requires_fresh_tick_and_unchanged_direction(tick, direction, expected):
    from rimbot.colony_plan import PlanSpec, PlanStep, StepProgress
    from rimbot.waste_management import refresh
    step = PlanStep(id='waste-step', title='Contain corpse', action=action(), completion_criteria='Exact containment')
    plan = ColonyPlan(spec=PlanSpec(steps=[step]), control={'player_direction': direction},
        progress={step.id: StepProgress(state='waiting', issued={'0': {'confirmed': True,
            'load_token': 'load', 'player_direction': 2, 'issued_tick': 10}})})
    # Paired persistence must retain the order's native scope and freshness gate.
    plan = ColonyPlan.model_validate_json(plan.model_dump_json())
    rt = SimpleNamespace(current_plan=plan, context_token='load', chat_revision=1,
        batch=SimpleNamespace(native={'pawns': {'pawns': []}}), signal=Mock(), sync_identity=AsyncMock(),
        game=SimpleNamespace(query=AsyncMock(return_value={'success': True, 'tick': tick, 'items': [
            {'thingId': 'Thing_Corpse1', 'state': 'relocated'}]})))
    await refresh(rt)
    assert plan.progress[step.id].state == expected
