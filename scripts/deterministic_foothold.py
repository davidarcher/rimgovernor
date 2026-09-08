"""Visible deterministic baseline through normal commitment/Hands execution.

No inference, save edits, spawned items, or instant construction. Sleeping spots
are ordinary zero-cost player placements. This accepts the narrow foothold gate,
not roofed shelter, actual sleeping/hauling, or sustained survival.
"""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path
from types import SimpleNamespace

from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.campaign_metrics import CampaignEvidence
from rimbot.clock_control import PlayClock
from rimbot.colony_plan import CommitSteps, Decision, PlanStep
from rimbot.config import ModelRole
from rimbot.headless import isolated_root, prepare_rendered
from rimbot.store import Store
from headless_iterations import sample_metrics


class NoInference:
    async def complete(self, *args, **kwargs):
        raise AssertionError('Deterministic baseline must not call a model')

    async def close(self):
        pass


async def probe_deliberation(rt, report):
    """A slow scripted review must not spend simulation time or resume idle work."""
    async def slow_review():
        before = await rt.game.query('home/status', colonists=False, threats=False)
        await asyncio.sleep(5)
        after = await rt.game.query('home/status', colonists=False, threats=False)
        report['deliberation_probe'] = {'before': before['time'], 'after': after['time']}
        assert before['time']['paused'] and after['time']['paused']
        assert before['time']['ticksGame'] == after['time']['ticksGame']
        await rt.commit_strategy(Decision(expected_revision=rt.current_plan.revision,
            disposition='continue', assessment='Foothold already complete',
            rationale='No additional orders needed', reply='Keep the completed foothold'),
            actor=ModelRole.STRATEGIST, expected_token=rt.context_token,
            expected_revision=rt.chat_revision)
    rt.planner = SimpleNamespace(play_bridge=slow_review)
    rt.wake.clear()
    await rt.supervisor.change('Normal')
    await rt.review()
    assert 'deliberation_probe' in report and rt.mode == 'automate'
    await rt.advance_execution()
    await asyncio.sleep(2)
    idle = await rt.game.query('home/status', colonists=False, threats=False)
    report['deliberation_probe']['after_idle_execution'] = idle['time']
    assert idle['time']['paused']
    assert idle['time']['ticksGame'] == report['deliberation_probe']['after']['ticksGame']


def candidates(anchor, radius=12):
    """Nearest first, stable ties, with no map-specific coordinates."""
    x, z = map(round, anchor)
    return sorted(((a, b) for a in range(max(0, x-radius), x+radius+1)
                   for b in range(max(0, z-radius), z+radius+1)),
                  key=lambda p: ((p[0]-anchor[0])**2+(p[1]-anchor[1])**2, p))


async def commit(rt, action, name):
    step = PlanStep(id=name, title=name.replace('-', ' '), action=action,
                    completion_criteria='Fresh native readback verifies the committed effect')
    proposal = CommitSteps(expected_revision=rt.current_plan.revision,
                           reason='Deterministic foothold baseline using native evidence', steps=[step])
    await rt.commit_strategy(proposal.decision(rt.current_plan), actor=ModelRole.STRATEGIST,
                             expected_token=rt.context_token, expected_revision=rt.chat_revision)
    await rt.hands.advance(rt)
    progress = rt.current_plan.progress[name]
    if progress.state != 'complete':
        raise AssertionError({'step': name, 'progress': progress.model_dump()})


async def establish(rt, anchor, report):
    game = rt.game
    # Select the normal Allow designator from the installed native catalog.
    catalog = await game.invoke('rimworld/list_architect_designators', {'categoryId': 'Orders'})
    report['orders_catalog'] = catalog
    allow = next(d for d in catalog['designators'] if d['className'] == 'RimWorld.Designator_Unforbid')
    supplies = await game.query('home/list_things', ownership='ours', includeHeld=False,
                               x=round(anchor[0]), z=round(anchor[1]), radius=18, maxPositionsPerDef=100)
    report['supplies_before'] = supplies
    targets = sorted({(p['x'], p['z']) for row in supplies['things'] if row.get('forbidden', 0) > 0
                      for p in row.get('positions', [])})
    if not targets:
        raise AssertionError('No observed nearby starting supplies')
    for index, (x, z) in enumerate(targets):
        await commit(rt, {'kind': 'native_operation', 'tool': 'rimworld/apply_architect_designator',
                         'arguments': {'designatorId': allow['id'], 'x': x, 'z': z,
                                       'dryRun': False, 'keepSelected': False}}, f'allow-{index}')
    print(json.dumps({'phase': 'supplies_allowed', 'cells': len(targets)}), flush=True)

    report['zone_previews'] = []
    for x, z in candidates(anchor):
        cells = {(a, b) for a in range(x, x+3) for b in range(z, z+3)}
        preview = await rt.inspect_native('home/zone_cells', {
            'op': 'create', 'zoneType': 'stockpile', 'label': 'Baseline supplies',
            'cells': ';'.join(f'{a},{b}' for a, b in sorted(cells)), 'dryRun': True})
        report['zone_previews'].append({'x': x, 'z': z, 'result': preview})
        if preview.get('cellsAccepted') == 9:
            await commit(rt, {'kind': 'create_zone', 'zone_type': 'stockpile', 'label': 'Baseline supplies',
                              'patches': [{'x': x, 'z': z, 'width': 3, 'height': 3}]}, 'stockpile')
            reserved = cells
            break
    else:
        raise AssertionError('No fully legal nearby nine-cell stockpile')
    print(json.dumps({'phase': 'stockpile_verified', 'cells': 9}), flush=True)

    # Every sleeping footprint is selected from a fresh native preview; successful
    # placements are visible to later previews and reserved locally as well.
    report['sleep_previews'] = []
    needed = len(rt.batch.summary.pawns)
    count = 0
    for x, z in candidates(anchor):
        if (x, z) in reserved:
            continue
        preview = await rt.inspect_native('home/place_building', {
            'defName': 'SleepingSpot', 'x': x, 'z': z, 'rotation': 'north', 'dryRun': True})
        report['sleep_previews'].append({'x': x, 'z': z, 'result': preview})
        occupied = {(c['x'], c['z']) for rotation in preview.get('rotations', [])
                    for c in rotation.get('occupiedCells', [])}
        if not preview.get('canPlace') or not occupied or occupied & reserved:
            continue
        await commit(rt, {'kind': 'place_buildings', 'placements': [
            {'def_name': 'SleepingSpot', 'x': x, 'z': z}]}, f'sleep-{count}')
        reserved |= occupied
        count += 1
        print(json.dumps({'phase': 'sleeping_place_verified', 'count': count, 'required': needed}), flush=True)
        if count == needed:
            break
    if count != needed:
        raise AssertionError(f'Only {count}/{needed} legal sleeping places found')


