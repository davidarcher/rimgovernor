from types import SimpleNamespace
from unittest.mock import AsyncMock
import httpx
import pytest
from rimbot.bridge_server import create_app
from rimbot.bridge_game import BridgeGame

@pytest.mark.asyncio
async def test_only_native_routes_and_local_mutations():
    rt=SimpleNamespace(steer=AsyncMock(),set_mode=AsyncMock())
    app=create_app(rt)
    app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver') as client:
        assert (await client.get('/api/health')).json()['backend']=='rimbridge'
        assert (await client.get('/rimapi/api/v1/map')).status_code==404
        assert (await client.post('/api/chat',json={'text':'hello'})).status_code==403
        assert (await client.post('/api/chat',json={'text':'hello'},headers={'X-RimBot':'1','Origin':'http://external.example'})).status_code==403
        assert (await client.post('/api/chat',json={'text':'hello'},headers={'X-RimBot':'1'})).status_code==202
        rt.steer.assert_awaited_once_with('hello')

@pytest.mark.asyncio
async def test_manual_and_invalid_actions_never_reach_game():
    bridge=SimpleNamespace(call=AsyncMock())
    game=BridgeGame(bridge)
    game.schemas['home/place_building']={'type':'object','properties':{'dryRun':{'type':'boolean'},'godMode':{'type':'boolean'}}}
    with pytest.raises(ValueError,match='Automation is off'):
        await game.invoke('home/place_building',{'dryRun':False})
    with pytest.raises(ValueError,match='normal gameplay'):
        await game.invoke('home/place_building',{'dryRun':False,'godMode':True},allow_write=True)
    with pytest.raises(ValueError,match='dryRun explicitly'):
        await game.invoke('home/place_building',{},allow_write=True)
    bridge.call.assert_not_awaited()


@pytest.mark.asyncio
async def test_notebook_delete_requires_local_header_and_versioned_body():
    rt=SimpleNamespace(forget_memory=AsyncMock(return_value={'deleted':'camp'}))
    app=create_app(rt)
    app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver') as client:
        body={'session_id':'colony-load','version':'a'*64}
        assert (await client.request('DELETE','/api/memories/camp',json=body)).status_code==403
        assert (await client.request('DELETE','/api/memories/camp',json={},headers={'X-RimBot':'1'})).status_code==422
        response=await client.request('DELETE','/api/memories/camp',json=body,headers={'X-RimBot':'1'})
        assert response.json()=={'deleted':'camp'}
        rt.forget_memory.assert_awaited_once_with('camp','colony-load','a'*64)
