"""Observe real keyboard clock interruption and reject stale controller writes.

An operator sends the requested key to the rendered game after ready.json appears.
No native time-setting API substitutes for the keyboard input under test.
"""
from rimgovernor.bridge import gabs_executable
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path

from rimgovernor.bridge import bridge_session
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.headless import isolated_root, prepare_rendered
from rimgovernor.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare_rendered(root)
    report = {'outcome': 'failed', 'cases': [], 'model_calls': 0,
              'manifest': capture_manifest(Path(__file__).resolve().parents[1], root, config,
                                           {'mode': 'no inference'}, profile=root / 'profile')}
    def save():
        (args.output / 'result.json').write_text(json.dumps(report, indent=2))
    save()
    store = Store(args.output / 'controller.sqlite')
    try:
        async with bridge_session(gabs_executable(root), config) as bridge:
            rt = BridgeRuntime(store, root, headless=False)
            rt.bridge, rt.game = bridge, BridgeGame(bridge)
            try:
                await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                async def load():
                    await bridge.call('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                        readiness='visual', timeoutMs=90000,
                        ignoreModCompatibility=False)
                    await rt.sync_identity()
                    await rt.supervisor.change('Paused')
                await load()
                for key, reason in [('space', 'external_pause'), ('2', 'external_speed_changed')]:
                    await load()
                    rt.mode = 'automate'
                    token, direction, plan = rt.context_token, rt.chat_revision, rt.current_plan.revision
                    start = await rt.supervisor.change('Normal', max_ticks=18000)
                    assert start['active'], start
                    (args.output / 'ready.json').write_text(json.dumps({'key': key, 'reason': reason,
                        'start': start, 'ready_at': time.time()}))
                    print('READY: press ' + key + ' in the rendered RimWorld window', flush=True)
                    events = []
                    async with asyncio.timeout(180):
                        while rt.supervisor.state.get('active'):
                            await asyncio.sleep(.2)
                            events.extend(await rt.supervisor.poll())
                    stop = dict(rt.supervisor.state)
                    assert stop['stopReason'] == reason, stop
                    assert stop['pauseVerified'] and stop['lastTick'] < stop['tickDeadline'], stop
                    rt.clock_events.extend(events)
                    # Deliberately leave the background relay unconsumed: dispatch
                    # must ingest the queued interruption before sending this write.
                    before = await rt.game.query('home/status')
                    pawn = before['colonists'][0]['thingId']
                    actions = rt.counters['actions']
                    try:
                        await rt.native('home/order', {'action': 'draft', 'pawn': str(pawn), 'dryRun': False},
                            expected_token=token, expected_revision=direction, expected_plan_revision=plan)
                    except ValueError as error:
                        refused = str(error)
                        assert 'direction' in refused.lower(), refused
                    else:
                        raise AssertionError('A stale controller write survived player interruption')
                    assert rt.mode == 'manual' and rt.chat_revision > direction
                    try:
                        await rt.supervisor.change('Normal', max_ticks=37)
                    except ValueError as error:
                        assert 'player must enable' in str(error), str(error)
                    else:
                        raise AssertionError('Automatic resume overrode real player input')
                    await asyncio.sleep(.5)
                    after = await rt.game.query('home/status')
                    assert after['time']['paused'] and before['time']['ticksGame'] == after['time']['ticksGame']
                    assert before['colonists'] == after['colonists'] and rt.counters['actions'] == actions
                    report['cases'].append({'key': key, 'start': start, 'stop': stop, 'events': events,
                        'stale_write_refusal': refused, 'before': before, 'after': after})
                    save()
                    print('PASS: ' + reason, flush=True)
                    rt.supervisor.allow_resume()
                    assert (await rt.supervisor.change('Normal', max_ticks=37))['active']
                    async with asyncio.timeout(10):
                        while rt.supervisor.state.get('active'):
                            await asyncio.sleep(.1)
                            await rt.supervisor.poll()
                    assert rt.supervisor.state['stopReason'] == 'tick_budget'
                report['outcome'] = 'passed'
            finally:
                await rt.router.close()
                report['cleanup'] = (await bridge.core('games_stop', gameId=bridge.game_id)).model_dump(mode='json')
    except Exception as error:
        report.update(error=repr(error), traceback=traceback.format_exc())
    finally:
        store.close()
        save()
    print(report['outcome'], flush=True)
    return report['outcome'] == 'passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
