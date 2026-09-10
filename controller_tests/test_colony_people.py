import asyncio
import base64
from types import SimpleNamespace
from unittest.mock import AsyncMock

import httpx
import pytest

from rimbot.bridge_server import create_app


def runtime():
    return SimpleNamespace(lock=asyncio.Lock(), connected=True, context_token='load-a',
        sync_identity=AsyncMock(), headless=False, game=SimpleNamespace(query=AsyncMock()),
        mode='automate', chat_revision=4)


@pytest.mark.asyncio
async def test_read_details_is_cached_without_changing_control():
    rt = runtime()
    rt.game.query.side_effect = [
        {'time': {'ticksGame': 10}}, {'pawns': [{'thingId': 'Pawn1', 'thoughts': {'situationalCacheStale': True}}]},
        {'time': {'ticksGame': 12}}]
    app = create_app(rt); app.state.rt = rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver') as client:
        first = await client.get('/api/people?session_id=load-a')
        second = await client.get('/api/people?session_id=load-a')
        assert first.status_code == second.status_code == 200
        assert first.json() == second.json()
        assert first.json()['endTick'] == 12
        assert first.json()['pawns'][0]['thoughts']['situationalCacheStale']
        assert (await client.get('/api/people?session_id=old')).status_code == 400
    assert rt.game.query.await_count == 3
    assert rt.mode == 'automate' and rt.chat_revision == 4


@pytest.mark.asyncio
async def test_load_change_during_read_never_publishes_old_pawns():
    rt = runtime()
    async def query(tool, **args):
        if tool == 'home/list_pawns':
            rt.context_token = 'load-b'
            return {'pawns': [{'thingId': 'Pawn1'}]}
        return {'time': {'ticksGame': 10}}
    rt.game.query.side_effect = query
    app = create_app(rt); app.state.rt = rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver') as client:
        assert (await client.get('/api/people?session_id=load-a')).status_code == 400
    assert not hasattr(rt, '_people_read')


@pytest.mark.asyncio
async def test_image_identity_cache_headless_and_fixed_native_contract(monkeypatch):
    rt = runtime()
    contract = AsyncMock()
    frame = b'\x89PNG\r\n\x1a\nfixture'
    call = AsyncMock(return_value={'success': True, 'sessionId': 'load-a', 'pawnId': 'Pawn1',
                                 'tick': 12, 'pngBase64': base64.b64encode(frame).decode()})
    monkeypatch.setattr('rimbot.colony_people.camera_contract', contract)
    monkeypatch.setattr('rimbot.colony_people.camera_call', call)
    app = create_app(rt); app.state.rt = rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app), base_url='http://testserver') as client:
        url = '/api/people/Pawn1/image?session_id=load-a&view=follow'
        first = await client.get(url)
        assert first.status_code == 200 and first.content == frame
        assert first.headers['X-Observed-Tick'] == '12'
        assert (await client.get(url)).content == frame
        call.assert_awaited_once_with(rt, 'home/pawn_image',
            {'pawnId': 'Pawn1', 'sessionId': 'load-a', 'view': 'follow'})
        assert (await client.get(url.replace('follow', 'arbitrary'))).status_code == 422
        assert (await client.get(url.replace('load-a', 'old'))).status_code == 400
        rt.headless = True
        assert (await client.get(url)).status_code == 400
        rt.headless = False
        assert (await client.get(url.replace('Pawn1', 'Pawn2'))).status_code == 400
    assert rt.mode == 'automate' and rt.chat_revision == 4
