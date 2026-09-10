"""Local overlay and interactive planner for the replacement backend."""
from contextlib import asynccontextmanager
from pathlib import Path
import os
import time
from urllib.parse import urlparse

from fastapi import FastAPI, Request
from pydantic import BaseModel, ConfigDict, Field
from fastapi.responses import FileResponse, JSONResponse, Response
from fastapi.staticfiles import StaticFiles
from starlette.middleware.trustedhost import TrustedHostMiddleware
from .bridge_runtime import BridgeRuntime
from .config import DATA_DIR, Settings, load_model_routing
from .store import Store
from .controller_settings import PolicyUpdate, update_policy
from .dashboard_controls import router as dashboard_controls
from .session_checkpoint import create_checkpoint, stop_for_restart
from .video_stream import VideoHub, router as video_routes


class CheckpointRequest(BaseModel):
    model_config = ConfigDict(extra='forbid')
    session_id: str = Field(min_length=1, max_length=300)


class CheckpointStop(CheckpointRequest):
    manifest_path: str = Field(min_length=1, max_length=2000)


class ForgetMemory(BaseModel):
    model_config = ConfigDict(extra='forbid')
    session_id: str = Field(min_length=1, max_length=300)
    version: str = Field(pattern=r'^[a-f0-9]{64}$')


def create_app(runtime=None):
    @asynccontextmanager
    async def lifespan(app):
        rt = runtime or BridgeRuntime(Store(DATA_DIR/'bridge.sqlite'),
            os.environ.get('RIMBOT_BRIDGE_ROOT', '.rimbot/bridge'),
            fresh=os.environ.get('RIMBOT_BRIDGE_FRESH') == '1',
            resume=os.environ.get('RIMBOT_RESUME_CHECKPOINT'),
            headless=os.environ.get('RIMBOT_HEADLESS') == '1',
            settings=Settings(model=os.environ.get('RIMBOT_MODEL', 'qwen3.5-9b'), model_url=os.environ.get('RIMBOT_MODEL_URL', 'http://127.0.0.1:1234/v1')),
            routing=load_model_routing(Settings(model=os.environ.get('RIMBOT_MODEL', 'qwen3.5-9b'), model_url=os.environ.get('RIMBOT_MODEL_URL', 'http://127.0.0.1:1234/v1')), os.environ.get('RIMBOT_MODELS_CONFIG')))
        app.state.rt = rt
        app.state.video = VideoHub(rt)
        await rt.start()
        try:
            yield
        finally:
            await app.state.video.close()
            await rt.stop()
            if runtime is None:
                rt.store.close()
    app = FastAPI(title='RimBot live colony', lifespan=lifespan)
    app.include_router(dashboard_controls)
    app.include_router(video_routes)
    if runtime is not None:
        app.state.video = VideoHub(runtime)
    app.add_middleware(TrustedHostMiddleware, allowed_hosts=['127.0.0.1', 'localhost', '[::1]', 'testserver'])
    assets = Path(__file__).parent/'static'
    app.mount('/assets', StaticFiles(directory=assets/'assets'), name='overlay-assets')

    @app.middleware('http')
    async def local_only(request, call_next):
        if request.method not in ('GET', 'HEAD'):
            origin = request.headers.get('origin')
            if request.headers.get('x-rimbot') != '1' or (origin and urlparse(origin).netloc != request.headers.get('host')):
                return JSONResponse({'detail': 'Use the local colony dashboard'}, status_code=403)
        response = await call_next(request)
        response.headers['Cache-Control'] = 'no-store'
        response.headers['Content-Security-Policy'] = "default-src 'self'; img-src 'self' data:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'"
        return response

    @app.exception_handler(ValueError)
    async def invalid(request, error):
        return JSONResponse({'detail': str(error)}, status_code=400)

    @app.get('/')
    async def index():
        return FileResponse(assets/'index.html')

    @app.get('/state')
    @app.get('/api/state')
    async def state(request: Request):
        return request.app.state.rt.public()

    @app.get('/api/health')
    async def health():
        return {'service': 'rimbot', 'backend': 'rimbridge', 'pid': os.getpid(), 'source_root': str(Path(__file__).resolve().parents[2])}

    @app.post('/api/chat', status_code=202)
    async def chat(request: Request):
        await request.app.state.rt.steer((await request.json())['text'])
        return {'accepted': True}

    @app.post('/api/control')
    async def control(request: Request):
        await request.app.state.rt.set_mode((await request.json())['mode'])
        return {'ok': True}

    @app.post('/api/autopilot/settings')
    async def autopilot_settings(body: PolicyUpdate, request: Request):
        return await update_policy(request.app.state.rt, body)

    @app.post('/api/session/checkpoint')
    async def checkpoint(body: CheckpointRequest, request: Request):
        return await create_checkpoint(request.app.state.rt, body.session_id)

    @app.post('/api/session/stop')
    async def stop_checkpoint(body: CheckpointStop, request: Request):
        return await stop_for_restart(request.app.state.rt, body.session_id, body.manifest_path)

    @app.post('/api/projects')
    async def project(request: Request):
        row = await request.app.state.rt.project_update(await request.json())
        await request.app.state.rt.steer('Player objective: '+row['title'])
        return row

    @app.delete('/api/projects/{identity}')
    async def cancel(identity: str, request: Request):
        return await request.app.state.rt.cancel_project(identity)

    @app.delete('/api/plan/steps/{identity}')
    async def cancel_step(identity: str, request: Request):
        await request.app.state.rt.cancel_plan_step(identity)
        return {'cancelled': identity}

    @app.delete('/api/memories/{identity}')
    async def forget_memory(identity: str, body: ForgetMemory, request: Request):
        return await request.app.state.rt.forget_memory(identity, body.session_id, body.version)

    @app.post('/api/video')
    async def video(request: Request):
        body = await request.json()
        viewer = body.get('viewer')
        if not isinstance(viewer, str) or not 1 <= len(viewer) <= 80 or not isinstance(body.get('playing'), bool):
            raise ValueError('Provide viewer ID and playing flag')
        rt = request.app.state.rt
        now = time.monotonic()
        if not request.app.state.video.accept_heartbeat(viewer, body.get('revision')):
            return {'playing': rt.video_viewers.get(viewer, 0) > now, 'ignored': True}
        rt.video_viewers = {key:until for key,until in rt.video_viewers.items() if until > now}
        if body['playing']:
            if viewer not in rt.video_viewers and len(rt.video_viewers) >= 32:
                raise ValueError('Too many video viewers')
            rt.video_viewers[viewer] = now + 8
        else:
            rt.video_viewers.pop(viewer, None)
            await request.app.state.video.drop(viewer)
        return {'playing': body['playing']}

    @app.get('/api/camera')
    async def camera(request: Request):
        rt = request.app.state.rt
        frame = rt.camera_bytes
        if frame is None:
            return JSONResponse({'detail': 'Waiting for camera'}, status_code=503)
        return Response(frame, media_type='image/png')

    @app.get('/api/diagnostics')
    async def diagnostics(request: Request):
        rt = request.app.state.rt
        return {'events': rt.store.history(rt.colony, 100, include_diagnostics=True)}
    return app
