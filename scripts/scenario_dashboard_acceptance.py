"""Verify passive dashboard inspection against one private, paused native colony."""
import argparse
import asyncio
import json
import os
from pathlib import Path
import time

import httpx

from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.store import Store
from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready


async def run(seconds):
    root = Path(os.environ['RIMGOVERNOR_BRIDGE_ROOT'])
    store = Store(root/'observer.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = {'passed': False, 'scope': 'Native paused tick and direction invariance under HTTP observation; no pawn-work or rendering claim.'}
    try:
        await ready(rt)
        before = (await rt.game.query('home/status', colonists=False, threats=False))['time']
        identity, revision, mode = rt.context_token, rt.chat_revision, rt.mode
        reads = 0
        async with httpx.AsyncClient(base_url='http://127.0.0.1:8787', timeout=10) as client:
            end = time.monotonic()+seconds
            while time.monotonic() < end:
                response = await client.get('/api/state')
                response.raise_for_status()
                assert response.json()['sessionId'] == identity
                assert response.json()['observationOnly'] is True
                reads += 1
                await asyncio.sleep(1)
            for path in ('/api/control', '/api/chat', '/api/video', '/api/player/input'):
                assert (await client.post(path, json={}, headers={'X-RimGovernor': '1'})).status_code == 403
        after = (await rt.game.query('home/status', colonists=False, threats=False))['time']
        assert before['ticksGame'] == after['ticksGame'] and after['paused'] is True
        assert (rt.context_token, rt.chat_revision, rt.mode) == (identity, revision, mode)
        report.update(passed=True, reads=reads, before=before, after=after, session=identity)
    finally:
        await rt.stop()
        store.close()
        (root/'observer-result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    print(json.dumps(report, indent=2), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seconds', type=int, default=30)
    args = parser.parse_args()
    if not 1 <= args.seconds <= 3600:
        parser.error('--seconds must be 1..3600')
    asyncio.run(run(args.seconds))