async def run(args):
    if args.output.exists():
        raise ValueError('Choose a fresh output directory; never overwrite evidence')
    root = isolated_root(args.source_root, args.output/'bridge')
    config = prepare_rendered(root)
    store = Store(args.output/'state.sqlite')
    rt = BridgeRuntime(store, root, model_factory=lambda _: NoInference())
    evidence = CampaignEvidence()
    report = {'outcome': 'error', 'strategy': 'deterministic', 'rendered': True,
              'save_edits': [], 'model_calls': 0, 'clock_events': []}
    started = time.monotonic()
    try:
        source = Path(__file__).resolve().parents[1]
        manifest = capture_manifest(source, root, config, rt.router.routing.model_dump(mode='json'),
                                    profile=root/'profile')
        (args.output/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf8')
        report['manifest_fingerprint'] = manifest['fingerprint']
        async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe', config) as bridge:
            rt.bridge, rt.game = bridge, BridgeGame(bridge)
            try:
                await bridge.core('games_start', gameId=bridge.game_id)
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline',
                                  readiness='visual', timeoutMs=90000, ignoreModCompatibility=True)
                await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                await rt.sync_identity()
                rt.batch = await observe(rt.game)
                rt.strategic_state.update(rt.batch)
                rt.mode = 'automate'
                anchor = (sum(p.position.x for p in rt.batch.summary.pawns)/len(rt.batch.summary.pawns),
                          sum(p.position.z for p in rt.batch.summary.pawns)/len(rt.batch.summary.pawns))
                report['anchor'] = anchor
                report['before'] = await sample_metrics(rt, evidence, anchor, 0)
                began = time.monotonic()
                await establish(rt, anchor, report)
                report['foothold'] = await sample_metrics(rt, evidence, anchor, time.monotonic()-began,
                                                        capture=report.setdefault('native_after', {}))
                if not report['foothold']['usable']:
                    raise AssertionError({'foothold_not_verified': report['foothold']})
                report['foothold_seconds'] = round(time.monotonic()-began, 2)
                print(json.dumps({'phase': 'foothold_verified', 'seconds': report['foothold_seconds']}), flush=True)
                # Leave a bounded, supervised interval to observe ordinary play.
                # No model, repair orders, or forced pawn jobs run in this period.
                rt.supervisor = PlayClock(bridge)
                await rt.supervisor.change('Normal')
                until = time.monotonic()+args.observe_seconds
                while time.monotonic() < until:
                    await asyncio.sleep(2)
                    events = await rt.supervisor.poll()
                    report['clock_events'].extend(events)
                    if rt.supervisor.hold or not rt.supervisor.state.get('active'):
                        report['observation_stopped'] = rt.supervisor.hold or rt.supervisor.state.get('stopReason')
                        break
                await rt.supervisor.change('Paused')
                report['foothold'] = await sample_metrics(rt, evidence, anchor, time.monotonic()-began,
                                                        capture=report['native_after'])
                if not report['foothold']['usable']:
                    raise AssertionError('Foothold no longer verifies after observation')
                if args.clock_probe:
                    await probe_deliberation(rt, report)
                report['outcome'] = 'usable_foothold'
            finally:
                try:
                    await rt.halt()
                finally:
                    await bridge.core('games_stop', gameId=bridge.game_id)
    except Exception as error:
        report.update(outcome='error', error=str(error), traceback=traceback.format_exc())
    finally:
        report['elapsed_seconds'] = round(time.monotonic()-started, 2)
        report['plan'] = rt.current_plan.model_dump()
        report['metrics'] = evidence.report()
        report['events'] = store.history(rt.colony, limit=10000, include_diagnostics=True)
        report['counters'] = rt.counters
        await rt.router.close()
        store.close()
        (args.output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
        print(json.dumps({'outcome': report['outcome'], 'error': report.get('error'),
                          'evidence': str(args.output/'result.json')}), flush=True)
    return report['outcome'] == 'usable_foothold'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--observe-seconds', type=int, default=30)
    parser.add_argument('--clock-probe', action='store_true', help='Verify a slow review stays paused and completed work does not resume time')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
