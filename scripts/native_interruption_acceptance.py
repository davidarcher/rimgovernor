"""Native letter attribution, danger preemption and stale-load dispatch acceptance.

Letter setup uses a separate disposable fixture DLL and the real LetterStack.
Production observation/identity binaries are unchanged by this harness.
"""
from rimgovernor.bridge import gabs_executable
import argparse
import asyncio
import hashlib
import json
import shutil
import traceback
from pathlib import Path

from rimgovernor.bridge import bridge_session
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.headless import isolated_root, prepare
from rimgovernor.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare(root)
    if args.alternate_save:
        shutil.copy2(args.alternate_save, root/'headless-profile/Saves/Interruption-alternate.rws')
    report = {'outcome': 'failed', 'cases': [], 'model_calls': 0,
        'manifest': capture_manifest(Path(__file__).resolve().parents[1], root, config, {'mode': 'no inference'})}
    if args.alternate_save:
        report['alternate_save_sha256'] = hashlib.sha256(args.alternate_save.read_bytes()).hexdigest()
    installed = Path(json.loads((config / 'config.json').read_text())['games']['rimgovernor-trial']['workingDir'])
    fixture = installed / 'Mods/RimGovernor/BridgeTools/InterruptionFixtures/RimGovernor.InterruptionFixtures.BridgeTools.dll'
    report['fixture_sha256'] = hashlib.sha256(fixture.read_bytes()).hexdigest()
    def save():
        (args.output / 'result.json').write_text(json.dumps(report, indent=2))
    def record(name, **evidence):
        report['cases'].append(dict(name=name, **evidence))
        save()
        print('PASS: ' + name, flush=True)
    save()
    store = Store(args.output / 'controller.sqlite')
    try:
        async with bridge_session(gabs_executable(root), config) as bridge:
            rt = BridgeRuntime(store, root, headless=True)
            rt.bridge, rt.game = bridge, BridgeGame(bridge)
            try:
                await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                async def load():
                    await bridge.call('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                        readiness='visual', timeoutMs=90000)
                    await rt.sync_identity()
                    await rt.supervisor.change('Paused')
                    rt.mode = 'automate'
                async def collect_stop():
                    events = []
                    async with asyncio.timeout(12):
                        while True:
                            events.extend(await rt.supervisor.poll())
                            if not rt.supervisor.state['active']:
                                return dict(rt.supervisor.state), events
                            await asyncio.sleep(.05)
                async def reject_old(token, direction, revision):
                    before = await rt.game.query('home/status')
                    actions = rt.counters['actions']
                    pawn = str(before['colonists'][0]['thingId'])
                    try:
                        await rt.native('home/order', {'action': 'draft', 'pawn': pawn, 'dryRun': False},
                            expected_token=token, expected_revision=direction, expected_plan_revision=revision)
                    except ValueError as error:
                        refusal = str(error)
                    else:
                        raise AssertionError('Stale dispatch was accepted')
                    after = await rt.game.query('home/status')
                    assert before['colonists'] == after['colonists'] and rt.counters['actions'] == actions
                    return refusal
                for definition, after, reason in [
                    ('ThreatBig', 'none', 'letter_pause'),
                    ('ThreatBig', 'pause', 'external_pause'),
                    ('ThreatBig', 'speed', 'external_speed_changed'),
                    ('ThreatSmall', 'none', 'notification_batch'),
                ]:
                    await load()
                    token, direction, revision = rt.context_token, rt.chat_revision, rt.current_plan.revision
                    start = await rt.supervisor.change('Normal', max_ticks=1800)
                    assert start['active'], start
                    delivered = (await bridge.call('test/interruption_letter', definition=definition, after=after)).structuredContent
                    stop, events = await collect_stop()
                    assert stop['stopReason'] == reason and stop['pauseVerified'], stop
                    assert stop['lastTick'] < stop['tickDeadline'], stop
                    matching = [e for e in events if e['kind'] == reason]
                    assert len(matching) == 1, events
                    payload = matching[0].get('event') or {}
                    if reason == 'letter_pause':
                        assert payload['letterId'] == delivered['letterId'], (payload, delivered)
                        assert delivered['delivered'] == 'Paused'
                    elif reason == 'notification_batch':
                        assert any(l['id'] == delivered['letterId'] for l in payload['letters']), payload
                        assert delivered['delivered'] != 'Paused'
                    else:
                        assert 'letterId' not in payload, payload
                    rt.clock_events.extend(events)
                    rt.receive_clock_events()
                    assert rt.chat_revision > direction and rt.wake.is_set() and not rt.resume_after_review
                    refused = await reject_old(token, direction, revision)
                    record(definition + '/' + after, start=start, delivered=delivered, stop=stop,
                           events=events, refusal=refused)
                # A non-pausing ordinary announcement remains evidence without stealing the clock.
                await load()
                await rt.supervisor.change('Normal', max_ticks=1800)
                delivered = (await bridge.call('test/interruption_letter', definition='PositiveEvent')).structuredContent
                events = []
                async with asyncio.timeout(8):
                    while not any(e['kind'] == 'notification_new' for e in events):
                        events.extend(await rt.supervisor.poll())
                        await asyncio.sleep(.05)
                assert rt.supervisor.state['active'], rt.supervisor.state
                record('nonstopping_announcement', delivered=delivered, events=events)
                await rt.supervisor.change('Paused')
                for save_name in ['RimGovernor-tribal8-baseline'] + (['Interruption-alternate'] if args.alternate_save else []):
                    await load()
                    token, direction, revision = rt.context_token, rt.chat_revision, rt.current_plan.revision
                    old_colony = rt.identity['colonyId']
                    old_clock = rt.supervisor
                    await old_clock.change('Normal', max_ticks=1800)
                    await bridge.call('rimworld/load_game_ready', saveName=save_name,
                        readiness='visual', timeoutMs=90000)
                    stop, events = await collect_stop()
                    assert stop['stopReason'] == 'session_changed', stop
                    await rt.sync_identity()
                    assert rt.context_token != token and rt.mode == 'manual' and rt.supervisor is not old_clock
                    if save_name == 'Interruption-alternate':
                        assert rt.identity['colonyId'] != old_colony, 'Alternate save must be a different colony'
                    await rt.supervisor.change('Paused')
                    refused = await reject_old(token, direction, revision)
                    record('load_invalidates_dispatch/' + save_name, old_token=token, new_token=rt.context_token,
                           stop=stop, events=events, refusal=refused)
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
    parser.add_argument('--alternate-save', type=Path, help='Unchanged native save from a different colony for cross-colony load acceptance')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
