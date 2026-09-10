"""Shared bounded native construction and failure-diagnosis scenarios."""
import argparse
import asyncio
import base64
import json
import shutil
from pathlib import Path
import time
import traceback

from session_checkpoint_acceptance import ready
from native_scenario_support import allow_starting_supplies, baseline_tick
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.flight_recorder import recorder
from rimgovernor.native_scenario import advance_game, ScenarioInterrupted
from rimgovernor.headless import isolated_root, prepare_rendered
from rimgovernor.player_commands import apply_command
from rimgovernor.store import Store


class MissingPrerequisite(ValueError):
    pass


async def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    root = isolated_root(args.source_root, args.output/'bridge')
    if args.rendered:
        prepare_rendered(root)
    checkpoint = None
    state_path = args.output/'state.sqlite'
    if getattr(args, 'checkpoint', None):
        from rimgovernor.session_checkpoint import prepare_resume, digest
        data = json.loads((args.checkpoint/'checkpoint.json').read_text())
        if data.get('root') != str(root.resolve()) or data.get('owned') is not True or data.get('headless') != (not args.rendered):
            raise MissingPrerequisite('Checkpoint must belong to this named-runner root, ownership and display mode')
        name = data['save_name']
        if Path(name).name != name or name in ('.', '..'):
            raise MissingPrerequisite('Invalid checkpoint name')
        destination = root/'checkpoints'/name
        destination.mkdir(parents=True)
        for file in ('checkpoint.json', 'game.rws', 'bridge.sqlite'):
            shutil.copy2(args.checkpoint/file, destination/file)
        checkpoint = destination/'checkpoint.json'
        data, state = prepare_resume(checkpoint)
        state_path = state/'bridge.sqlite'
    store = Store(state_path)
    rt = BridgeRuntime(store, root, fresh=True, headless=not args.rendered, resume=checkpoint)
    report = dict(category='infrastructure_failure', case=args.case)
    recording = recorder()
    async def observe():
        return dict(identity=await rt.game.query('home/colony_identity'),
                    time=(await rt.game.query('home/status', colonists=False, threats=False))['time'],
                    clock=(await rt.bridge.call('home/supervised_play', op='status')).structuredContent,
                    pawns=await rt.game.query('home/list_pawns', colonistsOnly=True, work=True),
                    facts=await rt.game.query('home/colony_facts', planning=True),
                    buildings=await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True))
    async def command(**payload):
        result = await apply_command(rt, payload, token=rt.context_token, revision=rt.chat_revision)
        await rt.execute_manual_requests()
        return result
    try:
        await ready(rt)
        report['initial'] = await observe()
        if args.case == 'checkpoint-continuation':
            assert checkpoint is not None
            assert rt.mode == 'manual' and rt.identity['colonyId'] == data['colony_id'] and rt.identity['mapId'] == data['map_id']
            assert rt.identity['loadToken'] != data['load_token']
            assert report['initial']['time']['ticksGame'] in (data['tick'], data['tick']+1)
            candidates = [s for s in rt.current_plan.spec.steps if s.action.kind == 'place_buildings'
                          and rt.current_plan.progress[s.id].issued]
            if not candidates:
                raise MissingPrerequisite('Checkpoint has no already-issued construction action')
            report['retained_actions'] = [s.model_dump(mode='json') for s in candidates]
            placements = [p for s in candidates for p in s.action.placements]
            def matching(buildings):
                return [b for p in placements for b in buildings['buildings']
                        if b.get('position') == dict(x=p.x, z=p.z) and b.get('buildDefName', b.get('defName')) == p.def_name
                        and (not p.materials or b.get('stuff') in p.materials)]
            initial_targets = report['accepted_targets'] = matching(report['initial']['buildings'])
            if not any(b.get('isBlueprint') or b.get('isFrame') for b in initial_targets):
                raise MissingPrerequisite('Checkpoint has no observed pending native construction')
            for _ in range(20):
                await rt.projects.reconcile(rt.game, plan=rt.current_plan)
                rt.reconcile_plan()
                targets = matching(await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True))
                report['actual'] = [b for b in targets if b.get('status') == 'built'
                                    and b.get('isBlueprint') is False and b.get('isFrame') is False]
                if len(report['actual']) == len(placements):
                    break
                await advance_game(rt, 600, report, timeout=60)
            else:
                raise AssertionError('Retained construction did not complete within 12000 native ticks')
            report['last_observation'] = await observe()
            assert all(report['last_observation']['identity'][key] == report['initial']['identity'][key]
                       for key in ('colonyId', 'mapId', 'loadToken'))
            assert digest(checkpoint.parent/'game.rws') == data['game_sha256']
            assert digest(checkpoint.parent/'bridge.sqlite') == data['database_sha256']
            assert rt.counters['actions'] == 0, 'Continuation redispatched a game order'
            report.update(category='passed', start='immutable checkpoint; previously issued work only')
            return True
        expected_tick = baseline_tick(args.source_root/'profile/Saves/RimGovernor-tribal8-baseline.rws')
        initial_tick = report['initial']['time']['ticksGame']
        report['baseline_tick'] = dict(expected=expected_tick, actual=initial_tick)
        if args.case in ('construction', 'blocked-construction', 'startup', 'endurance'):
            assert initial_tick == expected_tick and report['initial']['time']['paused'], 'Fresh paused load tick differs from immutable baseline'
        if args.case == 'startup':
            report['category'] = 'passed'
            return True
        if args.case.startswith('warning-'):
            variant = args.case.removeprefix('warning-')
            try:
                await rt.bridge.detail('test/interruption_letter')
            except Exception as error:
                raise MissingPrerequisite('Private InterruptionFixtures assembly is required') from error
            async def inject():
                async with asyncio.timeout(30):
                    while not (await rt.supervisor.call(op='status'))['active']:
                        await asyncio.sleep(.05)
                if variant == 'load':
                    report['injection'] = (await rt.bridge.call('rimworld/load_game_ready',
                        saveName='RimGovernor-tribal8-baseline', readiness='visual', timeoutMs=90000)).model_dump(mode='json')
                else:
                    report['injection'] = (await rt.bridge.call('test/interruption_letter',
                        label='Ancient danger', after='none' if variant=='recovery' else variant)).structuredContent
            injection = asyncio.create_task(inject())
            try:
                try:
                    await advance_game(rt, 6000, report)
                except (ScenarioInterrupted, ValueError) as error:
                    assert variant != 'recovery', str(error)
                    report['refusal'] = str(error)
                else:
                    assert variant == 'recovery', 'Unsafe native interruption was resumed'
                    interruptions = report['simulation'][0]['interruptions']
                    assert any(i.get('acknowledgedLetterId') for i in interruptions), 'Injected warning was not acknowledged'
                await injection
                if variant != 'recovery':
                    assert not report['simulation'][0].get('completed'), 'Refused simulation completed'
                    assert not any(i.get('acknowledgedLetterId') for i in report['simulation'][0]['interruptions']), 'Unsafe warning acknowledged'
                    safety = report['refusal_status'] = await rt.game.query('home/status', colonists=True, threats=True)
                    if variant == 'raid':
                        assert safety['counts']['hostileCount'] > 0, 'Native raid produced no observed threat'
                    if variant == 'modal':
                        assert safety['ui']['modalOpen'], 'Native modal was not observed'
                    if variant == 'load':
                        assert (await rt.game.query('home/colony_identity'))['loadToken'] != report['initial']['identity']['loadToken']
            finally:
                if not injection.done():
                    injection.cancel()
                await asyncio.gather(injection, return_exceptions=True)
            report['last_observation'] = await observe()
            assert report['last_observation']['time']['paused'], 'Native refusal left simulation running'
            report['category'] = 'passed'
            return True
        if args.case == 'endurance':
            await allow_starting_supplies(rt)
            report['windows'] = []
            began = time.monotonic()
            actual_tick = initial_tick
            for index in range(30):
                target_tick = initial_tick+(index+1)*6000
                window = time.monotonic()
                if time.monotonic()-began >= args.seconds:
                    raise TimeoutError('Three-day native endurance exceeded wall budget')
                status = await advance_game(rt, target_tick-actual_tick, report)
                observed = await observe()
                report['last_observation'] = observed
                actual_tick = observed['time']['ticksGame']
                report['windows'].append(dict(index=index, seconds=time.monotonic()-window,
                    expected_tick=target_tick, actual_tick=actual_tick, clock=status))
                (args.output/'progress.json').write_text(json.dumps(report, indent=2))
                assert observed['time']['paused'] and actual_tick == target_tick, 'Endurance boundary differs'
                assert all(observed['identity'][key] == report['initial']['identity'][key]
                           for key in ('colonyId', 'mapId', 'loadToken')), 'Endurance native identity changed'
            report.update(category='passed', elapsed_seconds=time.monotonic()-began,
                          native_ticks=180000, scope='Three native days; no colony survival or pawn outcome assertion')
            return True
        if args.case == 'assertion-failure':
            raise AssertionError('Injected postcondition: expected absent fixture building; actual native buildings retained')
        if args.case == 'timeout':
            async with asyncio.timeout(1):
                await asyncio.Event().wait()
        if args.case == 'native-exit':
            # Exercise loss of this owned native process, without touching any peer.
            stopped = await rt.bridge.core('games_stop', gameId=rt.bridge.game_id)
            report['native_exit_stop'] = stopped.model_dump(mode='json')
            status = (await rt.bridge.core('games_status', gameId=rt.bridge.game_id)).structuredContent or {}
            report['native_exit_observation'] = status
            assert status.get('status') in ('stopped', 'stale-runtime-cleaned'), 'Owned native process exit was not observed'
            rt.owned_game_stopped = True
            report['frame_omission'] = 'Owned native process exited before failure capture'
            report['checkpoint_omission'] = 'Owned native process exited; no save requested'
            raise RuntimeError('Injected owned native process exit; game cannot supply further outcomes')
        facts = await allow_starting_supplies(rt)
        if facts['resources'].get('WoodLog', 0) < 5:
            raise MissingPrerequisite('At least five accessible WoodLog are required')
        builders = [p for p in report['initial']['pawns']['pawns']
                    if any(w['name']=='Construction' and not w['disabled'] for w in p['work']['types'])]
        if not builders:
            raise MissingPrerequisite('A capable builder is required')
        chosen = None
        for cell in sorted(facts['cells'], key=lambda c: (c['x']-facts['center']['x'])**2+(c['z']-facts['center']['z'])**2):
            if cell['occupied'] or not cell['walkable']:
                continue
            preview = await rt.game.invoke('home/place_building', dict(defName='Wall', stuff='WoodLog',
                x=cell['x'], z=cell['z'], rotation='north', dryRun=True))
            if preview.get('canPlace') is True:
                chosen = cell
                break
        if chosen is None:
            raise MissingPrerequisite('No observed legal wall site')
        for pawn in builders:
            await command(kind='SetWorkPriority', pawn=pawn['thingId'], work_type='Construction',
                          priority=0 if args.case=='blocked-construction' else 1)
        result = await command(kind='PlaceBuildings', buildings={'kind':'place_buildings', 'placements':[
            dict(def_name='Wall', materials=['WoodLog'], x=chosen['x'], z=chosen['z'])]})
        report['action'] = result
        report['target'] = dict(x=chosen['x'], z=chosen['z'], defName='Wall', stuff='WoodLog')
        pending = await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True)
        report['accepted_targets'] = [b for b in pending['buildings']
            if b.get('position') == {'x':chosen['x'], 'z':chosen['z']} and b.get('stuff')=='WoodLog'
            and (b.get('isBlueprint') is True or b.get('isFrame') is True)]
        assert report['accepted_targets'], 'Native pending construction was not separately observed after dispatch'
        if getattr(args, 'checkpoint_before_work', False):
            from rimgovernor.session_checkpoint import create_checkpoint
            report['pending_checkpoint'] = await create_checkpoint(rt, rt.context_token)
        start = time.monotonic()
        limit = 1200 if args.case=='blocked-construction' else 12000
        advanced = 0
        while advanced < limit and time.monotonic()-start < args.seconds:
            status = await advance_game(rt, 600, report, timeout=60)
            advanced += 600
            observed = await observe()
            report['last_observation'] = observed
            assert all(observed['identity'][key] == report['initial']['identity'][key]
                       for key in ('colonyId', 'mapId', 'loadToken')), 'Native colony/map/load changed'
            walls = [b for b in observed['buildings']['buildings'] if b.get('position') == {'x':chosen['x'], 'z':chosen['z']}
                     and b.get('defName')=='Wall' and b.get('stuff')=='WoodLog' and b.get('status')=='built'
                     and b.get('isBlueprint') is False and b.get('isFrame') is False]
            report['actual'] = walls
            (args.output/'progress.json').write_text(json.dumps(report, indent=2))
            if walls:
                assert args.case != 'blocked-construction', 'Disabled builders unexpectedly completed construction'
                assert observed['facts']['resources']['WoodLog'] < facts['resources']['WoodLog'], 'No material consumption'
                report['category'] = 'passed'
                break
        else:
            if args.case == 'blocked-construction':
                accepted_ids = {b['thingId'] for b in report['accepted_targets']}
                report['pending_targets'] = [b for b in report['last_observation']['buildings']['buildings']
                    if b.get('thingId') in accepted_ids and (b.get('isBlueprint') is True or b.get('isFrame') is True)]
                assert report['pending_targets'], 'Blocked construction disappeared instead of remaining unfinished'
                report.update(category='passed', negative_case='Accepted orders did not certify construction; disabled builders retained')
            else:
                raise AssertionError('Expected completed native WoodLog wall before tick/wall budget; actual='+json.dumps(report.get('actual')))
    except MissingPrerequisite as error:
        report.update(category='missing_prerequisite', error=str(error))
    except TimeoutError as error:
        report.update(category='timeout', error=str(error) or 'Native wait exceeded deadline')
    except AssertionError as error:
        report.update(category='assertion_failure', error=str(error) or 'Assertion without a message; see traceback',
                      traceback=traceback.format_exc())
    except Exception as error:
        report.update(category='infrastructure_failure', error=repr(error), traceback=traceback.format_exc())
    finally:
        report['plan'] = rt.current_plan.model_dump(mode='json')
        if args.rendered and rt.connected and args.case != 'native-exit':
            try:
                async with rt.lock:
                    frame, source = await rt._capture_visual_source()
                (args.output/'frame.png').write_bytes(base64.b64decode(frame.split(',', 1)[1]))
                report['frame'] = source
            except Exception as error:
                report['frame_omission'] = repr(error)
        if report['category'] != 'passed' and args.case != 'native-exit' and rt.connected:
            try:
                from rimgovernor.session_checkpoint import create_checkpoint
                status = (await rt.bridge.call('home/supervised_play', op='status')).structuredContent
                native_time = (await rt.game.query('home/status', colonists=False, threats=False))['time']
                if native_time.get('paused') is True and not status.get('active'):
                    report['failure_checkpoint'] = await create_checkpoint(rt, rt.context_token)
                else:
                    report['checkpoint_omission'] = 'Native pause was not verified; no save requested'
            except Exception as error:
                report['checkpoint_omission'] = repr(error)
        if recording:
            recording.event('failure_summary', category=report['category'], error=report.get('error'),
                            target=report.get('target'), actual=report.get('actual'),
                            baseline_tick=report.get('baseline_tick'))
            recording.event('scenario_outcome', **report)
            report['recorder'] = recording.stats()
        (args.output/'scenario-result.json').write_text(json.dumps(report, indent=2))
        await rt.stop()
        store.close()
    return report['category']=='passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--case', required=True)
    parser.add_argument('--seconds', type=int, default=300)
    parser.add_argument('--rendered', action='store_true')
    parser.add_argument('--checkpoint', type=Path)
    parser.add_argument('--checkpoint-before-work', action='store_true')
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args)) else 1)
