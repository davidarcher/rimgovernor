import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

import httpx
import pytest

from rimgovernor.bridge_server import create_app
from rimgovernor.bridge_game import BridgeGame


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
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver', headers={'X-RimGovernor':'1'}) as client:
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
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver', headers={'X-RimGovernor':'1'}) as client:
        assert (await client.post('/api/time',json={'session_id':'old','speed':'Fast'})).status_code==400
        assert (await client.post('/api/time',json={'session_id':'load-a','speed':'Ultrafast'})).status_code==422
        assert (await client.post('/api/time',json={'session_id':'load-a','speed':'Normal','ultraSpeedBoost':True})).status_code==422
    rt.supervisor.change.assert_not_awaited()
    assert rt.mode=='automate' and rt.chat_revision==4


@pytest.mark.asyncio
async def test_failed_pause_never_resumes_and_leaves_automation_off():
    rt=runtime(); rt.supervisor.change.side_effect=ValueError('Native pause failed')
    app=create_app(rt); app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver', headers={'X-RimGovernor':'1'}) as client:
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
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver',headers={'X-RimGovernor':'1'}) as client:
        result=await client.post('/api/time',json={'session_id':'load-a','speed':'Fast'})
        assert result.status_code==400
        assert 'New player direction' in result.json()['detail']
    rt.supervisor.change.assert_awaited_once_with('Paused')


@pytest.mark.asyncio
async def test_review_blocks_play_but_not_explicit_pause():
    rt=runtime();rt.review_task=SimpleNamespace(done=lambda:False)
    app=create_app(rt);app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver',headers={'X-RimGovernor':'1'}) as client:
        assert (await client.post('/api/time',json={'session_id':'load-a','speed':'Normal'})).status_code==400
        rt.supervisor.change.assert_not_awaited()
        assert (await client.post('/api/time',json={'session_id':'load-a','speed':'Paused'})).status_code==200


@pytest.mark.asyncio
async def test_camera_is_explicit_session_bound_and_rendered_only():
    rt=runtime(); app=create_app(rt); app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver') as client:
        body={'session_id':'load-a','following':True}
        assert (await client.post('/api/camera/follow',json=body)).status_code==403
        headers={'X-RimGovernor':'1'}
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


def camera_runtime():
    rt = runtime()
    state = {'success': True, 'mapId': 'map-a', 'rootSize': 20,
             'sizeRange': {'min': 12, 'max': 40}, 'cameraZoomExtensionEnabled': False}
    async def detail(tool):
        properties = {'rimworld/get_camera_state': {},
                      'rimworld/move_camera': {'deltaX': {'type': 'number'}, 'deltaZ': {'type': 'number'}},
                      'rimworld/set_camera_zoom': {'rootSize': {'type': 'number'}}}[tool]
        return SimpleNamespace(structuredContent={'inputSchema': {'type': 'object',
            'properties': properties, 'required': list(properties)}})
    rt.bridge = SimpleNamespace(detail=AsyncMock(side_effect=detail),
        call=AsyncMock(return_value=SimpleNamespace(structuredContent=state)))
    rt.game.cinematic = True
    return rt, state


async def navigate(rt, **body):
    app = create_app(rt); app.state.rt = rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver', headers={'X-RimGovernor': '1'}) as client:
        return await client.post('/api/camera/navigate', json={'session_id': 'load-a', 'action': 'left', **body})


@pytest.mark.asyncio
@pytest.mark.parametrize('action,arguments', [('left', {'deltaX': -10, 'deltaZ': 0}),
    ('right', {'deltaX': 10, 'deltaZ': 0}), ('up', {'deltaX': 0, 'deltaZ': 10}),
    ('down', {'deltaX': 0, 'deltaZ': -10}), ('in', {'rootSize': 18}), ('out', {'rootSize': 22})])
async def test_camera_navigation_discovers_contract_and_reads_back_without_changing_control(action, arguments):
    rt, state = camera_runtime()
    response = await navigate(rt, action=action)
    assert response.status_code == 200 and response.json()['camera'] == state
    assert rt.bridge.call.await_args_list[1].kwargs == arguments
    assert rt.bridge.call.await_args_list[-1].args == ('rimworld/get_camera_state',)
    assert not rt.game.cinematic and rt.mode == 'automate' and rt.chat_revision == 4
    rt.supervisor.change.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize('case', ['stale', 'headless', 'schema', 'changed', 'missing-map'])
