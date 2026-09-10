"""Disposable native surface mining: safety, interruption, depletion and real output."""
import argparse
import asyncio
import json
import time
from pathlib import Path

from session_checkpoint_acceptance import ready
from rimgovernor.bridge import BridgeError
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.colony_plan import ColonyGoal, CommitSteps, PlanStep
from rimgovernor.production_policy import resource_method
from rimgovernor.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
from rimgovernor.headless import isolated_root
from rimgovernor.store import Store
from rimgovernor.native_scenario import advance_game


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    store = Store(args.output / 'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True)
    report = {'passed': False, 'cases': []}

    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        (args.output / 'result.json').write_text(json.dumps(report, indent=2))
        print(name, passed, flush=True)
        assert passed, name

    async def fixture(**args):
        return (await rt.bridge.call('test/mining_fixture', **args)).structuredContent

    async def sources():
        return await rt.game.invoke('home/resource_sources', {'resource': 'Steel'})

    async def window(ticks):
        await advance_game(rt, ticks, report, timeout=90, expected_letters=())
        async with rt.lock:
            await rt.refresh_clock_events()
        if rt.review_task and not rt.review_task.done(): await rt.review_task

    async def dispatch(target, refuse=False):
        args = {k: rt.identity[k] for k in ('colonyId', 'loadToken', 'mapId')}
        args.update({k: target[k] for k in ('thingId', 'resource', 'x', 'z')})
        try:
            return await rt.game.invoke('home/acquire_resource', dict(args, dryRun=False), allow_write=True)
        except BridgeError as error:
            if not refuse or 'Resource source changed or is unsafe/unavailable' not in error.detail: raise
            return {'success': False, 'native_error': error.result.model_dump(mode='json')}

    try:
        await ready(rt)
        try:
            await rt.bridge.detail('test/mining_fixture')
        except Exception as error:
            report.update(outcome='missing_prerequisite', error='Private MiningFixture assembly required: '+str(error))
            return
        setup = await fixture()
        record('fixture_ready', setup.get('success'), fixture=setup, identity=rt.identity)
        first, second, third = setup['targets']
        observed = await sources()
        record('surface_sources_discovered', all(any(s['thingId'] == t['thingId'] and s['safety'] == 'open_surface'
            for s in observed['sources']) for t in setup['targets']), sources=observed)
        stored_before = observed['storage']['stored']
        facts = await rt.game.query('home/colony_facts', planning=True)
        goal_id = 'MaintainResource-Steel'
        goal = rt.current_plan.colony_goals[goal_id] = ColonyGoal(source='PLAYER', priority_class=3,
            target={'resource': 'Steel', 'quantity': facts['resources'].get('Steel', 0) + 1})
        for _ in range(4):
            method, actions = await resource_method(rt, goal_id, facts)
            if actions[0]['kind'] != 'create_zone': break
            steps, _ = rt.controller.skills.steps(goal_id, method, actions, facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Stage material storage', steps=steps).decision(rt.current_plan), actor='strategist',
                expected_token=rt.context_token, expected_revision=rt.chat_revision)
            rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
            await rt.execute_manual_requests()
            goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
            record('storage_' + method, all(rt.current_plan.progress[s.id].state == 'complete' for s in steps), actions=actions)
        expected_yield = next(s['yield'] for s in observed['sources'] if s['thingId'] == first['thingId'])
        record('material_storage_ready', (await sources())['storage']['capacity'] >= expected_yield)
        await fixture(action='roof', x=first['x'], z=first['z'])
        refused = await dispatch(first, refuse=True)
        record('roof_hazard_refused', refused.get('success') is False, receipt=refused, sources=await sources())
        await fixture(action='unroof', x=first['x'], z=first['z'])
        receipt = await dispatch(first)
        record('designation_is_not_output', receipt.get('success') and not any(
            e['finished'] >= 0 for e in (await sources())['extractions']), receipt=receipt)
        await fixture(action='cancel', x=first['x'], z=first['z'])
        await window(300)
        record('cancelled_dig_does_not_complete', any(s['thingId'] == first['thingId'] and not s['designated']
            for s in (await sources())['sources']))
        record('cancel_releases_ownership', next(e for e in (await sources())['extractions']
            if e['thingId'] == first['thingId'])['cancelled'])
        # Explicitly renewed player direction allows reissuing the exact cancelled source.
        assert (await dispatch(first))['success']
        await fixture(action='roof', x=first['x'], z=first['z'])
        for _ in range(8):
            await window(600)
            guarded = await sources()
            if any(e['thingId'] == first['thingId'] and e['blocker'] for e in guarded['extractions']): break
        record('new_roof_prevents_owned_mining', any(e['thingId'] == first['thingId'] and e['finished'] < 0 and e['blocker']
            for e in guarded['extractions']), sources=guarded)
        await fixture(action='unroof', x=first['x'], z=first['z'])
        for target in (first, second):
            operation = {**rt.identity, **{k: target[k] for k in ('thingId', 'resource', 'x', 'z')}, 'dryRun': False}
            operation = {k: v for k, v in operation.items() if k in ('colonyId', 'loadToken', 'mapId', 'thingId', 'resource', 'x', 'z', 'dryRun')}
            step = PlanStep(id='mine-' + target['thingId'], title='Bounded native extraction',
                completion_criteria='Native designation; actual output verified separately',
                action={'kind': 'native_operation', 'tool': 'home/acquire_resource', 'arguments': operation})
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Surface mining acceptance', steps=[step]).decision(rt.current_plan), actor='strategist',
                expected_token=rt.context_token, expected_revision=rt.chat_revision)
            rt.manual_requests.append((step.id, rt.context_token, rt.chat_revision))
            await rt.execute_manual_requests()
            record('hands_' + target['thingId'], rt.current_plan.progress[step.id].state == 'complete',
                progress=rt.current_plan.progress[step.id].model_dump(mode='json'))
            deadline = time.monotonic() + args.seconds
            while time.monotonic() < deadline:
                await window(600)
                observed = await sources()
                report['latest'] = observed
                report['pawns'] = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True)
                (args.output / 'progress.json').write_text(json.dumps(report, indent=2))
                if any(e['thingId'] == target['thingId'] and e['finished'] >= 0 for e in observed['extractions']): break
            evidence = next(e for e in observed['extractions'] if e['thingId'] == target['thingId'])
            record('recovered_' + target['thingId'], evidence['finished'] >= 0 and evidence['recovered'] > 0
                and not any(s['thingId'] == target['thingId'] for s in observed['sources']), extraction=evidence)
            geometry = await fixture(action='inspect', x=target['x'], z=target['z'])
            record('safe_geometry_' + target['thingId'], geometry['standable']
                and geometry['unknown'] == geometry['roofs'] == geometry['collapsing'] == 0, geometry=geometry)
        record('depleted_receipt_cannot_replay', (await dispatch(first, refuse=True)).get('success') is False)
        await fixture(action='hauling')
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline and (await sources())['storage']['stored'] <= stored_before:
            await window(600)
        record('actual_material_hauling', (await sources())['storage']['stored'] > stored_before,
            before=stored_before, storage=(await sources())['storage'])
        assert (await dispatch(third))['success']
        old_identity = dict(rt.identity)
        retained = (await sources())['extractions']
        checkpoint = await create_checkpoint(rt, rt.context_token)
        report['checkpoint'] = checkpoint
        await stop_for_restart(rt, rt.context_token, checkpoint['manifest_path'])
        await rt.stop()
        store.close()
        _, state_root = prepare_resume(checkpoint['manifest_path'])
        store = Store(state_root / 'bridge.sqlite')
        rt = BridgeRuntime(store, root, fresh=True, headless=True, resume=checkpoint['manifest_path'])
        await ready(rt)
        restored = (await sources())['extractions']
        record('paired_pending_mining_restored', rt.mode == 'manual' and rt.identity['loadToken'] != old_identity['loadToken']
            and [{k:v for k,v in e.items() if k != 'thingId'} for e in restored]
            == [{k:v for k,v in e.items() if k != 'thingId'} for e in retained], before=retained, after=restored)
        await fixture(action='mining')
        report['restored_sources'] = await sources()
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            await window(600)
            observed = await sources()
            pending = next(e for e in observed['extractions'] if e['sourceId'] == third['thingId'])
            report['latest'] = observed
            report['pawns'] = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True)
            (args.output / 'progress.json').write_text(json.dumps(report, indent=2))
            if pending['finished'] >= 0 or pending['cancelled']: break
        record('resumed_native_output', pending['finished'] >= 0 and pending['recovered'] > 0, extraction=pending)
        geometry = await fixture(action='inspect', x=third['x'], z=third['z'])
        record('safe_resumed_geometry', geometry['standable']
            and geometry['unknown'] == geometry['roofs'] == geometry['collapsing'] == 0, geometry=geometry)
        record('zero_inference', rt.counters.get('model_calls', 0) == 0)
        report['passed'] = True
    except Exception as error:
        report['error'] = repr(error)
        report['execution'] = {key: value.model_dump(mode='json') for key, value in rt.current_plan.progress.items()}
        raise
    finally:
        (args.output / 'result.json').write_text(json.dumps(report, indent=2))
        await rt.stop()
        store.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=900, help='Wall-time limit per pawn-outcome phase')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    asyncio.run(run(args))
