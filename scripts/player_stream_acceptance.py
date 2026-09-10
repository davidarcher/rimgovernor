"""Native input fault acceptance against interactive_view_acceptance.py."""
import argparse
import asyncio
import json
import struct
import time
from pathlib import Path
from urllib.parse import urlencode

import httpx
from websockets.asyncio.client import connect


class View:
    def __init__(self, base, session, viewer):
        self.url = base.replace('http:', 'ws:') + '/api/video/frames?' + urlencode(
            {'session_id': session, 'viewer': viewer, 'connection_id': viewer + '-acceptance', 'hardware': 'true'})
        self.base = base
        self.latest = None
        self.count = 0

    async def open(self):
        self.socket = await connect(self.url, origin=self.base, subprotocols=['rimbot-view-v1'], max_size=16000000)
        self.task = asyncio.create_task(self.read())
        await self.fresh()
        return self

    async def read(self):
        async for packet in self.socket:
            size, = struct.unpack_from('<I', packet)
            self.latest = json.loads(packet[4:4 + size])
            self.count += 1
            await self.socket.send(json.dumps({'frame': self.latest['frame'], 'displayed': time.time()}))

    async def fresh(self):
        previous = self.count
        async with asyncio.timeout(5):
            while self.count == previous:
                await asyncio.sleep(.01)
        return dict(self.latest)

    async def close(self):
        await self.socket.close()
        await asyncio.gather(self.task, return_exceptions=True)


async def run(base, output, gesture_only=False):
    evidence = {'checks': []}
    views = []
    async with httpx.AsyncClient(base_url=base, headers={'X-RimBot': '1'}, timeout=120) as client:
        async def post(path, body, expected=200):
            response = await client.post(path, json=body)
            assert response.status_code == expected, (path, response.status_code, response.text)
            return response.json()

        async def clear(label):
            async with asyncio.timeout(4):
                while True:
                    held = (await client.get('/test/held')).json()
                    if held == {'buttons': 0, 'keys': []}:
                        evidence['checks'].append({'name': label, 'held': held})
                        return
                    await asyncio.sleep(.1)

        session = (await client.get('/api/state')).json()['sessionId']
        credentials = {'session_id': session, 'viewer_id': 'acceptance-a'}
        async def take():
            result = await post('/api/input/take', {'session_id': session, 'viewer_id': 'acceptance-a'})
            assert result['direct_input'] is True
            credentials['lease_id'] = result['lease_id']
            await asyncio.sleep(.25)
            return result

        async def event(view, order, kind, **fields):
            frame = await view.fresh()
            body = dict(credentials, source=frame['source'], frame=frame['frame'], order=order, kind=kind, x=800, y=250)
            body.update(fields)
            return await post('/api/input/event', body)

        try:
            a = await View(base, session, 'acceptance-a').open(); views.append(a)
            b = await View(base, session, 'acceptance-b').open(); views.append(b)
            await take()
            before_zones = await post('/test/read', {'tool': 'home/list_zones', 'arguments': {'includeCells': True}})
            await post('/api/input/take', {'session_id': session, 'viewer_id': 'acceptance-b'}, 400)
            await event(a, 1, 'keyDown', key='ShiftLeft')
            result = await event(a, 2, 'down', button=0)
            await event(a, 3, 'move', x=860, y=300)
            assert result['heldKeys'] == result['heldButtons'] == 1
            held = (await client.get('/test/held')).json()
            assert held['buttons'] and held['keys'], held
            evidence['checks'].append({'name': 'native-held-before-disconnect', 'held': held})
            await a.close()
            await clear('disconnect-releases-button-and-modifier')
            after_zones = await post('/test/read', {'tool': 'home/list_zones', 'arguments': {'includeCells': True}})
            def zone_cells(result):
                return sorted((zone['id'], sorted((cell['x'], cell['z']) for cell in zone.get('cells', [])))
                              for zone in result['zones'])
            assert zone_cells(before_zones) == zone_cells(after_zones), 'Disconnect committed unfinished zone cells'
            evidence['checks'].append({'name': 'disconnect-does-not-commit-zone-cells', 'zones': zone_cells(after_zones)})
            before = b.count; await b.fresh(); assert b.count > before
            evidence['checks'].append({'name': 'other-viewer-survives-owner-disconnect'})
            if gesture_only:
                evidence['passed'] = True
                return
            a = await View(base, session, 'acceptance-a').open(); views.append(a)
            await take()
            await event(a, 1, 'keyDown', key='ControlLeft')
            await asyncio.sleep(9)
            await clear('native-expiry-releases-modifier')
            await take()
            frame = await a.fresh()
            await asyncio.sleep(1)
            await post('/api/input/event', dict(credentials, source=frame['source'], frame=frame['frame'],
                order=1, kind='down', x=800, y=250), 400)
            await clear('stale-frame-rejected')
            await take()
            frame = await a.fresh()
            await post('/api/camera/navigate', dict(credentials, action='right'))
            await post('/api/input/event', dict(credentials, source=frame['source'], frame=frame['frame'],
                order=1, kind='down', x=800, y=250), 400)
            await clear('changed-camera-rejected')
            await take()
            await event(a, 1, 'keyDown', key='ShiftLeft')
            await post('/api/input/release', credentials)
            await clear('explicit-release-cleans-modifier')
            old = dict(credentials)
            await post('/test/load', {})
            await post('/api/input/heartbeat', old, 400)
            await clear('load-invalidates-old-owner')
            assert (await client.get('/api/state')).json()['mode'] == 'manual'
            evidence['passed'] = True
        except Exception as error:
            evidence['error'] = repr(error)
            raise
        finally:
            for view in views:
                await view.close()
            output.write_text(json.dumps(evidence, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', default='http://127.0.0.1:8787')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--gesture-only', action='store_true')
    args = parser.parse_args()
    asyncio.run(run(args.url, args.output, args.gesture_only))
