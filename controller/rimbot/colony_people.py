"""Demand-driven native people reads for the dashboard, outside model execution."""
import base64
import time
from typing import Literal

from fastapi import APIRouter, Query, Request
from fastapi.responses import Response

from .dashboard_controls import check_session, camera_contract, camera_call
from .bridge import runtime_file_read

router = APIRouter(prefix='/api/people')


@router.get('')
async def people(request: Request, session_id: str = Query(min_length=1, max_length=300)):
    rt = request.app.state.rt
    async with rt.lock:
        await check_session(rt, session_id)
        cached = getattr(rt, '_people_read', None)
        if cached and cached[0] == session_id and time.monotonic() - cached[1] < 2:
            return cached[2]
        before = await rt.game.query('home/status', colonists=False, threats=False)
        result = await rt.game.query('home/list_pawns', colonistsOnly=True, health=True,
                                     needs=True, equipment=True, bio=True, thoughts=True)
        after = await rt.game.query('home/status', colonists=False, threats=False)
        await check_session(rt, session_id)
        if not isinstance(result.get('pawns'), list):
            raise ValueError('Colonist details are unavailable')
        payload = {'sessionId': session_id, 'pawns': result['pawns'],
                   'startTick': before['time']['ticksGame'],
                   'endTick': after['time']['ticksGame'], 'observedAt': time.time()}
        rt._people_read = (session_id, time.monotonic(), payload)
        return payload


@router.get('/{pawn_id}/image')
async def pawn_image(request: Request, pawn_id: str,
                     session_id: str = Query(min_length=1, max_length=300),
                     view: Literal['portrait', 'follow'] = 'portrait'):
    rt = request.app.state.rt
    if not 1 <= len(pawn_id) <= 100:
        raise ValueError('Invalid colonist identity')
    async with rt.lock:
        await check_session(rt, session_id)
        if rt.headless:
            raise ValueError('Pawn images need a rendered game')
        cache = getattr(rt, '_people_images', {})
        now = time.monotonic()
        cache = {key: row for key, row in cache.items()
                 if key[0] == session_id and now - row[0] < 15}
        key = (session_id, pawn_id, view)
        cached = cache.get(key)
        if cached and now - cached[0] < (15 if view == 'portrait' else 1):
            return Response(cached[1], media_type='image/png', headers=cached[2])
        tool = 'home/pawn_image'
        args = {'pawnId': pawn_id, 'sessionId': session_id, 'view': view}
        await camera_contract(rt, tool, args)
        result = await runtime_file_read(camera_call, rt, tool, args)
        await check_session(rt, session_id)
        if result.get('pawnId') != pawn_id or result.get('sessionId') != session_id:
            raise ValueError('Pawn image belongs to a different colony or colonist')
        encoded = result.get('pngBase64', '')
        if not isinstance(encoded, str) or len(encoded) > 4_000_000:
            raise ValueError('Invalid pawn image')
        try:
            frame = base64.b64decode(encoded, validate=True)
        except ValueError as exc:
            raise ValueError('Invalid pawn image') from exc
        if not frame.startswith(b'\x89PNG\r\n\x1a\n'):
            raise ValueError('Invalid pawn image')
        headers = {'X-Observed-Tick': str(result['tick'])}
        if len(cache) >= 128:
            cache.pop(next(iter(cache)))
        cache[key] = (time.monotonic(), frame, headers)
        rt._people_images = cache
        return Response(frame, media_type='image/png', headers=headers)
