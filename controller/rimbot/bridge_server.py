"""Local overlay and interactive planner for the replacement backend."""
from contextlib import asynccontextmanager
from pathlib import Path
import os
from urllib.parse import urlparse

from fastapi import FastAPI, Request
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from starlette.middleware.trustedhost import TrustedHostMiddleware
from .bridge_runtime import BridgeRuntime
from .config import DATA_DIR, Settings, load_model_routing
from .store import Store


def create_app(runtime=None):
    @asynccontextmanager
    async def lifespan(app):
        rt = runtime or BridgeRuntime(Store(DATA_DIR/'bridge.sqlite'),
            os.environ.get('RIMBOT_BRIDGE_ROOT', '.rimbot/bridge'),
            fresh=os.environ.get('RIMBOT_BRIDGE_FRESH') == '1',
            headless=os.environ.get('RIMBOT_HEADLESS') == '1',
            settings=Settings(model=os.environ.get('RIMBOT_MODEL', 'qwen3.5-9b')),
            routing=load_model_routing(Settings(model=os.environ.get('RIMBOT_MODEL', 'qwen3.5-9b')), os.environ.get('RIMBOT_MODELS_CONFIG')))
        app.state.rt = rt
        await rt.start()
        try:
            yield
        finally:
            await rt.stop()
            if runtime is None:
                rt.store.close()
    app = FastAPI(title='RimBot live colony', lifespan=lifespan)
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
        response.headers['Content-Security-Policy'] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'"
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

    @app.get('/api/camera')
    async def camera(request: Request):
        rt = request.app.state.rt
        if not rt.camera_path:
            return JSONResponse({'detail': 'Waiting for camera'}, status_code=503)
        return FileResponse(rt.camera_path, media_type='image/png')

    @app.get('/api/diagnostics')
    async def diagnostics(request: Request):
        rt = request.app.state.rt
        return {'events': rt.store.history(rt.colony, 100, include_diagnostics=True)}
    return app
