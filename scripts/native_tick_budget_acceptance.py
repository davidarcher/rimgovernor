"""Verify exact native execution boundaries and external clock ownership in a private game."""
from rimgovernor.bridge import gabs_executable
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import subprocess
import time

from rimgovernor.bridge import bridge_session
from rimgovernor.clock_control import PlayClock
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.headless import isolated_root, prepare, prepare_rendered


async def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    root = isolated_root(args.source_root, args.output/'bridge')
    config = prepare_rendered(root) if args.rendered else prepare(root)
    game_config = json.loads((config/'config.json').read_text())['games']['rimgovernor-trial']
    dll = Path(game_config['workingDir'])/'Mods/RimGovernor/BridgeTools/RimGovernor/RimGovernor.Bridge.dll'
    report = {'outcome': 'failed', 'cases': [], 'started': time.time(),
              'revision': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
              'native_sha256': hashlib.sha256(dll.read_bytes()).hexdigest(),
              'baseline_sha256': hashlib.sha256((root/'profile/Saves/RimGovernor-tribal8-baseline.rws').read_bytes()).hexdigest(),
              'rendered': args.rendered}
    report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config,
        {'mode': 'no inference'}, profile=root/('profile' if args.rendered else 'headless-profile'))
    def save():
        (args.output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    save()
    try:
        async with bridge_session(gabs_executable(root), config) as bridge:
            async def state():
                return (await bridge.call('home/supervised_play', op='status')).structuredContent
            async def stopped():
                async with asyncio.timeout(20):
                    while True:
                        result = await state()
                        if not result['active']:
                            return result
                        await asyncio.sleep(.1)
            try:
                await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                                  readiness='visual', timeoutMs=90000,
                                  ignoreModCompatibility=False)
                clock = PlayClock(bridge)
                await clock.change('Paused')
                for speed in ('Normal', 'Fast', 'Superfast'):
                    for budget in (1, 37, 600):
                        await bridge.call('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                                          readiness='visual', timeoutMs=90000,
                                          ignoreModCompatibility=False)
                        clock = PlayClock(bridge)
                        await clock.change('Paused')
                        start = await clock.change(speed, max_ticks=budget)
                        assert start['active'] or start['stopReason'] == 'tick_budget', start
                        if start['active']:
                            await clock.poll()  # Renewal must not move the boundary.
                        end = await stopped()
                        assert end['stopReason'] == 'tick_budget' and end['pauseVerified'], end
                        assert end['tickDeadline'] == start['startTick'] + budget, (start, end)
                        observed = (await bridge.call('home/status', colonists=False, threats=False)).structuredContent
                        assert observed['time']['ticksGame'] == end['tickDeadline'], (end, observed)
                        assert observed['time']['paused'], observed
                        await asyncio.sleep(.25)
                        stable = (await bridge.call('home/status', colonists=False, threats=False)).structuredContent
                        assert stable['time']['ticksGame'] == end['tickDeadline'], stable
                        events = await clock.poll()
                        assert len([e for e in events if e['kind'] == 'tick_budget']) <= 1, events
                        assert clock.hold is None
                        report['cases'].append({'speed': speed, 'budget': budget, 'start': start,
                                                'stop': end, 'readback': observed, 'stable': stable})
                        save()
                        print(f'PASS {speed}: exactly {budget} ticks', flush=True)
                for external, reason in (('Paused', 'external_pause'), ('Fast', 'external_speed_changed')):
                    await clock.change('Normal', max_ticks=3000)
                    await bridge.call('rimworld/set_time_speed', speed=external, ultraSpeedBoost=False)
                    end = await stopped()
                    assert end['stopReason'] == reason, end
                    await clock.poll()
                    try:
                        await clock.change('Normal', max_ticks=100)
                    except ValueError as error:
                        assert 'player must enable' in str(error), error
                    else:
                        raise AssertionError('External hold was overridden')
                    clock.allow_resume()
                    report['cases'].append({'external_speed': external, 'stop': end})
                    save()
                    print(f'PASS {reason}: explicit resume required', flush=True)
                lease = (await bridge.call('home/supervised_play', op='start', owner=clock.owner,
                    speed='Normal', leaseMs=1000, maxTicks=3000, hostileWithin=40,
                    injuryStopCooldownMs=0)).structuredContent
                assert lease['active'], lease
                expired = await stopped()
                assert expired['stopReason'] == 'lease_expired' and expired['pauseVerified'], expired
                assert expired['lastTick'] < expired['tickDeadline'], expired
                report['cases'].append({'lease_expiry': expired})
                save()
                print('PASS lease expiry before tick limit', flush=True)
                # Loading a native save retires the old watcher without claiming
                # the new session's clock or carrying its old tick deadline over.
                await bridge.call('home/supervised_play', op='start', owner=clock.owner,
                                  speed='Normal', leaseMs=15000, maxTicks=3000,
                                  hostileWithin=40, injuryStopCooldownMs=0)
                await bridge.call('rimworld/load_game_ready', saveName='RimGovernor-tribal8-baseline',
                                  readiness='visual', timeoutMs=90000,
                                  ignoreModCompatibility=False)
                changed = await stopped()
                assert changed['stopReason'] == 'session_changed', changed
                report['cases'].append({'load_change': changed})
                report['outcome'] = 'passed'
                save()
            finally:
                report['cleanup'] = (await bridge.core('games_stop', gameId=bridge.game_id)).model_dump(mode='json')
    except Exception as error:
        report['outcome'] = 'failed'
        report['error'] = repr(error)
    finally:
        save()
    print(report['outcome'], flush=True)
    return report['outcome'] == 'passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--rendered', action='store_true')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
