"""Passive HTTP observation of the scenario's existing runtime, on its event loop."""
import asyncio
from contextlib import contextmanager
import os
from pathlib import Path
import socket
import weakref

from fastapi import FastAPI
from fastapi.responses import FileResponse, JSONResponse, RedirectResponse, Response
from fastapi.staticfiles import StaticFiles
from starlette.middleware.trustedhost import TrustedHostMiddleware
import uvicorn


class Observer:
    def __init__(self):
        self.runtime = lambda: None
        self.server = None
        self.task = None

    def current(self):
        rt = self.runtime()
        return rt if rt is not None and not rt.stopped else None

    def app(self):
        app = FastAPI(title='Outpost scenario observer')
        app.add_middleware(TrustedHostMiddleware, allowed_hosts=['127.0.0.1', 'localhost', '[::1]', 'testserver'])
        assets = Path(__file__).parent/'static'
        app.mount('/assets', StaticFiles(directory=assets/'assets'))

        @app.middleware('http')
        async def read_only(request, call_next):
            if request.method not in ('GET', 'HEAD'):
                return JSONResponse({'detail': 'Scenario dashboards are observation-only'}, status_code=403)
            response = await call_next(request)
            response.headers['Cache-Control'] = 'no-store'
            response.headers['Content-Security-Policy'] = "default-src 'self'; img-src 'self' blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'"
            return response

        @app.get('/')
        def index():
            return RedirectResponse('/scenario')

        @app.get('/scenario')
        def page():
            return FileResponse(assets/'index.html')

        @app.get('/api/state')
        async def state():
            rt = self.current()
            if rt is None:
                return JSONResponse({'detail': 'Waiting for the next scenario runtime'}, status_code=503)
            # No fresh native reads, locks, viewer leases or changes to simulation.
            return dict(rt.public(), observationOnly=True)

        @app.get('/api/camera')
        async def camera(session_id: str):
            rt = self.current()
            if rt is None or session_id != (rt.context_token or rt.colony):
                return JSONResponse({'detail': 'Scenario session changed'}, status_code=409)
            if rt.camera_bytes is None:
                return JSONResponse({'detail': 'No retained frame'}, status_code=404)
            return Response(rt.camera_bytes, media_type='image/png')

        return app

    def start(self, host='0.0.0.0', port=8787):
        # Bind synchronously: a port collision fails the launcher instead of silently
        # presenting a different test's runtime at the advertised address.
        sock = socket.socket()
        try:
            sock.bind((host, port))
            sock.listen(128)
            sock.setblocking(False)
            class Server(uvicorn.Server):
                @contextmanager
                def capture_signals(self):
                    yield  # The scenario retains signal and cleanup ownership.
            self.server = Server(uvicorn.Config(self.app(), log_level='warning', lifespan='off'))
            async def serve():
                try:
                    await self.server.serve(sockets=[sock])
                except asyncio.CancelledError:
                    await self.server.shutdown(sockets=[sock])
                    raise
                finally:
                    sock.close()
            self.task = asyncio.create_task(serve())
        except BaseException:
            sock.close()
            raise


_observers = weakref.WeakKeyDictionary()


def attach(runtime):
    if os.environ.get('RIMBOT_SCENARIO_DASHBOARD') != '1':
        return
    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:
        return  # start() will register runtimes constructed before asyncio.run().
    observer = _observers.get(loop)
    if observer is None:
        observer = Observer()
        observer.start()
        _observers[loop] = observer
    observer.runtime = weakref.ref(runtime)
