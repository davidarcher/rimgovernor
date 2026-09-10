"""Serve the real dashboard with synthetic frames; never start or contact a game."""
import argparse
import asyncio
import io
import time
from pathlib import Path
from types import SimpleNamespace

import av
import uvicorn
from fastapi import Request
from fastapi.responses import HTMLResponse, Response
import rimgovernor.video_stream as video
from rimgovernor.bridge_server import create_app


def fixture_app():
    state = {'stalled': False, 'width': 640, 'height': 360, 'generation': 1}
    started = time.monotonic()

    class Frames:
        def __init__(self, name):
            self.closed = False

        def read(self, previous=0):
            sequence = int((time.monotonic() - started) * 30) + 1
            if state['stalled'] or sequence == previous:
                return None
            width, height = state['width'], state['height']
            rows = [bytes((sequence % 200 + 30, y * 200 // height + 30, 130)) * width
                    for y in range(height)]
            return sequence, width, height, time.time(), b''.join(rows)

        def close(self):
            self.closed = True

    # This replacement exists only in this explicitly synthetic fixture process.
    video.RawFrames = Frames

    async def noop(*args, **kwargs):
        pass

    async def native(*args, **kwargs):
        return SimpleNamespace(structuredContent={'name': 'synthetic', 'capacity': video.CAPACITY, 'supported': True})

    rt = SimpleNamespace(connected=True, headless=False, session_closing=False,
                         context_token='synthetic-1', video_viewers={}, bridge=SimpleNamespace(call=native),
                         start=noop, stop=noop, camera_bytes=None, camera_version=1,
                         store=SimpleNamespace(history=lambda *args, **kwargs: []), colony='synthetic')

    def public():
        return {'sessionId': rt.context_token, 'connected': True, 'headless': False,
                'mode': 'manual', 'mood': 'happy', 'cameraVersion': rt.camera_version,
                'goals': {'long': 'Synthetic video acceptance fixture', 'short': 'No game connected'},
                'feed': [], 'status': {'label': 'Synthetic video'},
                'game': {'paused': True, 'stale': False}, 'counters': {'tools': 0, 'actions': 0, 'model_calls': 0}}

    rt.public = public
    frame = video.video_frame((1, 64, 36, time.time(), bytes((50, 90, 150)) * 64 * 36))
    output = io.BytesIO()
    with av.open(output, mode='w', format='image2pipe') as container:
        stream = container.add_stream('png')
        stream.width, stream.height, stream.pix_fmt = 64, 36, 'rgb24'
        for packet in stream.encode(frame):
            container.mux(packet)
    rt.camera_bytes = output.getvalue()
    app = create_app(rt)

    @app.get('/fixture')
    async def page():
        path = Path(video.__file__).parent / 'static/index.html'
        controls = '<script src="/fixture/controls.js"></script>'
        return HTMLResponse(path.read_text(encoding='utf8').replace('</body>', controls + '</body>'))

    @app.get('/fixture/controls.js')
    async def controls():
        return Response('''
const panel = document.createElement('div');
panel.style.cssText = 'position:fixed;bottom:8px;right:8px;z-index:9999;background:#fff;color:#111;padding:10px;border:2px solid #000';
panel.textContent = 'Synthetic fixture: ';
for (const [label, action] of [['Stall frames','stall'],['Resume frames','resume'],['Change session','load'],['Resize frames','resize']]) {
 const button = document.createElement('button'); button.textContent = label;
 button.onclick = () => fetch('/fixture/control', {method:'POST',headers:{'X-RimGovernor':'1','Content-Type':'application/json'},body:JSON.stringify({action})});
 panel.append(button);
}
document.body.append(panel);
''', media_type='application/javascript')

    @app.post('/fixture/control')
    async def control(request: Request):
        action = (await request.json()).get('action')
        if action == 'stall':
            state['stalled'] = True
        elif action == 'resume':
            state['stalled'] = False
        elif action == 'load':
            state['generation'] += 1
            rt.context_token = 'synthetic-' + str(state['generation'])
        elif action == 'resize':
            state['width'], state['height'] = (360, 640) if state['width'] == 640 else (640, 360)
        else:
            raise ValueError('Unknown fixture action')
        return state

    return app


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8791)
    args = parser.parse_args()
    uvicorn.run(fixture_app(), host='127.0.0.1', port=args.port, log_level='warning')
