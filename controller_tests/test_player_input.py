import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
import httpx
import pytest
from rimbot.bridge_server import create_app
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.player_input import InputLease


def runtime():
    rt = SimpleNamespace(lock=asyncio.Lock(), connected=True, context_token='load-a',
        sync_identity=AsyncMock(), persist=Mock(), note=Mock(), steer=AsyncMock(),
        supervisor=SimpleNamespace(change=AsyncMock(), allow_resume=Mock()),
        game=SimpleNamespace(cinematic=True, query=AsyncMock(return_value={'time': {'paused': True}})),
        release_drafts=AsyncMock(return_value={'failed': {}}), mode='automate', chat_revision=4,
        manual_requests=['pending'], manual_execution=('old',), headless=True)
    rt.halt = AsyncMock()
    rt.set_mode = lambda mode, **kw: BridgeRuntime.set_mode(rt, mode, **kw)
    return rt


async def request(rt, path, **body):
    app=create_app(rt); app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver',headers={'X-RimBot':'1'}) as client:
        return await client.post('/api/'+path,json={'session_id':'load-a','viewer_id':'a',**body})


@pytest.mark.asyncio
async def test_handoff_acknowledges_pause_and_invalidates_pending_orders():
    rt=runtime()
    response=await request(rt,'input/take')
    assert response.status_code==200
    assert rt.mode=='manual' and rt.chat_revision==5 and not rt.resume_after_review
    assert not rt.game.cinematic and rt.manual_requests==[] and rt.manual_execution is None
    assert rt.player_input.ready and response.json()['lease_id']==rt.player_input.token
    rt.supervisor.change.assert_awaited_once_with('Paused')
    rt.supervisor.allow_resume.assert_not_called()


@pytest.mark.asyncio
@pytest.mark.parametrize('failure',['pause','drafts','readback'])
async def test_failed_handoff_stays_manual_without_acknowledgement(failure):
    rt=runtime()
    if failure=='pause': rt.supervisor.change.side_effect=ValueError('pause failed')
    if failure=='drafts': rt.release_drafts.return_value={'failed':{'pawn':'uncertain'}}
    if failure=='readback': rt.game.query.return_value={'time':{'paused':False}}
    assert (await request(rt,'input/take')).status_code==400
    assert rt.mode=='manual' and not rt.player_input.ready
    assert (await request(rt,'input/heartbeat',lease_id=rt.player_input.token)).status_code==400


@pytest.mark.asyncio
async def test_exclusive_lease_expiry_and_stale_cleanup():
    rt=runtime(); await request(rt,'input/take'); old=rt.player_input.token
    assert (await request(rt,'input/take',viewer_id='b')).status_code==400
    assert (await request(rt,'input/heartbeat',lease_id=old)).status_code==200
    rt.player_input.deadline=0
    assert (await request(rt,'input/heartbeat',lease_id=old)).status_code==400
    with pytest.raises(ValueError,match='Release player control'):
        await rt.set_mode('automate')
    assert rt.mode=='manual'
    assert (await request(rt,'input/take',viewer_id='b')).status_code==200
    assert (await request(rt,'input/release',lease_id=old,resume=True)).status_code==400
    assert rt.player_input.viewer=='b' and rt.mode=='manual'


@pytest.mark.asyncio
async def test_owner_release_explicitly_resumes_and_other_controls_cannot_bypass():
    rt=runtime(); await request(rt,'input/take'); token=rt.player_input.token
    assert (await request(rt,'camera/follow',following=True)).status_code==400
    assert (await request(rt,'time',speed='Normal')).status_code==400
    assert (await request(rt,'input/release',lease_id=token,resume=True)).status_code==200
    assert rt.player_input is None and rt.mode=='automate' and rt.resume_after_review


@pytest.mark.asyncio
async def test_release_rechecks_direction_after_native_pause():
    rt=runtime(); await request(rt,'input/take'); token=rt.player_input.token
    async def change(*args): rt.chat_revision+=1
    rt.supervisor.change.side_effect=change
    assert (await request(rt,'input/release',lease_id=token,resume=True)).status_code==400
    assert rt.mode=='manual' and rt.player_input is not None


@pytest.mark.asyncio
async def test_held_input_blocks_native_writes_and_manual_dispatch_even_after_expiry():
    rt=runtime(); await request(rt,'input/take'); rt.player_input.deadline=0
    rt.refresh_clock_events=AsyncMock()
    with pytest.raises(ValueError,match='Player control is held'):
        await BridgeRuntime.native(rt,'home/order',{'action':'draft','pawn':'a','dryRun':False})
    rt.refresh_clock_events.assert_awaited_once()
    rt.manual_requests=[('a','load-a',5)]
    await BridgeRuntime.execute_manual_requests(rt)
    assert rt.manual_requests==[] and rt.manual_execution is None

@pytest.mark.asyncio
async def test_selection_requires_owner_current_pawn_and_native_readback():
    rt=runtime(); await request(rt,'input/take'); owner={'lease_id':rt.player_input.token}
    rt.headless=False
    async def detail(tool):
        return SimpleNamespace(structuredContent={'inputSchema':{'type':'object','properties':{
            'currentMapOnly':{'type':'boolean'},'pawnId':{'type':'string'},'append':{'type':'boolean'}}}})
    rt.bridge=SimpleNamespace(detail=AsyncMock(side_effect=detail),call=AsyncMock(side_effect=[
        SimpleNamespace(structuredContent={'success':True,'colonists':[{'pawnId':'pawn','spawned':True}]}),
        SimpleNamespace(structuredContent={'success':True}),
        SimpleNamespace(structuredContent={'success':True,'selectedCount':1,'selectedObjects':[{'id':'pawn'}]})]))
    assert (await request(rt,'input/select',pawn_id='pawn')).status_code==400
    assert (await request(rt,'input/select',pawn_id='pawn',**owner)).status_code==200
    assert rt.bridge.call.await_args_list[1].args==('rimworld/select_pawn',)
    assert rt.bridge.call.await_args_list[1].kwargs=={'pawnId':'pawn','append':False}
    rt.bridge.call.side_effect=[SimpleNamespace(structuredContent={'success':True,'colonists':[]})]
    assert (await request(rt,'input/select',pawn_id='gone',**owner)).status_code==400


@pytest.mark.asyncio
async def test_clear_selection_needs_observed_empty_selection():
    rt=runtime(); await request(rt,'input/take'); rt.headless=False
    rt.bridge=SimpleNamespace(detail=AsyncMock(return_value=SimpleNamespace(structuredContent={'inputSchema':{'type':'object'}})),
        call=AsyncMock(side_effect=[SimpleNamespace(structuredContent={'success':True}),
            SimpleNamespace(structuredContent={'success':True,'selectedCount':1,'selectedObjects':[{'id':'still-selected'}]})]))
    assert (await request(rt,'input/select',lease_id=rt.player_input.token)).status_code==400
    assert rt.bridge.call.await_count==2


@pytest.mark.parametrize('viewer, token', [('a', 'released'), ('a', ''), ('', 'released')])
def test_released_credentials_cannot_fall_back_to_unowned_controls(viewer, token):
    from rimbot.player_input import check_player_control
    rt = SimpleNamespace(player_input=None)
    with pytest.raises(ValueError, match='expired'):
        check_player_control(rt, 'load-a', viewer, token)
    check_player_control(rt, 'load-a', '', '')
