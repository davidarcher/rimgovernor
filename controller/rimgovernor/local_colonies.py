"""Read-only local Docker worker directory; never starts or controls a colony."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import httpx

from fastapi import APIRouter, FastAPI
from fastapi.responses import FileResponse, RedirectResponse
from fastapi.staticfiles import StaticFiles
from starlette.middleware.trustedhost import TrustedHostMiddleware

router = APIRouter()


def docker_executable():
    executable = shutil.which('docker')
    if executable:
        return executable
    if os.name == 'nt':
        for base, suffix in (('LOCALAPPDATA', 'Programs/DockerDesktop/resources/bin/docker.exe'),
                             ('ProgramFiles', 'Docker/Docker/resources/bin/docker.exe')):
            if os.environ.get(base):
                candidate = Path(os.environ[base])/suffix
                if candidate.is_file():
                    return str(candidate)
    raise OSError('Docker executable unavailable')


def worker_entries(containers):
    entries = []
    for container in containers:
        config = container.get('Config') or {}
        labels = config.get('Labels') or {}
        command = (config.get('Entrypoint') or []) + (config.get('Cmd') or [])
        if labels.get('io.rimgovernor.colony') != '1' and 'rimgovernor.container_worker' not in command:
            continue
        if not container.get('State', {}).get('Running'):
            continue
        ports = container.get('NetworkSettings', {}).get('Ports') or {}
        url = None
        for binding in ports.get('8787/tcp') or []:
            port = str(binding.get('HostPort', ''))
            if binding.get('HostIp') in ('127.0.0.1', '0.0.0.0', '::', '::1') and port.isdecimal() and 0 < int(port) < 65536:
                host = '[::1]' if binding['HostIp'] in ('::', '::1') else '127.0.0.1'
                url = f'http://{host}:{int(port)}/'
                break
        environment = dict(item.split('=', 1) for item in config.get('Env', []) if '=' in item)
        entries.append({'id': container['Id'],
                        'name': labels.get('io.rimgovernor.colony.name') or container['Name'].lstrip('/'),
                        'url': url, 'display': environment.get('RIMGOVERNOR_DISPLAY', 'headless'),
                        'startedAt': container['State'].get('StartedAt', '')})
    return sorted(entries, key=lambda item: (item['name'].casefold(), item['id']))


@router.get('/api/colonies')
def colonies():
    # A synchronous route runs in the server thread pool, off the game event loop.
    try:
        if Path('/var/run/docker.sock').is_socket():
            with httpx.Client(transport=httpx.HTTPTransport(uds='/var/run/docker.sock'),
                              base_url='http://docker', timeout=5) as client:
                response = client.get('/containers/json')
                response.raise_for_status()
                containers = []
                for item in response.json():
                    response = client.get(f"/containers/{item['Id']}/json")
                    if response.status_code == 404:
                        continue  # A short-lived test finished during discovery.
                    response.raise_for_status()
                    containers.append(response.json())
                return {'colonies': worker_entries(containers), 'error': None}
        docker = docker_executable()
        def run(*args):
            return subprocess.run([docker, *args], capture_output=True, text=True,
                                  check=True, timeout=5).stdout
        ids = run('ps', '-q').split()
        containers = json.loads(run('inspect', *ids)) if ids else []
        return {'colonies': worker_entries(containers), 'error': None}
    except (OSError, subprocess.SubprocessError, httpx.HTTPError, ValueError, KeyError, TypeError):
        return {'colonies': None, 'error': 'Local discovery is unavailable. The directory needs access to the local Docker engine.'}


def create_directory_app():
    """Serve the directory without creating a controller, database or game session."""
    app = FastAPI(title='Outpost local colonies')
    app.add_middleware(TrustedHostMiddleware, allowed_hosts=['127.0.0.1', 'localhost', '[::1]', 'testserver'])
    app.include_router(router)
    assets = Path(__file__).parent/'static'
    app.mount('/assets', StaticFiles(directory=assets/'assets'))

    @app.middleware('http')
    async def headers(request, call_next):
        response = await call_next(request)
        response.headers['Cache-Control'] = 'no-store'
        response.headers['Content-Security-Policy'] = "default-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'"
        return response

    @app.get('/')
    def index():
        return RedirectResponse('/colonies')

    @app.get('/colonies')
    def directory():
        return FileResponse(assets/'index.html')

    return app