async def test_camera_navigation_rejects_before_dispatch(case):
    rt, state = camera_runtime()
    body = {}
    if case == 'stale': body['session_id'] = 'old'
    if case == 'headless': rt.headless = True
    if case == 'missing-map': state.pop('mapId')
    if case == 'schema':
        rt.bridge.detail.side_effect = None
        rt.bridge.detail.return_value = SimpleNamespace(structuredContent={'inputSchema': {'type': 'object', 'required': ['unsupported']}})
    if case == 'changed':
        async def detail(tool):
            if tool == 'rimworld/move_camera': rt.context_token = 'new-load'
            return SimpleNamespace(structuredContent={'inputSchema': {'type': 'object', 'properties': {'deltaX': {}, 'deltaZ': {}}}})
        rt.bridge.detail.side_effect = detail
    assert (await navigate(rt, **body)).status_code == 400
    assert all(call.args[0] == 'rimworld/get_camera_state' for call in rt.bridge.call.await_args_list)
    assert rt.game.cinematic


@pytest.mark.asyncio
async def test_uncertain_camera_write_is_not_retried_or_reported_successful():
    rt, state = camera_runtime()
    rt.bridge.call.side_effect = [SimpleNamespace(structuredContent=state), ValueError('uncertain native dispatch')]
    assert (await navigate(rt)).status_code == 400
    assert rt.bridge.call.await_count == 2 and not rt.game.cinematic


@pytest.mark.asyncio
async def test_zoom_clamps_to_native_range_and_refuses_extension():
    rt, state = camera_runtime(); state['rootSize'] = 12
    assert (await navigate(rt, action='in')).status_code == 200
    assert rt.bridge.call.await_args_list[1].kwargs == {'rootSize': 12}
    rt.bridge.call.reset_mock(); state['cameraZoomExtensionEnabled'] = True
    assert (await navigate(rt, action='out')).status_code == 400
    assert rt.bridge.call.await_count == 1


@pytest.mark.asyncio
async def test_camera_request_does_not_accept_arbitrary_native_arguments():
    rt, _ = camera_runtime()
    assert (await navigate(rt, deltaX=200)).status_code == 422
    assert (await navigate(rt, action='click')).status_code == 422
    rt.bridge.call.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize('case', ['valid', 'stale', 'headless', 'changed'])
async def test_camera_state_reads_native_geometry_without_writes(case):
    rt, state = camera_runtime()
    if case == 'headless':
        rt.headless = True
    if case == 'changed':
        async def changed(*args, **kwargs):
            rt.context_token = 'load-b'
            return SimpleNamespace(structuredContent=state)
        rt.bridge.call.side_effect = changed
    app = create_app(rt); app.state.rt = rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver') as client:
        response = await client.get('/api/camera/state', params={'session_id': 'old' if case == 'stale' else 'load-a'})
    assert response.status_code == (200 if case == 'valid' else 400)
    if case in ('stale', 'headless'):
        rt.bridge.call.assert_not_awaited()
    else:
        rt.bridge.call.assert_awaited_once_with('rimworld/get_camera_state')
    if case == 'valid':
        assert response.json()['camera'] == state
    assert rt.game.cinematic and rt.mode == 'automate' and rt.chat_revision == 4
    rt.supervisor.change.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize('path, body', [('camera/navigate', {'action': 'right'}), ('time', {'speed': 'Normal'})])
async def test_delayed_released_control_never_dispatches_native_write(path, body):
    rt, _ = camera_runtime(); rt.player_input = None
    app = create_app(rt); app.state.rt = rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver', headers={'X-RimGovernor': '1'}) as client:
        response = await client.post('/api/'+path, json=dict(body, session_id='load-a', viewer_id='old-owner', lease_id='released'))
    assert response.status_code == 400
    assert all(call.args == ('rimworld/get_camera_state',) for call in rt.bridge.call.await_args_list)
    rt.supervisor.change.assert_not_awaited()
