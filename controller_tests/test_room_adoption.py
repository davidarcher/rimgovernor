from copy import deepcopy
from unittest.mock import AsyncMock
import pytest
from rimbot.player_commands import apply_command
from rimbot.shelter_handoff import completed_shelters,player_shelter,sleeping_handoff
from rimbot.colony_skills import SkillBlocked
from test_strategic_architecture import runtime,batch


async def fixture(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    room={'id':3,'properRoom':True,'psychologicallyOutdoors':False,'openRoofCount':0,'cellsComplete':True,
          'cells':[{'x':x,'z':z} for x in range(11,18) for z in range(11,18)]}
    async def query(name,**args):
        if name=='home/spatial_access':return dict(success=True,accepted=True,pawnCount=1)
        if name=='home/list_rooms':
            return {'success':True,'rooms':[{'isDoorway':True,'doorDef':'Door'}] if args.get('includeOutdoors') else [room]}
        if name=='home/list_buildings':
            return {'success':True,'buildings':[{'thingId':'Door1','defName':'Door','position':{'x':14,'z':10},
                'isBlueprint':False,'isFrame':False}]}
        return {'colonyId':'test','mapId':1,'loadToken':'load'}
    rt.game.query=AsyncMock(side_effect=query)
    return rt,room


async def adopt(rt):
    return await apply_command(rt,{'kind':'AdoptRoom','intent_id':'edited-home',
        'bounds':{'x':10,'z':10,'width':9,'height':9},'entrance':'south'},token=rt.context_token,revision=rt.chat_revision)


@pytest.mark.asyncio
async def test_explicit_native_room_adoption_uses_existing_handoff_without_orders(tmp_path):
    rt,room=await fixture(tmp_path)
    result=await adopt(rt)
    assert result['native_room']==3 and not rt.current_plan.spec.steps and not rt.manual_requests
    selected=player_shelter(rt.current_plan)
    assert selected[0]=='intent-edited-home' and selected[1].status=='complete'
    assert list(completed_shelters(rt.current_plan))==[(selected[0],selected[2])]
    assert selected[1].evidence['adoption']['loadToken']=='load'
    rt.game.invoke.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('change',['roof','geometry','entrance','direction','load'])
async def test_adoption_rejects_incomplete_or_stale_room_without_changing_plan(tmp_path,change):
    rt,room=await fixture(tmp_path)
    if change=='roof':room['openRoofCount']=1
    if change=='geometry':room['cells'].pop()
    original=rt.game.query.side_effect
    async def query(name,**args):
        result=await original(name,**args)
        if name=='home/list_buildings':
            if change=='entrance':result['buildings'][0]['position']['x']=13
            if change=='direction':rt.chat_revision+=1
            if change=='load':rt.context_token='different'
        return result
    rt.game.query.side_effect=query
    before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises((ValueError,InterruptedError)):await adopt(rt)
    assert rt.current_plan.model_dump()==before and not rt.manual_requests
    rt.store.close()


@pytest.mark.asyncio
async def test_adopted_room_requires_fresh_adoption_after_load(tmp_path):
    rt,room=await fixture(tmp_path)
    await adopt(rt)
    identity,goal,shell=player_shelter(rt.current_plan)
    rt.identity['loadToken']='new-load'
    rt.game.query.reset_mock()
    with pytest.raises(SkillBlocked,match='another load'):
        await sleeping_handoff(rt,{'colonists':3,'indoorSleepingCapacity':0},identity,shell)
    rt.game.query.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('access',[dict(success=False),dict(success=True,accepted=False,pawnCount=1),
    dict(success=True,accepted=True,pawnCount=0)])
async def test_adoption_cannot_claim_an_unreachable_or_unverified_room(tmp_path,access):
    rt,_=await fixture(tmp_path);original=rt.game.query.side_effect
    async def query(name,**args):
        if name=='home/spatial_access':
            assert args==dict(blockedCells='',targetCells='14,11;14,9')
            return access
        return await original(name,**args)
    rt.game.query.side_effect=query;before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError,match='safe native pawn route'):await adopt(rt)
    assert rt.current_plan.model_dump()==before
    rt.store.close()
