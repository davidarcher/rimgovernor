"""Container-local display with verified rendering and owned shutdown."""
from dataclasses import dataclass, asdict
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import time


@dataclass(frozen=True)
class DisplaySettings:
    width: int
    height: int
    renderer: str = 'llvmpipe'

    @classmethod
    def parse(cls, resolution, renderer='llvmpipe'):
        if renderer not in ('llvmpipe', 'd3d12'):
            raise ValueError('Unsupported display renderer')
        if not re.fullmatch(r'[0-9]+x[0-9]+', resolution):
            raise ValueError('Display resolution must be WIDTHxHEIGHT')
        width, height = map(int, resolution.split('x'))
        if not (640 <= width <= 3840 and 480 <= height <= 2160):
            raise ValueError('Display dimensions must be within 640x480 and 3840x2160')
        return cls(width, height, renderer)

    def manifest(self):
        return dict(asdict(self), display=':99', depth=24)


def stop(process):
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=45)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def run_display(root, settings, command):
    """Keep X alive through controller/game cleanup; never restart failed children."""
    destination = Path(root)/'display'
    destination.mkdir()
    env = dict(os.environ, DISPLAY=':99', LIBGL_ALWAYS_SOFTWARE='1',
               GALLIUM_DRIVER=settings.renderer, LP_NUM_THREADS='4', RIMGOVERNOR_PRIVATE_DISPLAY='1')
    result = settings.manifest()
    server = child = None
    stopping = False
    def shutdown(signum, frame):
        nonlocal stopping
        stopping = True
        if child is not None and child.poll() is None:
            child.send_signal(signum)
    previous = {sig: signal.signal(sig, shutdown) for sig in (signal.SIGTERM, signal.SIGINT)}
    try:
        with (destination/'Xvfb.log').open('w') as log:
            server = subprocess.Popen(['Xvfb', ':99', '-screen', '0',
                f'{settings.width}x{settings.height}x24', '-nolisten', 'tcp', '-noreset'],
                stdout=log, stderr=subprocess.STDOUT, env=env)
            deadline = time.monotonic()+20
            while True:
                if stopping or server.poll() is not None:
                    raise RuntimeError('Virtual display stopped during startup')
                probe = subprocess.run(['xdpyinfo'], env=env, capture_output=True, timeout=3)
                if probe.returncode == 0:
                    (destination/'xdpyinfo.log').write_bytes(probe.stdout+probe.stderr)
                    break
                if time.monotonic() >= deadline:
                    raise TimeoutError('Virtual display was not ready within 20 seconds')
                time.sleep(.1)
            renderer = subprocess.run(['glxinfo', '-B'], env=env, capture_output=True, timeout=20)
            (destination/'renderer.log').write_bytes(renderer.stdout+renderer.stderr)
            renderer_text = renderer.stdout.lower()
            if (renderer.returncode or settings.renderer.encode() not in renderer_text
                    or (settings.renderer == 'd3d12' and b'accelerated: yes' not in renderer_text)):
                raise RuntimeError(f'Expected the {settings.renderer} OpenGL renderer')
            if stopping:
                raise RuntimeError('Worker stopped before command launch')
            child = subprocess.Popen(command, env=env)
            while child.poll() is None and not stopping:
                if server.poll() is not None:
                    raise RuntimeError('Virtual display exited while the worker was running')
                time.sleep(.1)
            if stopping:
                stop(child)
            result['command_exit_code'] = child.wait()
            code = result['command_exit_code']
            return 128-code if code < 0 else code
    except Exception as error:
        result['error'] = str(error)
        raise
    finally:
        stop(child)
        stop(server)
        result['display_exit_code'] = server.returncode if server else None
        (destination/'result.json').write_text(json.dumps(result, indent=2), encoding='utf8')
        for sig, handler in previous.items():
            signal.signal(sig, handler)
