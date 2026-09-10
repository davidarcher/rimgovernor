import asyncio
from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock,Mock
import pytest
from rimgovernor.colony_plan import ColonyPlan,ColonyGoal,PlanSpec,StepProgress,Failure
from rimgovernor.construction_recovery import recover_construction
from rimgovernor.hands import Hands
from rimgovernor.resource_accounting import ResourceShortage,validate_execution_costs


def fixture():
    plan=ColonyPlan(spec=PlanSpec(steps=[dict(id='build',title='Shelter',goal_id='EnsureInitialShelter',
        source='AUTOPILOT',completion_criteria='Built',action=dict(kind='place_buildings',placements=[
            dict(def_name='Wall',x=x,z=10,materials=['WoodLog']) for x in range(10,13)]))]),
        colony_goals={'EnsureInitialShelter':ColonyGoal(priority_class=2,steps=['build'])},
        progress={'build':StepProgress()},control={'costs':{'build':{str(i):{'WoodLog':5} for i in range(3)}}})
    stock={'wood':15}
    async def query(name,**args):
        if name=='home/spatial_access':return dict(success=True,accepted=True,pawnCount=1)
        if name=='home/status':return {'time':{'ticksGame':200,'paused':True},'threats':{'hostileCount':0,'huntingPredatorCount':0}}
        if name=='home/list_buildings':return {'buildings':[]}
        if name=='home/list_zones':return {'zones':[]}
        raise AssertionError(name)
    async def preview(name,args):
        return {'canPlace':True,'madeFromStuff':True,'passability':'Impassable','isDoor':False,'costList':[{'defName':'WoodLog','count':5}],
            'materials':{'rows':[{'defName':'WoodLog','available':stock['wood']}]},
            'rotations':[{'rotation':args['rotation'],'occupiedCells':[{'x':args['x'],'z':args['z']}]}]}
    async def native(*args,**kwargs):
        stock['wood']=9
        return {'receipt':{'outcome':'placed','stuff':'WoodLog'}}
    rt=SimpleNamespace(current_plan=plan,hands=Hands(),lock=asyncio.Lock(),sync_identity=AsyncMock(),
        mode='automate',context_token='load',chat_revision=0,handled_revision=0,
        game=SimpleNamespace(query=AsyncMock(side_effect=query)),inspect_native=AsyncMock(side_effect=preview),
        native=AsyncMock(side_effect=native),batch=SimpleNamespace(summary=SimpleNamespace(end_tick=100)),
        note=Mock(),persist=Mock(),signal=Mock(),projects=SimpleNamespace(
            upsert=Mock(return_value=SimpleNamespace(id='project',state='complete')),reconcile=AsyncMock()))
    async def locked_preview(name,args,allow_write):
        assert allow_write is False and rt.lock.locked()
        return await rt.inspect_native(name,args)
    rt.game.invoke=AsyncMock(side_effect=locked_preview)
    return rt,stock


async def blocked_fixture():
    rt,stock=fixture()
    await rt.hands.advance(rt)
    assert rt.current_plan.progress['build'].failure.code=='construction_resources'
    return rt,stock


async def recover(rt,limit=3):
    return await recover_construction(rt,'build',token='load',direction=0,limit=limit)


@pytest.mark.asyncio
async def test_known_shortage_resumes_only_remaining_pieces_after_full_reservation_recovers():
    rt,stock=await blocked_fixture()
    p=rt.current_plan.progress['build'];original=deepcopy(p.issued)
    assert set(original)=={'0'} and original['0']['confirmed']
    assert rt.native.await_count==1
    # One piece is affordable; the entire remaining commitment is not.
    assert not await recover(rt)
    assert p.issued==original and len(p.recovery_history)==0
    stock['wood']=15
    assert await recover(rt)
    assert p.issued==original and rt.native.await_count==1
    assert len(p.recovery_history)==1 and p.state=='pending'
    rt.current_plan=ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    await rt.hands.advance(rt)
    p=rt.current_plan.progress['build']
    assert p.state=='complete' and len(p.issued)==3
    assert [c.args[1]['x'] for c in rt.native.await_args_list]==[10,11,12]


