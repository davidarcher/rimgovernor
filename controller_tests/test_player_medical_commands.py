from unittest.mock import AsyncMock
import pytest
from rimgovernor.player_commands import apply_command
from test_strategic_architecture import runtime, batch


@pytest.mark.asyncio
async def test_manual_ground_tending_requires_existing_player_draft(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    async def query(name,**args):
        if name=='home/list_pawns':return {'pawns':[
            {'thingId':'Thing_Human1','name':'Doctor','drafted':False},
            {'thingId':'Thing_Human2','name':'Patient','dead':False,'health':{'needsTend':True}}]}
        return {'colonyId':'test','mapId':1,'loadToken':'load'}
    rt.game.query=AsyncMock(side_effect=query)
    rt.inspect_native=AsyncMock(return_value={'success':True,'wouldIssue':{'tendPath':'drafted'}})
    with pytest.raises(ValueError,match='already drafted doctor'):
        await apply_command(rt,dict(kind='TendPawn',pawn='Doctor',patient='Patient'),token=rt.context_token,revision=rt.chat_revision)
    assert not rt.current_plan.spec.steps
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('kind,completion', [('TendPawn','patient_tended'),('RescuePawn','patient_in_bed')])
async def test_medical_request_resolves_identity_and_waits_for_native_labor(tmp_path,kind,completion):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    async def query(name,**args):
        if name=='home/list_pawns':return {'pawns':[
            {'thingId':'Thing_Human1','name':'Doctor','dead':False,'downed':False},
            {'thingId':'Thing_Human2','name':'Patient','dead':False,'downed':True,'health':{'needsTend':True}}]}
        return {'colonyId':'test','mapId':1,'loadToken':'load'}
    rt.game.query=AsyncMock(side_effect=query)
    rt.inspect_native=AsyncMock(return_value={'success':True})
    result=await apply_command(rt,dict(kind=kind,pawn='Doctor',patient='Patient'),token=rt.context_token,revision=rt.chat_revision)
    step=next(s for s in rt.current_plan.spec.steps if s.id==result['step'])
    assert step.source=='PLAYER' and step.action.completion==completion
    assert step.action.arguments['pawn']=='Thing_Human1' and step.action.arguments['target']=='Thing_Human2'
    assert rt.current_plan.progress[step.id].state=='pending' and rt.counters['actions']==0
    assert rt.inspect_native.await_args.args[1]['dryRun'] is True
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('patient', [
    {'dead':True,'downed':True}, {'dead':False,'downed':False}, {'downed':True}])
async def test_rescue_refuses_unobserved_eligibility_before_admission(tmp_path,patient):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch()
    async def query(name,**args):
        if name=='home/list_pawns':return {'pawns':[
            {'thingId':'Thing_Human1','name':'Doctor'},dict(patient,thingId='Thing_Human2',name='Patient')]}
        return {'colonyId':'test','mapId':1,'loadToken':'load'}
    rt.game.query=AsyncMock(side_effect=query)
    rt.inspect_native=AsyncMock(return_value={'success':True})
    with pytest.raises(ValueError):
        await apply_command(rt,dict(kind='RescuePawn',pawn='Thing_Human1',patient='Thing_Human2'),token=rt.context_token,revision=rt.chat_revision)
    assert not rt.current_plan.spec.steps
    rt.inspect_native.assert_not_awaited()
    rt.store.close()

