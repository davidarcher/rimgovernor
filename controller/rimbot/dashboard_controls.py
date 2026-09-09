"""Explicit player clock and presentation controls, outside model execution."""
from typing import Literal

from fastapi import APIRouter, Request
from pydantic import BaseModel, ConfigDict, Field, StrictBool

router = APIRouter(prefix='/api')


class PlayerControl(BaseModel):
    model_config = ConfigDict(extra='forbid')
    session_id: str = Field(min_length=1, max_length=300)


class TimeControl(PlayerControl):
    speed: Literal['Paused', 'Normal', 'Fast', 'Superfast']


class CameraControl(PlayerControl):
    following: StrictBool


async def check_session(rt, session_id):
    if getattr(rt, 'session_closing', False):
        raise ValueError('Session is restarting; wait for reconnection')
    if not rt.connected:
        raise ValueError('Wait for the colony to connect')
    await rt.sync_identity()
    if session_id != rt.context_token:
        raise ValueError('Loaded colony changed; refresh before using controls')


@router.post('/time')
async def player_time(body: TimeControl, request: Request):
    rt = request.app.state.rt
    async with rt.lock:
        await check_session(rt, body.session_id)
        if not rt.supervisor:
            raise ValueError('Native clock supervision is unavailable')
        if body.speed != 'Paused' and (
                any(task is not None and not task.done() for task in
                    (getattr(rt, 'review_task', None), getattr(rt, 'execution_task', None)))
                or (getattr(rt, 'wake', None) is not None and rt.wake.is_set())):
            raise ValueError('Pause the game and let the current review finish before resuming time')
        # Take player ownership before awaiting native work. Existing asynchronous
        # plans fail their direction guards even if the native request fails.
        rt.mode, rt.resume_after_review = 'manual', False
        rt.execution_window_end = None
        rt.execution_wait_explicit = False
        rt.chat_revision += 1
        direction = rt.chat_revision
        rt.manual_requests.clear()
        rt.manual_execution = None
        rt.phase = 'Manual'
        rt.persist()
        await rt.supervisor.change('Paused')
        await rt.release_drafts()
        await check_session(rt, body.session_id)
        if rt.chat_revision != direction:
            raise ValueError('New player direction arrived; time remains paused')
        rt.supervisor.allow_resume()
        result = await rt.supervisor.change(body.speed)
        rt.clock = (await rt.game.query('home/status', colonists=False, threats=False))['time']
        rt.note('player_clock', 'Player '+('paused the game' if body.speed == 'Paused' else 'requested '+body.speed.lower()+' speed'))
        rt.persist()
        return {'game': rt.clock, 'clock': result, 'mode': rt.mode}


@router.post('/camera/follow')
async def camera_follow(body: CameraControl, request: Request):
    rt = request.app.state.rt
    async with rt.lock:
        await check_session(rt, body.session_id)
        if rt.headless and body.following:
            raise ValueError('Action follow needs a rendered game')
        rt.game.cinematic = body.following
        return {'following': body.following}