@pytest.mark.asyncio
@pytest.mark.parametrize('change',['unknown_write','new_load','new_direction','manual','cancelled','changed_action','rewind','danger','policy','limit','unknown_stock'])
async def test_recovery_refuses_uncertainty_changed_authority_and_unverified_preconditions(change):
    rt,stock=await blocked_fixture();stock['wood']=15
    p=rt.current_plan.progress['build'];step=rt.current_plan.spec.steps[0]
    if change=='unknown_write':p.issued['1']={'confirmed':False}
    if change=='new_load':rt.context_token='new-load'
    if change=='new_direction':rt.chat_revision=1;rt.current_plan.control['player_direction']=1
    if change=='manual':rt.mode='manual'
    if change=='cancelled':rt.current_plan.colony_goals['EnsureInitialShelter'].cancelled=True
    if change=='changed_action':step.action.placements[1].x=99
    if change in ('rewind','danger'):
        rt.game.query=AsyncMock(return_value={'time':{'ticksGame':50 if change=='rewind' else 200,'paused':True},
            'threats':{'hostileCount':1 if change=='danger' else 0,'huntingPredatorCount':0}})
    if change=='policy':rt.current_plan.control['resource_policy']={'WoodLog':{'spending':'stop'}}
    if change=='limit':p.recovery_history=[{}]*3
    if change=='unknown_stock':stock['wood']=None
    before=deepcopy(p.model_dump())
    assert not await recover(rt)
    assert p.model_dump()==before and rt.native.await_count==1


@pytest.mark.asyncio
async def test_direction_change_during_preview_cannot_publish_recovery():
    rt,stock=await blocked_fixture();stock['wood']=15
    preview=rt.inspect_native.side_effect
    async def changed(*args):
        rt.chat_revision+=1
        return await preview(*args)
    rt.inspect_native.side_effect=changed
    assert not await recover(rt)
    assert rt.current_plan.progress['build'].state=='blocked'


def test_unknown_availability_is_not_a_retryable_resource_shortage():
    rt,_=fixture()
    with pytest.raises(ValueError,match='unknown') as error:
        validate_execution_costs(rt.current_plan,rt.current_plan.progress['build'],'0',{
            'costList':[{'defName':'WoodLog','count':5}], 'materials':{'rows':[]}})
    assert not isinstance(error.value,ResourceShortage)


@pytest.mark.asyncio
async def test_known_prewrite_placement_refusal_recovers_only_after_native_preview_changes():
    rt,stock=fixture();preview=rt.inspect_native.side_effect
    rt.inspect_native.side_effect=None
    rt.inspect_native.return_value={'canPlace':False,'researchFinished':True,'buildableByPlayer':True}
    await rt.hands.advance(rt)
    p=rt.current_plan.progress['build']
    assert p.failure.code=='construction_unavailable' and p.failure.evidence['prewrite']
    rt.native.assert_not_awaited()
    assert not await recover(rt)
    rt.inspect_native.side_effect=preview
    assert await recover(rt)
    assert p.state=='pending' and not p.issued and len(p.recovery_history)==1
    rt.native.assert_not_awaited()
    # Legacy/unknown refusals cannot gain retry authority through fresh observations.
    p.state='blocked'
    p.failure=Failure(code='construction_unavailable',detail='old',retryable=True,evidence={})
    assert not await recover(rt)


@pytest.mark.asyncio
async def test_failure_and_native_evidence_revisions_do_not_impersonate_player_direction():
    rt,stock=await blocked_fixture();stock['wood']=15
    rt.chat_revision=4  # The review has not marked these internal events handled yet.
    assert await recover_construction(rt,'build',token='load',direction=4,limit=3)
    rt,stock=await blocked_fixture();stock['wood']=15
    rt.chat_revision=4;rt.handled_revision=4
    rt.current_plan.control['player_direction']=1
    assert not await recover_construction(rt,'build',token='load',direction=4,limit=3)
    # Previously persisted failures lack a separate authority scope and stay strict.
    rt.current_plan.control['player_direction']=0
    del rt.current_plan.progress['build'].failure.evidence['player_direction']
    assert not await recover_construction(rt,'build',token='load',direction=4,limit=3)


@pytest.mark.asyncio
async def test_recovery_uses_read_only_preview_under_existing_writer_lock():
    rt,stock=await blocked_fixture();stock['wood']=15
    preview=rt.inspect_native.side_effect
    async def locking_inspect(name,args):
        async with rt.lock:return await preview(name,args)
    async def invoke(name,args,allow_write):
        assert allow_write is False and rt.lock.locked()
        return await preview(name,args)
    rt.inspect_native.side_effect=locking_inspect
    rt.game.invoke.side_effect=invoke
    assert await asyncio.wait_for(recover(rt),1)
