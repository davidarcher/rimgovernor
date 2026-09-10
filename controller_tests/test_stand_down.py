from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store


def runtime(tmp_path):
    rt=BridgeRuntime(Store(tmp_path/'test.sqlite'),tmp_path)
    rt.mode='automate';rt.context_token='load'
    rt.sync_identity=AsyncMock(return_value=False)
    return rt


@pytest.mark.asyncio
async def test_selected_cleanup_leaves_automation_and_player_drafts_alone(tmp_path):
    rt=runtime(tmp_path);rt.draft_owners={'AI':'load','Other':'load','Stale':'old'}
    drafted={'AI':True}
    async def invoke(name,args,**kwargs):
        if args['action']=='undraft':drafted[args['pawn']]=False
        return {'pawn':{'thingId':args['pawn'],'drafted':drafted[args['pawn']], 'draftOwner':'load'}}
    rt.game=SimpleNamespace(invoke=AsyncMock(side_effect=invoke))
    result=await rt.stand_down(['AI','Player','Stale'],expected_token='load',expected_revision=0,expected_plan_revision=0)
    assert result=={'released':['AI'],'not_owned':['Player','Stale'],'failed':{}}
    assert rt.mode=='automate' and rt.draft_owners=={'Other':'load','Stale':'old'}
    assert all(c.args[1]['pawn']=='AI' for c in rt.game.invoke.await_args_list)
    assert rt.game.invoke.await_args_list[1].args[1]['releaseOwner']=='load'
    rt.store.close()


@pytest.mark.asyncio
async def test_lost_undraft_receipt_reconciles_without_second_write(tmp_path):
    rt=runtime(tmp_path);rt.draft_owners={'AI':'load'};drafted=True;writes=0
    async def invoke(name,args,**kwargs):
        nonlocal drafted,writes
        if args['action']=='undraft':
            writes+=1;drafted=False;raise RuntimeError('lost reply')
        return {'pawn':{'thingId':'AI','drafted':drafted,'draftOwner':'load'}}
    rt.game=SimpleNamespace(invoke=AsyncMock(side_effect=invoke))
    first=await rt.release_drafts(['AI'])
    assert first['failed'] and 'AI' in rt.draft_owners
    second=await rt.release_drafts(['AI'])
    assert second['released']==['AI'] and not rt.draft_owners and writes==1
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('owner', [None, 'another-controller'])
async def test_native_claim_loss_preserves_redrafted_pawn(tmp_path, owner):
    rt=runtime(tmp_path);rt.draft_owners={'AI':'load'}
    rt.game=SimpleNamespace(invoke=AsyncMock(return_value={
        'pawn':{'thingId':'AI','drafted':True,'draftOwner':owner}}))
    result=await rt.release_drafts()
    assert result=={'released':[], 'not_owned':['AI'], 'failed':{}}
    assert not rt.draft_owners
    assert rt.game.invoke.await_count==1
    rt.store.close()


@pytest.mark.asyncio
async def test_legacy_native_read_does_not_authorize_cleanup(tmp_path):
    rt=runtime(tmp_path);rt.draft_owners={'AI':'load'}
    rt.game=SimpleNamespace(invoke=AsyncMock(return_value={
        'pawn':{'thingId':'AI','drafted':True}}))
    result=await rt.release_drafts()
    assert 'AI' in result['failed'] and rt.draft_owners=={'AI':'load'}
    assert rt.game.invoke.await_count==1
    rt.store.close()


@pytest.mark.asyncio
async def test_steering_during_resolution_stops_undraft(tmp_path):
    rt=runtime(tmp_path);rt.draft_owners={'AI':'load'}
    async def invoke(name,args,**kwargs):
        rt.chat_revision+=1
        return {'pawn':{'thingId':'AI','drafted':True}}
    rt.game=SimpleNamespace(invoke=AsyncMock(side_effect=invoke))
    with pytest.raises(InterruptedError):
        await rt.stand_down(['AI'],expected_token='load',expected_revision=0,expected_plan_revision=0)
    assert rt.game.invoke.await_count==1 and rt.draft_owners=={'AI':'load'}
    rt.store.close()
