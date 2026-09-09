import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

import httpx
import pytest

from rimbot.bridge_server import create_app
from rimbot.bridge_game import BridgeGame


def runtime():
    return SimpleNamespace(
        lock=asyncio.Lock(), connected=True, context_token='load-a',
        sync_identity=AsyncMock(), persist=Mock(), note=Mock(),
        supervisor=SimpleNamespace(change=AsyncMock(return_value={'active': True}), allow_resume=Mock()),
        game=SimpleNamespace(cinematic=False, query=AsyncMock(return_value={'time': {'paused': False}})),
        release_drafts=AsyncMock(), headless=False, mode='automate', chat_revision=4,
        manual_requests=['pending'], manual_execution=('old',),
    )


@pytest.mark.asyncio
async def test_time_takes_manual_ownership_and_invalidates_pending_work():
    rt=runtime(); app=create_app(rt); app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver', headers={'X-RimBot':'1'}) as client:
        result=await client.post('/api/time', json={'session_id':'load-a','speed':'Fast'})
    assert result.status_code==200
    assert rt.mode=='manual' and rt.chat_revision==5 and not rt.resume_after_review
    assert rt.manual_requests==[] and rt.manual_execution is None
    assert [c.args[0] for c in rt.supervisor.change.await_args_list]==['Paused','Fast']
    rt.release_drafts.assert_awaited_once()
    rt.game.query.assert_awaited_once_with('home/status',colonists=False,threats=False)


@pytest.mark.asyncio
async def test_stale_session_and_bad_speed_do_not_touch_clock():
    rt=runtime(); app=create_app(rt); app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver', headers={'X-RimBot':'1'}) as client:
        assert (await client.post('/api/time',json={'session_id':'old','speed':'Fast'})).status_code==400
        assert (await client.post('/api/time',json={'session_id':'load-a','speed':'Ultrafast'})).status_code==422
        assert (await client.post('/api/time',json={'session_id':'load-a','speed':'Normal','ultraSpeedBoost':True})).status_code==422
    rt.supervisor.change.assert_not_awaited()
    assert rt.mode=='automate' and rt.chat_revision==4


@pytest.mark.asyncio
async def test_failed_pause_never_resumes_and_leaves_automation_off():
    rt=runtime(); rt.supervisor.change.side_effect=ValueError('Native pause failed')
    app=create_app(rt); app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver', headers={'X-RimBot':'1'}) as client:
        assert (await client.post('/api/time',json={'session_id':'load-a','speed':'Normal'})).status_code==400
    assert rt.mode=='manual'
    rt.supervisor.change.assert_awaited_once_with('Paused')
    rt.supervisor.allow_resume.assert_not_called()


@pytest.mark.asyncio
async def test_new_direction_during_pause_prevents_resume():
    rt=runtime()
    async def new_direction():
        rt.chat_revision += 1
    rt.release_drafts.side_effect=new_direction
    app=create_app(rt);app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver',headers={'X-RimBot':'1'}) as client:
        result=await client.post('/api/time',json={'session_id':'load-a','speed':'Fast'})
        assert result.status_code==400
        assert 'New player direction' in result.json()['detail']
    rt.supervisor.change.assert_awaited_once_with('Paused')


@pytest.mark.asyncio
async def test_review_blocks_play_but_not_explicit_pause():
    rt=runtime();rt.review_task=SimpleNamespace(done=lambda:False)
    app=create_app(rt);app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver',headers={'X-RimBot':'1'}) as client:
        assert (await client.post('/api/time',json={'session_id':'load-a','speed':'Normal'})).status_code==400
        rt.supervisor.change.assert_not_awaited()
        assert (await client.post('/api/time',json={'session_id':'load-a','speed':'Paused'})).status_code==200


@pytest.mark.asyncio
async def test_camera_is_explicit_session_bound_and_rendered_only():
    rt=runtime(); app=create_app(rt); app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver') as client:
        body={'session_id':'load-a','following':True}
        assert (await client.post('/api/camera/follow',json=body)).status_code==403
        headers={'X-RimBot':'1'}
        assert (await client.post('/api/camera/follow',json={**body,'session_id':'old'},headers=headers)).status_code==400
        assert not rt.game.cinematic
        assert (await client.post('/api/camera/follow',json=body,headers=headers)).json()=={'following':True}
        assert rt.game.cinematic
        rt.headless=True; rt.game.cinematic=False
        assert (await client.post('/api/camera/follow',json=body,headers=headers)).status_code==400
        assert not rt.game.cinematic


@pytest.mark.asyncio
async def test_cinematic_only_decorates_authorized_real_writes():
    bridge=SimpleNamespace(call=AsyncMock(return_value=SimpleNamespace(structuredContent={})))
    game=BridgeGame(bridge)
    game.schemas['home/place_building']={'type':'object','properties':{'dryRun':{'type':'boolean'},'watch':{'type':'boolean'}}}
    await game.invoke('home/place_building',{'dryRun':False},allow_write=True)
    assert bridge.call.call_args.kwargs['watch'] is False
    game.cinematic=True
    await game.invoke('home/place_building',{'dryRun':False,'watch':False},allow_write=True)
    assert bridge.call.call_args.kwargs['watch'] is True
    with pytest.raises(ValueError,match='Automation is off'):
        await game.invoke('home/place_building',{'dryRun':False})
    await game.invoke('home/place_building',{'dryRun':True})
    assert bridge.call.call_args.kwargs['watch'] is False
