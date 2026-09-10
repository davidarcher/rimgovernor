"""Explicit player clock and presentation controls, outside model execution."""
from typing import Literal
import math

from fastapi import APIRouter, Request
from pydantic import BaseModel, ConfigDict, Field, StrictBool
from jsonschema import Draft202012Validator
from .bridge import runtime_file_read
from .native_contracts import validate_arguments
from .player_input import InputLease, require_owner, check_player_control
import time

router = APIRouter(prefix='/api')


class PlayerControl(BaseModel):
    model_config = ConfigDict(extra='forbid')
    session_id: str = Field(min_length=1, max_length=300)
    viewer_id: str = Field(default='', max_length=100)
    lease_id: str = Field(default='', max_length=100)


class TakeControl(PlayerControl):
    viewer_id: str = Field(min_length=1, max_length=100)


class ReleaseControl(TakeControl):
    resume: StrictBool = False


@router.post('/input/take')
async def take_control(body: TakeControl, request: Request):
    rt = request.app.state.rt
    async with rt.lock:
        await check_session(rt, body.session_id)
        old = getattr(rt, 'player_input', None)
        if old and old.live() and old.viewer != body.viewer_id:
            raise ValueError('Another viewer has player control')
        if not rt.supervisor:
            raise ValueError('Native clock supervision is unavailable')
        lease = InputLease.create(body.session_id, body.viewer_id)
        rt.player_input = lease
        rt.mode, rt.resume_after_review = 'manual', False
        rt.execution_window_end, rt.execution_wait_explicit = None, False
        rt.chat_revision += 1
        direction = rt.chat_revision
        rt.manual_requests.clear()
        rt.manual_execution = None
        rt.game.cinematic = False
        rt.phase = 'Manual'
        rt.persist()
        async def guard():
            await check_session(rt, body.session_id)
            if rt.chat_revision != direction:
                raise InterruptedError('New player direction arrived during handoff')
        await rt.supervisor.change('Paused')
        await guard()
        released = await rt.release_drafts(guard=guard)
        if released.get('failed'):
            raise ValueError('Owned drafts could not be released; player control was not acknowledged')
        await guard()
        rt.clock = (await rt.game.query('home/status', colonists=False, threats=False))['time']
        await guard()
        if rt.clock.get('paused') is not True:
            raise ValueError('Native pause was not confirmed; player control was not acknowledged')
        lease.ready, lease.deadline = True, time.monotonic() + 15
        return {'lease_id': lease.token, 'mode': 'manual'}


@router.post('/input/heartbeat')
async def input_heartbeat(body: TakeControl, request: Request):
    rt = request.app.state.rt
    async with rt.lock:
        await check_session(rt, body.session_id)
        lease = require_owner(rt, body.session_id, body.viewer_id, body.lease_id)
        lease.deadline = time.monotonic() + 15
        return {'active': True}


@router.post('/input/release')
async def release_control(body: ReleaseControl, request: Request):
    rt = request.app.state.rt
    await rt.set_mode('automate' if body.resume else 'manual',
                      player_owner=(body.session_id, body.viewer_id, body.lease_id))
    return {'mode': rt.mode}


class TimeControl(PlayerControl):
    speed: Literal['Paused', 'Normal', 'Fast', 'Superfast']


class CameraControl(PlayerControl):
    following: StrictBool


class CameraNavigation(PlayerControl):
    action: Literal['left', 'right', 'up', 'down', 'in', 'out']


async def camera_contract(rt, tool, arguments):
    detail = await runtime_file_read(rt.bridge.detail, tool)
    schema = dict(detail.structuredContent['inputSchema'], additionalProperties=False)
    Draft202012Validator.check_schema(schema)
    validate_arguments(tool, schema, arguments)


async def camera_call(rt, tool, arguments):
    # Relative input is never retried after an uncertain native dispatch.
    result = await rt.bridge.call(tool, **arguments)
    payload = result.structuredContent
    if (getattr(result, 'isError', False) or not isinstance(payload, dict)
            or payload.get('success') is not True or payload.get('unknownArguments')):
        raise ValueError('Camera request was not confirmed; inspect the view before retrying')
    return payload


@router.post('/camera/navigate')
async def camera_navigate(body: CameraNavigation, request: Request):
    rt = request.app.state.rt
    async with rt.lock:
        await check_session(rt, body.session_id)
        if rt.headless:
            raise ValueError('Camera navigation needs a rendered game')
        read = 'rimworld/get_camera_state'
        await camera_contract(rt, read, {})
        await check_session(rt, body.session_id)
        before = await camera_call(rt, read, {})
        if not before.get('mapId'):
            raise ValueError('Native camera has no current map')
        if body.action in ('in', 'out'):
            limits = before.get('sizeRange', {})
            values = [limits.get('min'), limits.get('max'), before.get('rootSize')]
            if not all(type(v) in (int, float) and math.isfinite(v) for v in values):
                raise ValueError('Native camera zoom range is unavailable')
            low, high, current = values
            if low > high or before.get('cameraZoomExtensionEnabled') is not False:
                raise ValueError('Camera navigation requires the normal native zoom range')
            tool = 'rimworld/set_camera_zoom'
            arguments = {'rootSize': max(low, min(high, current + (-2 if body.action == 'in' else 2)))}
        else:
            dx, dz = {'left': (-10, 0), 'right': (10, 0), 'up': (0, 10), 'down': (0, -10)}[body.action]
            tool, arguments = 'rimworld/move_camera', {'deltaX': dx, 'deltaZ': dz}
        await camera_contract(rt, tool, arguments)
        await check_session(rt, body.session_id)
        check_player_control(rt, body.session_id, body.viewer_id, body.lease_id)
        rt.game.cinematic = False
        await camera_call(rt, tool, arguments)
        await check_session(rt, body.session_id)
        after = await camera_call(rt, read, {})
        await check_session(rt, body.session_id)
        if not before.get('mapId') or before['mapId'] != after.get('mapId'):
            raise ValueError('Camera map changed; refresh before using controls')
        return {'camera': after, 'following': False}


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
        check_player_control(rt, body.session_id, body.viewer_id, body.lease_id)
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
        check_player_control(rt, body.session_id, body.viewer_id, body.lease_id)
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
        check_player_control(rt, body.session_id, body.viewer_id, body.lease_id)
        if body.following and getattr(rt, 'player_input', None) is not None:
            raise ValueError('Release player control before enabling action follow')
        if rt.headless and body.following:
            raise ValueError('Action follow needs a rendered game')
        rt.game.cinematic = body.following
        return {'following': body.following}
