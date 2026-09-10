from types import SimpleNamespace
from unittest.mock import AsyncMock,Mock
import pytest
import asyncio
from rimbot.bridge_runtime import BridgeRuntime
from test_construction_recovery import fixture
from rimbot.colony_plan import ColonyPlan,ColonyGoal,PlanSpec,StepProgress
from rimbot.supply_recovery import recover_starting_supplies

async def blocked_supply():
    rt,_=fixture()
    rt.current_plan=ColonyPlan(spec=PlanSpec(steps=[dict(id='allow',title='Allow starter supplies',source='AUTOPILOT',
        goal_id='AllowStartingSupplies',completion_criteria='Allowed',action=dict(kind='native_operation',
        tool='rimworld/apply_architect_designator',arguments=dict(designatorId='allow',x=10,z=10,dryRun=False)))]),
        progress={'allow':StepProgress()},colony_goals={'AllowStartingSupplies':ColonyGoal(priority_class=2,steps=['allow'])})
    error=RuntimeError('Native allow preview refused')
    error.result=SimpleNamespace(structuredContent=dict(dryRun=True,acceptedCellCount=0,appliedCellCount=0,
        designator={'className':'RimWorld.Designator_Unforbid'}))
    rt.game.describe=AsyncMock(return_value={'properties':{'dryRun':{}}})
    rt.inspect_native=AsyncMock(side_effect=error)
    await rt.hands.advance(rt)
    assert rt.current_plan.progress['allow'].failure.code=='starting_supplies_unavailable'
    async def query(name,**args):
        if name=='home/status':return {'time':{'ticksGame':200,'paused':True}}
        if name=='home/list_things':return {'success':True,'forbiddenTotal':0,'foggedTotal':0}
        raise AssertionError(name)
    rt.game.query=AsyncMock(side_effect=query)
    rt.refresh_clock_events=AsyncMock()
    return rt

@pytest.mark.asyncio
async def test_obsolete_allow_recovers_from_native_absence_without_replaying_any_write():
    rt=await blocked_supply()
    assert await recover_starting_supplies(rt,'allow',token='load',direction=0)
    p=rt.current_plan.progress['allow']
    assert p.state=='complete' and len(p.recovery_history)==1
    assert p.issued['0']['native_outcome']=='already_allowed'
    assert rt.native.await_count==0
    assert not await recover_starting_supplies(rt,'allow',token='load',direction=0)
    assert len(p.recovery_history)==1

@pytest.mark.asyncio
@pytest.mark.parametrize('change',['remaining','unknown','fogged','manual','new_load','player','late_player','changed_target','cancelled'])
async def test_obsolete_allow_refuses_uncertain_stock_and_changed_authority(change):
    rt=await blocked_supply();plan=rt.current_plan
    if change in ('remaining','unknown','fogged'):
        value=dict(success=True,forbiddenTotal=1 if change=='remaining' else None if change=='unknown' else 0,foggedTotal=1 if change=='fogged' else 0)
        rt.game.query=AsyncMock(side_effect=[{'time':{'ticksGame':200,'paused':True}},value])
    if change=='manual':rt.mode='manual'
    if change=='new_load':rt.context_token='different'
    if change=='player':plan.control['player_direction']=1
    if change=='late_player':
        async def changed():plan.control['player_direction']=1
        rt.sync_identity=changed
    if change=='changed_target':plan.spec.steps[0].action.arguments['x']=99
    if change=='cancelled':plan.colony_goals['AllowStartingSupplies'].cancelled=True
    assert not await recover_starting_supplies(rt,'allow',token='load',direction=0)
    assert plan.progress['allow'].state=='blocked' and rt.native.await_count==0

@pytest.mark.asyncio
@pytest.mark.parametrize('kind',['external_pause','external_speed_changed'])
async def test_buffered_native_player_clock_input_invalidates_no_write_reconciliation(kind, tmp_path):
    from rimbot.store import Store
    from rimbot.strategic_state import StrategicState
    rt=await blocked_supply()
    rt.clock_events=[];rt.chat=[];rt.wake=asyncio.Event()
    rt.note=Mock(return_value={'id':1,'text':'Player clock input'})
    rt.strategic_state=StrategicState()
    rt.store=Store(tmp_path/'events.sqlite')
    rt.phase='Ready';rt.resume_after_review=False
    rt.execution_window_end=None;rt.execution_wait_explicit=False
    rt._receive_clock_events=lambda:BridgeRuntime._receive_clock_events(rt)
    rt.supervisor=SimpleNamespace(acknowledged_stop=None,poll=AsyncMock(return_value=[
        {'kind':kind,'epoch':1,'detail':'Player clock input','tick':200}]))
    rt.receive_clock_events=lambda:BridgeRuntime.receive_clock_events(rt)
    async def refresh():await BridgeRuntime.refresh_clock_events(rt)
    rt.refresh_clock_events=refresh
    assert not await recover_starting_supplies(rt,'allow',token='load',direction=0)
    assert rt.mode=='manual' and rt.current_plan.control['player_direction']==1
    assert rt.current_plan.progress['allow'].state=='blocked'
    assert rt.native.await_count==0
    rt.store.close()
