"""Bounded native upkeep outcomes through ColonyPlan and Hands, without inference."""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge import BridgeError
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.colony_upkeep import upkeep_nodes, upkeep_method, reconcile_upkeep
from rimbot.headless import isolated_root, prepare
from rimbot.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare(root)
    store = Store(args.output / 'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', cases=[], samples=[], scope='Fixture-prepared targets; ordinary pawn hauling, repair and cleaning via shared Hands. No sustained survival claim.')
    try:
        report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config, {'model': 'no inference'})
        await ready(rt)
        rt.execution_task = asyncio.current_task()
        report['setups'] = []
        report['installed_order_schema'] = await rt.game.describe('home/order')
        for goal_id, field in [('SecureSupplies', 'medicine'), ('MaintainEssentialRepairs', 'wall'), ('MaintainCleanFacilities', 'filth'), ('MaintainFireSafety', 'fire')]:
            setup = (await rt.bridge.call('test/upkeep_setup', fireSize=.1 if field == 'fire' else 0,
                storageMissing=goal_id == 'SecureSupplies', repairCompetition=goal_id == 'MaintainEssentialRepairs')).structuredContent
            assert setup['success'], setup
            report['setups'].append(setup)
            facts = await rt.game.query('home/colony_facts', planning=True)
            report['samples'].append(facts)
            assert not facts['upkeep']['errors'], facts['upkeep']['errors']
            roster = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True, health=True)
            report['last_roster'] = roster
            upkeep_nodes(facts, rt.current_plan.control)
            state = rt.current_plan.control['upkeep'][goal_id]
            # Isolate the declared fixture target, retaining unmodified native facts.
            state['targets'] = [r for r in state['targets'] if r['id'] in (setup[field], setup.get('cosmetic'))]
            if goal_id == 'MaintainEssentialRepairs':
                assert len(state['targets']) == 2 and state['targets'][0]['id'] == setup['wall']
                essential, cosmetic = state['targets']
                assert essential['repairPriority'] < cosmetic['repairPriority']
                assert essential['hitPoints'] / essential['maxHitPoints'] > cosmetic['hitPoints'] / cosmetic['maxHitPoints']
                report['repair_priority'] = state['targets']
            assert state['targets'], dict(goal=goal_id, state=state)
            goal = rt.current_plan.colony_goals[goal_id] = ColonyGoal(priority_class=3)
            compiled = await upkeep_method(rt, goal_id, facts, roster['pawns'])
            if goal_id == 'MaintainEssentialRepairs':
                assert compiled[1][0]['arguments']['target'] == setup['wall'], compiled
            if goal_id == 'SecureSupplies':
                assert compiled and compiled[1][0]['kind'] == 'create_zone', compiled
                method, actions = compiled
                steps, _ = rt.controller.skills.steps(goal_id, method, actions, facts)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Covered supply storage acceptance', steps=steps).decision(rt.current_plan),
                    actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
                goal.steps = [s.id for s in steps]
                goal.evidence.setdefault('methods', {})[method] = goal.steps[:]
                rt.mode = 'automate'
                await rt.hands.advance(rt)
                rt.mode = 'manual'
                assert rt.current_plan.progress[steps[0].id].state == 'complete', rt.current_plan.progress[steps[0].id]
                report['storage_creation'] = dict(action=actions[0], progress=rt.current_plan.progress[steps[0].id].model_dump())
                patch = actions[0]['patches'][0]
                try:
                    refusal = (await rt.bridge.call('home/zone_cells', op='create', zoneType='stockpile',
                        **patch, preset='nothing', requireCoveredEmpty=True, dryRun=False, watch=False)).structuredContent
                except BridgeError as error:
                    refusal = error.result.structuredContent
                assert refusal and refusal.get('success') is False, refusal
                report['storage_creation']['occupied_cell_refusal'] = refusal
                from home_coverage_acceptance import verify as verify_home
                await verify_home(rt, report)
                assert report['home_coverage']['target'].startswith('stockpile:')
                facts = await rt.game.query('home/colony_facts', planning=True)
                report['samples'].append(facts)
                upkeep_nodes(facts, rt.current_plan.control)
                rt.current_plan.control['upkeep'][goal_id]['targets'] = [r for r in
                    rt.current_plan.control['upkeep'][goal_id]['targets'] if r['id'] == setup[field]]
                compiled = await upkeep_method(rt, goal_id, facts, roster['pawns'])
                assert compiled and compiled[1][0].get('completion') == 'upkeep_target', compiled
            progress = None
            if compiled is None:
                assert field == 'fire' and goal.evidence.get('waiting_for_native_fire')
                report['cases'].append(dict(goal=goal_id, method='ordinary enabled native firefighting', orders=0))
            else:
                method, actions = compiled
                steps, _ = rt.controller.skills.steps(goal_id, method, actions, facts)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Native upkeep outcome acceptance', steps=steps).decision(rt.current_plan),
                    actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
                goal.steps = [s.id for s in steps]
                rt.mode = 'automate'
                await rt.hands.advance(rt)
                rt.mode = 'manual'
                progress = rt.current_plan.progress[steps[0].id]
                assert progress.state == 'waiting', progress.model_dump()
                report['cases'].append(dict(goal=goal_id, receipt=progress.model_dump()))
            deadline = time.monotonic() + args.seconds
            while time.monotonic() < deadline:
                status = await rt.game.query('home/status', colonists=False, threats=True)
                assert status['threats'].get('hostileCount') == 0 and status['threats'].get('huntingPredatorCount') == 0, status
                await rt.bridge.call('rimworld/set_time_speed', speed='Superfast', ultraSpeedBoost=False)
                await asyncio.sleep(1)
                await rt.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                facts = await rt.game.query('home/colony_facts')
                report['samples'].append(facts)
                reconcile_upkeep(rt, facts)
                if progress is None:
                    if not any(r['id'] == setup[field] for r in facts['upkeep']['fires']): break
                elif progress.state != 'waiting': break
            if progress is None:
                assert not any(r['id'] == setup[field] for r in facts['upkeep']['fires']), 'Fire remained'
                report['cases'][-1]['outcome'] = dict(state='complete', tick=facts['tick'], fires=facts['upkeep']['fires'])
            else:
                assert progress.state == 'complete', progress.model_dump()
                report['cases'][-1]['outcome'] = progress.model_dump()
            print(goal_id + ': native target postcondition observed', flush=True)
        setup = (await rt.bridge.call('test/upkeep_setup', animals=True, restrictWorkers=True)).structuredContent
        assert setup['success'], setup
        report['setups'].append(setup)
        facts = await rt.game.query('home/colony_facts')
        report['samples'].append(facts)
        assert 'animals' not in facts['upkeep']['errors'], facts['upkeep']['errors']
        animals = {p['id']: p for p in facts['upkeep']['animals']}
        penned, loose, pet = (animals[setup[k]] for k in ('penAnimal', 'looseAnimal', 'pet'))
        assert penned['requiresPen'] is True and penned['contained'] is True and penned['pen'] == setup['pen'], penned
        assert loose['requiresPen'] is True and loose['contained'] is False, loose
        assert pet['requiresPen'] is False and pet['contained'] is None, pet
        assert any(t['id'] == setup['feed'] and t['nutrition'] > 0 for t in penned['reachableStoredFeed']), penned
        assert not any(t['id'] == setup['feed'] for t in loose['reachableStoredFeed']), loose
        report['cases'].append(dict(goal='NativeAnimalEvidence', outcome=dict(state='complete'),
            penned=penned, loose=loose, pet=pet))
        print('NativeAnimalEvidence: pen, pet and feed access observed', flush=True)
        refusals = []
        for action, field in [('haul', 'medicine'), ('repair', 'wall'), ('clean', 'filth')]:
            order_args = dict(action=action, pawn=setup['workers'][0], target=setup[field], dryRun=False, draft=False, watch=False)
            if action == 'haul': order_args['requireSafeStorage'] = True
            try:
                result = (await rt.bridge.call('home/order', **order_args)).structuredContent
            except BridgeError as error:
                result = error.result.structuredContent
            assert result and result.get('success') is False, result
            refusals.append(dict(action=action, result=result))
        report['cases'].append(dict(goal='AllowedAreaPreserved', outcome=dict(state='complete'), refusals=refusals))
        print('AllowedAreaPreserved: native upkeep writes refused', flush=True)
        assert rt.counters['model_calls'] == 0
        report['outcome'] = 'passed'
    except Exception as error:
        report.update(error=str(error), traceback=traceback.format_exc())
    finally:
        report['plan'] = rt.current_plan.model_dump()
        rt.mode = 'manual'
        rt.execution_task = None
        await rt.stop()
        store.close()
        (args.output / 'result.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k: report.get(k) for k in ('outcome', 'error')}), flush=True)
    return report['outcome'] == 'passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=120)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
