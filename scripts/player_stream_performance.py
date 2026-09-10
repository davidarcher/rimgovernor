"""Bounded native streaming cost and real local-model handoff measurements."""
import argparse
import asyncio
import json
import time
import uuid
from pathlib import Path
import httpx
from player_stream_acceptance import View


async def run(base, output, model_only=False, simulation_only=False):
    result = {'samples': []}
    view = None
    owner = None
    async with httpx.AsyncClient(base_url=base, headers={'X-RimBot': '1'}, timeout=120) as client:
        async def post(path, body):
            response = await client.post(path, json=body)
            response.raise_for_status()
            return response.json()

        async def sample(label):
            before = (await client.get('/test/cost')).json()
            first = await post('/test/read', {'tool': 'home/status', 'arguments': {'colonists': False, 'threats': False}})
            started = time.monotonic()
            await asyncio.sleep(12)
            last = await post('/test/read', {'tool': 'home/status', 'arguments': {'colonists': False, 'threats': False}})
            elapsed = time.monotonic() - started
            after = (await client.get('/test/cost')).json()
            result['samples'].append({'label': label, 'elapsed': elapsed,
                'ticksPerSecond': (last['time']['ticksGame'] - first['time']['ticksGame']) / elapsed,
                'firstClock': first['time'], 'lastClock': last['time'],
                'before': before, 'after': after})

        state = (await client.get('/api/state')).json()
        session = state['sessionId']
        try:
            if not model_only:
                await post('/api/time', {'session_id': session, 'speed': 'Superfast'})
                await asyncio.sleep(9)
                await sample('video-off')
            view = await View(base, session, 'performance').open()
            if not model_only:
                await sample('video-on')
            await post('/api/time', {'session_id': session, 'speed': 'Paused'})
            if simulation_only:
                assert all(sample['ticksPerSecond'] > 0 for sample in result['samples']), 'Native simulation stopped during measurement'
                result['passed'] = True
                return
            state = (await client.get('/api/state')).json()
            initial_calls = state['counters']['model_calls']
            start_frames = view.count
            await post('/api/chat', {'session_id': session, 'request_id': uuid.uuid4().hex,
                'text': 'Explain the current colony priorities. Do not issue orders or change any settings.'})
            async with asyncio.timeout(120):
                while True:
                    state = (await client.get('/api/state')).json()
                    if state['mood'] == 'thinking' and state['status']['label'] == 'Thinking':
                        break
                    await asyncio.sleep(.1)
            result['busyState'] = state['status']
            taken = await post('/api/input/take', {'session_id': session, 'viewer_id': 'performance'})
            owner = {'session_id': session, 'viewer_id': 'performance', 'lease_id': taken['lease_id']}
            assert taken['direct_input'] and taken['mode'] == 'manual'
            async with asyncio.timeout(150):
                while True:
                    state = (await client.get('/api/state')).json()
                    if state['counters']['model_calls'] > initial_calls and state['mood'] != 'thinking':
                        break
                    await post('/api/input/heartbeat', owner)
                    await asyncio.sleep(2)
            assert state['mode'] == 'manual' and state['game']['paused']
            assert view.count > start_frames + 3
            result['busyFramesDelivered'] = view.count - start_frames
            result['modelCalls'] = state['counters']['model_calls'] - initial_calls
            result['model'] = state['chatModel']
            result['finalState'] = {'mode': state['mode'], 'game': state['game'], 'status': state['status']}
            result['passed'] = True
        except Exception as error:
            result['error'] = repr(error)
            raise
        finally:
            if owner:
                await post('/api/input/release', owner)
            if view:
                await view.close()
            await post('/api/time', {'session_id': session, 'speed': 'Paused'})
            output.write_text(json.dumps(result, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', default='http://127.0.0.1:8787')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--model-only', action='store_true')
    parser.add_argument('--simulation-only', action='store_true')
    args = parser.parse_args()
    asyncio.run(run(args.url, args.output, args.model_only, args.simulation_only))
