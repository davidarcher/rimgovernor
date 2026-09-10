"""Measure bounded native clocks in one private container_worker; retain every trial."""
import argparse
import asyncio
from contextlib import AsyncExitStack
import json
import hashlib
import os
import resource
import re
import time
import xml.etree.ElementTree as ET
from pathlib import Path

from rimbot.bridge import BridgeError, bridge_session, gabs_executable, runtime_file_read
from rimbot.clock_control import PlayClock
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_game import BridgeGame
from rimbot.native_scenario import advance_game
from rimbot.store import Store
from rimbot.flight_recorder import recorder
from rimbot.supply_batches import supply_rectangles
from deterministic_foothold import NoInference


async def run(args):
    root = Path(os.environ['RIMBOT_BRIDGE_ROOT'])
    prefs_path = root/'profile/Config/Prefs.xml'
    prefs = ET.parse(prefs_path)
    pause = prefs.getroot().find('pauseOnLoad')
    if pause is None:
        pause = ET.SubElement(prefs.getroot(), 'pauseOnLoad')
    pause.text = 'True'
    prefs.write(prefs_path, encoding='utf8', xml_declaration=True)
    # Native saves can contain .NET field tags rejected by Python's XML parser.
    saved = (root/'profile/Saves/RimBot-tribal8-baseline.rws').read_text(encoding='utf-8-sig')
    tick_field = re.search(r'<tickManager>\s*<ticksGame>(\d+)</ticksGame>', saved)
    if tick_field is None:
        raise ValueError('Native checkpoint has no tickManager/ticksGame metadata')
    baseline_tick = int(tick_field[1])
    config = root/'config'
    # prepare() owns the configuration path and preserves the worker's private game.
    if os.environ.get('RIMBOT_HEADLESS') == '1':
        from rimbot.headless import prepare
        config = prepare(root)
    report = dict(passed=False, samples=[], calls=[], stops=[], mode=args.mode,
                  start_type='saved_checkpoint' if args.saved_checkpoint else 'fresh_baseline',
                  inputs=json.loads((root/'inputs.json').read_text()),
                  scope='Native clock boundaries, supply scope, ordinary movement and scheduled safety-onset reactions; inference is measured separately')
    report['source_hashes'] = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in (
        Path(__file__), Path('controller/rimbot/clock_control.py'), Path('controller/rimbot/supply_batches.py'))}
    report['prepared_prefs_sha256'] = hashlib.sha256(prefs_path.read_bytes()).hexdigest()
    report['baseline_save_tick'] = baseline_tick
    path = root/'throughput-result.json'

    def save():
        path.write_text(json.dumps(report, indent=2), encoding='utf8')

    started = time.perf_counter()
    try:
        async with AsyncExitStack() as stack:
            bridge = await stack.enter_async_context(bridge_session(gabs_executable(root, config), config))
            fixture_store = Store(root/'fixture.sqlite')
            stack.callback(fixture_store.close)
            fixture_runtime = BridgeRuntime(fixture_store, root, headless=args.mode == 'headless',
                                           model_factory=lambda _: NoInference())
            fixture_runtime.bridge = bridge
            fixture_runtime.game = BridgeGame(bridge)
            rendered_at = time.perf_counter()
            async def stop_game():
                report['game_stop'] = (await bridge.core('games_stop', gameId=bridge.game_id)).model_dump(mode='json')
            stack.push_async_callback(stop_game)
            async def call(name, **arguments):
                begin = time.perf_counter()
                phase = ('identity' if name == 'home/colony_identity' else
                         'readback' if name == 'home/order' and arguments.get('action') == 'resolve' else
                         ('preview' if arguments.get('dryRun') else 'dispatch') if name in (
                             'home/order', 'rimworld/apply_architect_designator') else 'observation')
                try:
                    reply = await runtime_file_read(bridge.call, name, **arguments) if (
                        name == 'home/status' or (name == 'home/supervised_play' and arguments.get('op') in ('status', 'events'))
                        or (name == 'test/throughput_event' and arguments.get('op') == 'status')
                    ) else await bridge.call(name, **arguments)
                    value = reply.structuredContent
                    if name == 'home/status':
                        fixture_runtime.clock = value['time']
                    return value
                finally:
                    report['calls'].append(dict(tool=name, op=arguments.get('op'), phase=phase, seconds=time.perf_counter()-begin))

            def sample_memory():
                for entry in Path('/proc').iterdir():
                    if not entry.name.isdigit():
                        continue
                    try:
                        if 'RimWorld' not in (entry/'comm').read_text():
                            continue
                        status = dict(line.split(':', 1) for line in (entry/'status').read_text().splitlines() if ':' in line)
                        rss = int(status.get('VmRSS', '0 kB').split()[0])
                        report['game_peak_rss_kib'] = max(report.get('game_peak_rss_kib', 0), rss)
                    except (FileNotFoundError, ProcessLookupError):
                        continue

            async def load():
                nonlocal rendered_at
                await call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline',
                           readiness='visual', timeoutMs=90000, ignoreModCompatibility=True)
                await call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                if args.mode != 'headless':
                    render = await call('test/render_suspend', seconds=30) if args.mode == 'suspended' else await call('home/render_demand', seconds=30)
                    report.setdefault('render_states', []).append(render)
                    assert render['supported'] and render['suspended'] == (args.mode == 'suspended'), render
                    rendered_at = time.perf_counter()
                baseline = await call('home/status', colonists=False, threats=False)
                tick = baseline['time']['ticksGame']
                assert baseline['time']['paused'] and tick in (baseline_tick, baseline_tick+1), baseline
                expected = report.setdefault('loaded_baseline_tick', tick)
                assert tick == expected, ('Reload changed the comparison starting tick', expected, tick)
                await fixture_runtime.sync_identity()
                fixture_runtime.connected = True
                fixture_runtime.phase = 'Throughput clock acceptance'
                return baseline

            async def maintain_render():
                nonlocal rendered_at
                if args.mode != 'headless' and time.perf_counter()-rendered_at >= 10:
                    await call('test/render_suspend', seconds=30) if args.mode == 'suspended' else await call('home/render_demand', seconds=30)
                    rendered_at = time.perf_counter()

            async def advance_fixture(ticks, accelerated, evidence):
                await maintain_render()
                await fixture_runtime.sync_identity()
                fixture_runtime.supervisor.test_acceleration = accelerated
                return await advance_game(fixture_runtime, ticks, evidence)

            # Clock acceptance deliberately observes each stop without resuming;
            # ordinary pawn setup uses the shared scenario waiter above.
            async def stopped(clock):
                fixture_runtime.supervisor = clock
                captured_at = 0
                async with asyncio.timeout(120):
                    while True:
                        await maintain_render()
                        state = await call('home/supervised_play', op='status')
                        sample_memory()
                        clock.absorb(state)
                        fixture_runtime.clock = dict(ticksGame=state['lastTick'], paused=state['paused'])
                        if not state['active']:
                            return state
                        if args.mode == 'capture' and time.perf_counter()-captured_at >= 1:
                            frames = report.setdefault('captures', [])
                            frame = await call('rimworld/take_screenshot', fileName=f'b16-throughput-{len(frames)}',
                                               includeTargets=False, suppressMessage=True)
                            candidate = Path(frame['path']).resolve()
                            assert candidate.is_relative_to(root) and candidate.is_file(), frame
                            data = candidate.read_bytes()
                            assert data.startswith(b'\x89PNG\r\n\x1a\n'), frame
                            frames.append(dict(path=str(candidate), sha256=hashlib.sha256(data).hexdigest(), bytes=len(data)))
                            fixture_runtime.camera_bytes = data
                            fixture_runtime.camera_version += 1
                            fixture_runtime.camera_captured_at = time.time()
                            captured_at = time.perf_counter()
                        if state['leaseRemainingMs'] < 10000:
                            await clock.poll()
                        await asyncio.sleep(.1)

            async def approach_wild_animal():
                people = await call('home/list_pawns', colonistsOnly=True)
                pawn = next(p for p in people['pawns'] if not p.get('downed') and not p.get('dead'))
                animals = await call('home/list_pawns', animalsOnly=True, includeColonists=False, withinOfColonists=250)
                candidates = sorted((p for p in animals['pawns'] if p.get('wild') and not p.get('downed') and not p.get('dead')),
                    key=lambda p: (p['position']['x']-pawn['position']['x'])**2+(p['position']['z']-pawn['position']['z'])**2)
                move = None
                for candidate in candidates[:8]:
                    for dx, dz in ((8, 0), (-8, 0), (0, 8), (0, -8)):
                        proposal = dict(action='goto', pawn=pawn['thingId'], x=candidate['position']['x']+dx,
                                        z=candidate['position']['z']+dz, watch=False)
                        try:
                            preview = await call('home/order', **proposal, dryRun=True)
                        except BridgeError:
                            continue
                        if preview.get('success'):
                            move = proposal
                            break
                    if move:
                        break
                assert move, 'No native-reachable animal approach in this fixture'
                receipt = await call('home/order', **move, dryRun=False)
                setup = dict(receipt=receipt, windows=[])
                report.setdefault('hazard_setup', []).append(setup)
                for _ in range(12):
                    end = await advance_fixture(600, True, setup)
                    current = await call('home/order', action='resolve', pawn=pawn['thingId'], dryRun=True)
                    setup['windows'].append(dict(stop=end, pawn=current))
                    save()
                    assert end['stopReason'] == 'tick_budget', end
                    if current['pawn']['position'] == receipt['job']['targetA']['position']:
                        return
                raise AssertionError('Native animal approach did not complete')

            await bridge.core('games_start', gameId=bridge.game_id)
            await bridge.connect()
            report['startup_seconds'] = time.perf_counter()-started
            discovery_started = time.perf_counter()
            report['schemas'] = {name: (await bridge.detail(name)).model_dump(mode='json') for name in (
                'home/supervised_play', 'rimworld/apply_architect_designator', 'home/order')}
            report['discovery_seconds'] = time.perf_counter()-discovery_started
            for repeat in range(args.repeats):
                speeds = [('Superfast', False), ('Superfast', True)]
                for speed, accelerated in (list(reversed(speeds)) if repeat % 2 else speeds):
                    for ticks in args.ticks:
                        case_started = time.perf_counter()
                        baseline = await load()
                        clock = PlayClock(bridge, test_acceleration=accelerated)
                        begin = time.perf_counter()
                        initial = await clock.change(speed, max_ticks=ticks)
                        end = await stopped(clock)
                        elapsed = time.perf_counter()-begin
                        observed = await call('home/status', colonists=False, threats=False)
                        sample = dict(repeat=repeat, accelerated=accelerated, ticks=ticks,
                                      baseline=baseline, start=initial, stop=end, observed=observed,
                                      seconds=elapsed, case_seconds=time.perf_counter()-case_started,
                                      wall_tps=(observed['time']['ticksGame']-initial['startTick'])/elapsed)
                        report['samples'].append(sample)
                        save()
                        sample['completed'] = end['stopReason'] == 'tick_budget'
                        assert end['pauseVerified'] and observed['time']['paused'], sample
                        assert end['stopReason'] in {'tick_budget', 'letter_pause', 'notification_batch',
                            'hostile', 'colonist_downed', 'predator_hunt', 'colonist_health', 'colonist_injury'}, sample
                        if accelerated:
                            assert end['maxProbeTickGap'] <= end['probeTickLimit'] == 30, sample
                        assert not end['boostOwned'], sample
                        check = await call('rimworld/set_time_speed', speed='Paused')
                        assert check['currentUltraSpeedBoost'] is False, check
                        if not sample['completed']:
                            sample['interruption'] = await call('home/status', colonists=True, threats=True)
                            save()
                            print(f"INTERRUPTED accelerated={accelerated} ticks={ticks}: {end['stopReason']}", flush=True)
                            continue
                        assert observed['time']['ticksGame'] == initial['startTick']+ticks, sample
                        assert not end['boostOwned'] and observed['time']['paused'], sample
                        print(f"PASS accelerated={accelerated} ticks={ticks} wall_tps={sample['wall_tps']:.1f}", flush=True)
            for batched in (() if args.saved_checkpoint else (False, True)):
                await load()
                facts = await call('home/colony_facts')
                targets = facts['forbiddenSupplies'][:8]
                assert targets, 'No forbidden starting supplies in the fixture'
                catalog = await call('rimworld/list_architect_designators', categoryId='Orders')
                designator = next(row['id'] for row in catalog['designators'] if row.get('className') == 'RimWorld.Designator_Unforbid')
                rectangles = supply_rectangles(targets) if batched else [dict(p, width=1, height=1) for p in targets]
                begin = time.perf_counter()
                receipts = []
                identity = await call('home/colony_identity')
                for rectangle in rectangles:
                    arguments = dict(designatorId=designator, **rectangle, keepSelected=False)
                    preview = await call('rimworld/apply_architect_designator', **arguments, dryRun=True)
                    assert preview['acceptedCellCount'] > 0, preview
                    current_identity = await call('home/colony_identity')
                    assert all(current_identity[k] == identity[k] for k in ('colonyId', 'mapId', 'loadToken'))
                    receipts.append(await call('rimworld/apply_architect_designator', **arguments, dryRun=False))
                after = await call('home/colony_facts')
                remaining = {(p['x'], p['z']) for p in after['forbiddenSupplies']}
                selected = {(p['x'], p['z']) for p in targets}
                unselected = {(p['x'], p['z']) for p in facts['forbiddenSupplies']} - selected
                row = dict(batched=batched, targets=targets, rectangles=rectangles, receipts=receipts,
                           seconds=time.perf_counter()-begin, remaining=after['forbiddenSupplies'])
                report.setdefault('supply_batches', []).append(row)
                save()
                assert not (selected & remaining) and unselected <= remaining, row
                print(f"PASS supplies batched={batched} calls={len(receipts)} seconds={row['seconds']:.2f}", flush=True)
            for accelerated in (False, True):
                baseline = await load()
                people = await call('home/list_pawns', colonistsOnly=True)
                pawn = next(p for p in people['pawns'] if not p.get('downed') and not p.get('dead'))
                position = pawn['position']
                arguments = dict(action='goto', pawn=pawn['thingId'], x=position['x']+5, z=position['z'], watch=False)
                begin = time.perf_counter()
                preview = await call('home/order', **arguments, dryRun=True)
                assert preview['success'], preview
                receipt = await call('home/order', **arguments, dryRun=False)
                destination = receipt['job']['targetA']['position']
                setup = {}
                end = await advance_fixture(600, accelerated, setup)
                actual = await call('home/order', action='resolve', pawn=pawn['thingId'], dryRun=True)
                row = dict(accelerated=accelerated, receipt=receipt, stop=end, observed=actual, simulation=setup,
                           seconds=time.perf_counter()-begin, observation_age_ticks=end['lastTick']-baseline['time']['ticksGame'])
                report.setdefault('pawn_outcomes', []).append(row)
                save()
                assert actual['pawn']['position'] == destination, row
                assert end['pauseVerified'] and end['stopReason'] == 'tick_budget', row
                row['verified'] = True
                print(f"PASS ordinary movement accelerated={accelerated} seconds={row['seconds']:.2f}", flush=True)
            for speed, reason in (('Paused', 'external_pause'), ('Fast', 'external_speed_changed')):
                await load()
                clock = PlayClock(bridge, test_acceleration=True)
                await clock.change('Superfast', max_ticks=1800000)
                begin = time.perf_counter()
                receipt = await call('rimworld/set_time_speed', speed=speed)
                end = await stopped(clock)
                observed = await call('home/status', colonists=False, threats=False)
                row = dict(request=speed, receipt=receipt, stop=end, observed=observed,
                           request_to_verified_pause_seconds=time.perf_counter()-begin)
                report['stops'].append(row)
                save()
                assert end['stopReason'] == reason and observed['time']['paused'] and not end['boostOwned'], row
            for previous in (False, True):
                await load()
                await call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=previous)
                clock = PlayClock(bridge, test_acceleration=True)
                await clock.change('Superfast', max_ticks=37)
                end = await stopped(clock)
                restored = await call('rimworld/set_time_speed', speed='Paused')
                report.setdefault('boost_restoration', []).append(dict(previous=previous, stop=end, restored=restored))
                save()
                assert restored['currentUltraSpeedBoost'] is previous and not end['boostOwned'], restored
            await load()
            roster = await call('home/list_pawns', colonistsOnly=True)
            report['lease_fixture_drafts'] = [await call('home/order', action='draft', pawn=p['thingId'], watch=False)
                                             for p in roster['pawns'] if not p.get('downed') and not p.get('dead')]
            for attempt in range(2):
                expired = await call('home/supervised_play', op='start', owner='b16-expiry', leaseMs=1000,
                                     speed='Ultrafast', maxTicks=1800000, testAcceleration=True, injuryStopCooldownMs=0)
                async with asyncio.timeout(10):
                    while expired['active']:
                        await asyncio.sleep(.1)
                        expired = await call('home/supervised_play', op='status')
                if attempt == 0 and expired['stopReason'] == 'letter_pause' and 'Ancient danger' in expired['stopDetail']:
                    threats = await call('home/status', colonists=True, threats=True)
                    assert not threats['threats']['hostiles'] and not threats['threats']['huntingPredators'], threats
                    report['lease_setup_interruption'] = dict(stop=expired, threats=threats)
                    save()
                    continue
                break
            report['lease_expiry'] = expired
            save()
            assert expired['stopReason'] == 'lease_expired' and expired['pauseVerified'] and not expired['boostOwned'], expired
            if args.fixture:
                for accelerated in (False, True):
                    for op, reason in (('pause', 'external_pause'), ('speed', 'external_speed_changed'), ('hostile', 'hostile')):
                        for offset in (7, 31):
                            await load()
                            if op == 'hostile':
                                await approach_wild_animal()
                            scheduled = await call('test/throughput_event', op=op, afterTicks=offset)
                            clock = PlayClock(bridge, test_acceleration=accelerated)
                            await clock.change('Superfast', max_ticks=600)
                            end = await stopped(clock)
                            onset = await call('test/throughput_event', op='status')
                            observed = await call('home/status', colonists=False, threats=False)
                            row = dict(accelerated=accelerated, scheduled=scheduled, onset=onset, stop=end,
                                       observed=observed, reaction_ticks=observed['time']['ticksGame']-onset['onsetTick'],
                                       reaction_ms=end['stopAtMs']-onset['onsetAtMs'])
                            report.setdefault('safety', []).append(row)
                            save()
                            assert onset['applied'] and end['stopReason'] == reason, row
                            assert observed['time']['paused'] and not end['boostOwned'], row
                            if accelerated:
                                assert 0 <= row['reaction_ticks'] <= 30, row
                            assert 0 <= row['reaction_ms'] <= 1000, row
                            print(f"PASS safety {op} accelerated={accelerated} reaction_ticks={row['reaction_ticks']}", flush=True)
            report['passed'] = all(any(s['completed'] for s in report['samples'] if s['accelerated'] == accelerated)
                                   for accelerated in (False, True))
    except BaseException as error:
        report['passed'] = False
        report['error'] = repr(error)
        raise
    finally:
        report['total_seconds'] = time.perf_counter()-started
        report['flight_recorder'] = recorder().stats() if recorder() else None
        report['child_peak_rss_kib'] = resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss
        report['throughput'] = []
        for accelerated in (False, True):
            samples = [s for s in report['samples'] if s['accelerated'] == accelerated]
            elapsed = sum(s.get('case_seconds', s['seconds']) for s in samples)
            completed = sum(s.get('completed', False) for s in samples)
            report['throughput'].append(dict(accelerated=accelerated, attempted=len(samples), completed=completed,
                completed_cases_per_minute=60*completed/elapsed if elapsed else None,
                case_seconds_including_loads=elapsed))
        outcomes = report.get('pawn_outcomes', [])
        report['verified_pawn_outcomes_per_total_minute'] = 60*sum(
            row.get('verified', False) for row in outcomes)/report['total_seconds']
        save()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mode', choices=['headless', 'rendered', 'suspended', 'capture'], default='headless')
    parser.add_argument('--repeats', type=int, default=2)
    parser.add_argument('--ticks', type=int, nargs='+', default=[37, 600, 6000])
    parser.add_argument('--fixture', action='store_true')
    parser.add_argument('--saved-checkpoint', action='store_true')
    asyncio.run(run(parser.parse_args()))
