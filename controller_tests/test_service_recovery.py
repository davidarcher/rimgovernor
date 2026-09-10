from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimgovernor.colony_plan import ColonyGoal, NativeOperation
from rimgovernor.service_recovery import pending, outcome, compile_method
from rimgovernor.colony_policy import ColonyPolicy, criteria
from rimgovernor.disaster_recovery import reconcile
from test_disaster_recovery import stable


def building(**values):
    return dict(thingId='Thing_Stove1', hitPoints=50, maxHitPoints=100, broken=False,
                fuel=None, fuelTarget=None, **values)


def state(row):
    return dict(success=True, buildings=[row])


def action(method='repair'):
    return NativeOperation(tool='home/recover_service', completion='service_recovered',
                           arguments=dict(thingId='Thing_Stove1', pawn='Thing_Pawn1', method=method))


def test_receipt_and_partial_repair_cannot_complete_missing_or_interrupted_work():
    row = building()
    assert pending(state(row))[0][1] == 'repair'
    assert outcome(action(), {'job': 'Repair'}, state(row), [{'thingId': 'Thing_Pawn1', 'job': 'Repair'}]) == 'waiting'
    assert outcome(action(), {}, state(row), []).code == 'recovery_unverified'
    assert outcome(action(), {}, dict(success=True, buildings=[]), []).code == 'recovery_unverified'
    row['hitPoints'] = 100
    assert outcome(action(), {}, state(row), []) == 'complete'


def test_native_objects_without_hit_points_do_not_need_impossible_repairs():
    row=building(usesHitPoints=False)
    row['hitPoints']=-1
    assert pending(state(row))==[]
    row.update(broken=True,fuel=0,fuelTarget=10)
    assert {method for _,method in pending(state(row))}=={'breakdown','refuel'}


@pytest.mark.parametrize('uses',[True,None])
def test_unknown_or_real_hit_points_retain_damage(uses):
    assert pending(state(building(usesHitPoints=uses)))[0][1]=='repair'


def test_native_no_hit_points_observation_can_clear_legacy_damage_tracking():
    value=stable()
    value['environment']['conditions']=[{'defName':'PsychicSoothe'}]
    row=building()
    row['hitPoints']=-1
    value['recovery']=state(row)
    control={}
    assert reconcile(control,value,ColonyPolicy(),context='load',direction=0)['deficits']==['infrastructure']
    row['usesHitPoints']=False
    value['environment']['conditions']=[]
    assert reconcile(control,value,ColonyPolicy(),context='load',direction=0)['phase']=='restored'


def test_refuel_requires_increase_and_power_requires_actual_service():
    row = building(powerOn=False)
    row.update(hitPoints=100, fuel=0, fuelTarget=50)
    assert pending(state(row))[0][1] == 'refuel'
    assert outcome(action('refuel'), {'fuel': 0}, state(row), []).code == 'recovery_unverified'
    row['fuel'] = 10
    assert outcome(action('refuel'), {'fuel': 0}, state(row), []) == 'complete'
    value = stable()
    value['recovery'] = state(row)
    assert not criteria(value, ColonyPolicy())['power']
    row['switchedOn'] = False
    assert criteria(value, ColonyPolicy())['power']
    row.update(switchedOn=True, powerConsumer=False)
    assert criteria(value, ColonyPolicy())['power']


def test_missing_damaged_target_and_missing_state_do_not_restore_episode():
    value = stable()
    value['environment']['conditions'] = [{'defName': 'SolarFlare'}]
    value['recovery'] = state(building())
    control = {}
    reconcile(control, value, ColonyPolicy(), context='load', direction=0)
    value['environment']['conditions'] = []
    value['recovery']['buildings'] = []
    assert reconcile(control, value, ColonyPolicy(), context='load', direction=0)['phase'] == 'recovering'
    del value['recovery']
    assert reconcile(control, value, ColonyPolicy(), context='load', direction=0)['phase'] == 'recovering'


@pytest.mark.asyncio
async def test_compile_is_bounded_and_refuses_inaccessible_native_supplies():
    from rimgovernor.colony_skills import SkillBlocked
    rt = SimpleNamespace(inspect_native=AsyncMock(return_value={'success': True, 'accepted': False}))
    people = [dict(thingId=f'Thing_Pawn{i}') for i in range(20)]
    with pytest.raises(SkillBlocked, match='reachable'):
        await compile_method(rt, ColonyGoal(priority_class=2), {'recovery': state(building())}, people)
    assert rt.inspect_native.await_count == 8


@pytest.mark.asyncio
async def test_compile_keeps_native_completion_and_exact_target():
    rt = SimpleNamespace(inspect_native=AsyncMock(return_value={'success': True, 'accepted': True}))
    _, actions = await compile_method(rt, ColonyGoal(priority_class=2), {'recovery': state(building())}, [dict(thingId='Thing_Pawn1')])
    assert actions[0]['completion'] == 'service_recovered'
    assert actions[0]['arguments']['thingId'] == 'Thing_Stove1'


def test_recovery_cannot_use_receipt_only_completion():
    with pytest.raises(ValueError, match='Recovery requires'):
        NativeOperation(tool='home/recover_service', arguments={'thingId': 'Thing_Stove1', 'pawn': 'Thing_Pawn1', 'method': 'repair'})


def test_native_recovery_refusal_reason_is_visible():
    from rimgovernor.receipts import reason
    assert reason({'success': True, 'accepted': False, 'error': None, 'reason': 'Roofed refuge inaccessible'}) == 'Roofed refuge inaccessible'


@pytest.mark.asyncio
async def test_live_gateway_allows_recovery_reads_but_not_recovery_writes():
    from mcp.types import CallToolResult
    from rimgovernor.bridge_game import BridgeGame
    bridge = AsyncMock()
    bridge.detail.return_value = CallToolResult(content=[], structuredContent={
        'inputSchema': {'type': 'object', 'properties': {}}})
    bridge.call.return_value = CallToolResult(content=[], structuredContent=state(building()))
    game = BridgeGame(bridge)
    assert (await game.query('home/recovery_state'))['buildings'][0]['hitPoints'] == 50
    with pytest.raises(ValueError, match='approved observation'):
        await game.query('home/recover_service')
