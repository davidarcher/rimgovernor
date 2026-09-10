"""Disposable native construction and bounded drilling through shared Hands."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge import runtime_file_read
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.headless import isolated_root
from rimbot.production_policy import resource_method, sync_production_policy
from rimbot.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
from rimbot.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    store = Store(args.output / 'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True)
    report = {'passed':False, 'cases':[]}

    def save():
        (args.output / 'result.json').write_text(json.dumps(report, indent=2))

    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence)); save()
        print(name, passed, flush=True)
        assert passed, name

    async def sources():
        return await rt.game.invoke('home/resource_sources', {'resource':'Plasteel', 'development':True})

    async def window(ticks=600):
        async with rt.lock:
            await sync_production_policy(rt)
        await rt.supervisor.change('Superfast', max_ticks=ticks)
        async with asyncio.timeout(90):
            while True:
                state = (await runtime_file_read(rt.bridge.call, 'home/supervised_play', op='status')).structuredContent
                if not state['active']: break
                await asyncio.sleep(.2)
        assert state['pauseVerified'], state
        if state['stopReason'] == 'letter_pause':
            danger = await rt.game.query('home/status', colonists=False, threats=True)
            assert danger['counts']['hostileCount'] == danger['counts']['huntingPredatorCount'] == 0
            rt.supervisor.absorb(state); rt.supervisor.allow_resume()
        else:
            assert state['stopReason'] in ('tick_budget', 'requested_pause'), state
        async with rt.lock: await rt.refresh_clock_events()
        if rt.review_task and not rt.review_task.done(): await rt.review_task
        report['latest'] = await sources()
        report['pawns'] = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True)
        report['buildings'] = await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True)
        save()

    async def work(action):
        return (await rt.bridge.call('test/deep_mining_fixture', action=action)).structuredContent

    try:
        await ready(rt)
        setup = await work('setup')
        record('fixture', setup.get('success'), setup=setup)
        for _ in range(10):
            await window(120)
            if report['latest']['infrastructure']['scannersActive']: break
        initial = await sources()
        record('powered_native_sites', initial['infrastructure']['scannersActive'] and bool(initial['infrastructure']['sites']), sources=initial)
        record('baseline_deposits', (await work('seed')).get('success'))
        facts = await rt.game.query('home/colony_facts', planning=True)
        initial_stock = facts['resources'].get('Plasteel', 0)
        goal_id = 'MaintainResource-Plasteel'
        goal = rt.current_plan.colony_goals[goal_id] = ColonyGoal(source='PLAYER', priority_class=3,
            target={'resource':'Plasteel','quantity':initial_stock+2,'deep_extraction':True})
        for deposit in range(2):
            await work('construct')
            for _ in range(10):
                facts = await rt.game.query('home/colony_facts', planning=True)
                method, actions = await resource_method(rt, goal_id, facts)
                steps, _ = rt.controller.skills.steps(goal_id, method, actions, facts)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Bounded extraction development acceptance', steps=steps).decision(rt.current_plan),
                    actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
                goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
                for step in steps:
                    rt.manual_requests.append((step.id, rt.context_token, rt.chat_revision))
                    await rt.execute_manual_requests()
                    record('hands_'+step.id, rt.current_plan.progress[step.id].state in ('complete','waiting'),
                        progress=rt.current_plan.progress[step.id].model_dump(mode='json'))
                if actions[0]['kind'] == 'place_buildings': break
                if actions[0]['kind'] == 'native_operation':
                    await work('basic')
                    deadline = time.monotonic() + args.seconds
                    target = actions[0]['arguments']['thing']
                    while time.monotonic() < deadline:
                        await window()
                        retired = next(d for d in report['latest']['infrastructure']['drills'] if d['thingId'] == target)
                        if retired['switchOn'] is False: break
                    record('native_power_release_'+target, retired['switchOn'] is False, drill=retired)
                    await work('construct')
            site = goal.evidence['extraction_facility']
            deadline = time.monotonic() + args.seconds
            while time.monotonic() < deadline:
                await window()
                built = [d for d in report['latest']['infrastructure']['drills'] if d['x']==site['x'] and d['z']==site['z']]
                if built: break
            record('native_facility_'+str(deposit), bool(built), site=site, buildings=built)
            await rt.projects.reconcile(rt.game, plan=rt.current_plan)
            rt.reconcile_plan()
            if deposit == 0:
                owned = (await sources())['infrastructure']['owned']
                old_load = rt.identity['loadToken']
                checkpoint = await create_checkpoint(rt, rt.context_token)
                report['checkpoint'] = checkpoint
                await stop_for_restart(rt, rt.context_token, checkpoint['manifest_path'])
                await rt.stop(); store.close()
                _, state_root = prepare_resume(checkpoint['manifest_path'])
                store = Store(state_root / 'bridge.sqlite')
                rt = BridgeRuntime(store, root, fresh=True, headless=True, resume=checkpoint['manifest_path'])
                await ready(rt)
                goal = rt.current_plan.colony_goals[goal_id]
                record('paired_drill_ownership_restored', rt.mode == 'manual' and rt.identity['loadToken'] != old_load
                    and (await sources())['infrastructure']['owned'] == owned, owned=owned)
            await work('mine')
            deadline = time.monotonic() + args.seconds
            while time.monotonic() < deadline:
                await window()
                recovered = sum(r['recovered'] for r in report['latest']['infrastructure']['owned'])
                if recovered >= deposit+1: break
            record('native_drilling_output_'+str(deposit), recovered >= deposit+1,
                owned=report['latest']['infrastructure']['owned'])
        before = sum(r['recovered'] for r in report['latest']['infrastructure']['owned'])
        progress_before = {d['thingId']:d['progress'] for d in report['latest']['infrastructure']['drills']}
        remaining = report['latest']['infrastructure']['deposits']
        await window(1200)
        record('stock_target_stops_native_work', bool(remaining)
            and report['latest']['infrastructure']['deposits'] == remaining
            and {d['thingId']:d['progress'] for d in report['latest']['infrastructure']['drills']} == progress_before
            and sum(r['recovered'] for r in report['latest']['infrastructure']['owned']) == before)
        await work('haul')
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            await window()
            if report['latest']['storage']['stored'] >= initial_stock+2: break
        record('actual_deep_output_stored', report['latest']['storage']['stored'] >= initial_stock+2)
        record('zero_inference', rt.counters.get('model_calls', 0) == 0)
        report['passed'] = True
    except Exception as error:
        report['error'] = repr(error)
        raise
    finally:
        save(); await rt.stop(); store.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=900)
    args = parser.parse_args(); args.output.mkdir(parents=True, exist_ok=False)
    asyncio.run(run(args))
