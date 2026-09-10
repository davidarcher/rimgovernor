"""Observe ordinary bed construction, guarded assignment and actual sleeping use."""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimgovernor.bridge import BridgeError
from rimgovernor.bridge_observation import observe
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.colony_plan import ColonyGoal, CommitSteps
from rimgovernor.colony_upkeep import upkeep_nodes
from rimgovernor.headless import isolated_root, prepare
from rimgovernor.sleeping_upkeep import sleeping_method
from rimgovernor.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare(root)
    store = Store(args.output / 'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', samples=[], actions=[],
        scope='Fixture-prepared warm room and controller-owned floor spot. Ordinary new bed construction and sleeping; no sustained survival claim.')

    async def sample(pawn):
        facts = await rt.game.query('home/colony_facts', planning=True)
        facts['upkeep_context'] = rt.context_token
        report['samples'].append(facts)
        assert not facts['upkeep']['errors'], facts['upkeep']['errors']
        upkeep_nodes(facts, rt.current_plan.control)
        state = rt.current_plan.control['upkeep']['MaintainSleeping']
        assert state['known'], state
        # Exercise the declared fixture pawn; retain the complete native census.
        state['targets'] = [r for r in state['targets'] if r['id'] == pawn]
        return facts

    async def execute(compiled, facts):
        assert compiled is not None
        method, actions = compiled
        steps, costs = rt.controller.skills.steps('MaintainSleeping', method, actions, facts)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Native sleeping upkeep acceptance', steps=steps).decision(rt.current_plan),
            actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
        goal = rt.current_plan.colony_goals['MaintainSleeping']
        goal.steps.extend(s.id for s in steps)
        goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
        rt.current_plan.control.setdefault('costs', {}).update(costs)
        rt.mode = 'automate'
        await rt.hands.advance(rt)
        rt.mode = 'manual'
        report['actions'].append(dict(method=method, steps=[s.model_dump() for s in steps]))
        return rt.current_plan.progress[steps[-1].id]

    async def advance():
        status = await rt.game.query('home/status', colonists=False, threats=True)
        assert status['threats'].get('hostileCount') == 0 and status['threats'].get('huntingPredatorCount') == 0
        await rt.bridge.call('rimworld/set_time_speed', speed='Superfast', ultraSpeedBoost=False)
        await asyncio.sleep(.5)
        await rt.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
        rt.batch = await observe(rt.game)
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()

    try:
        report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config, {'model': 'no inference'})
        await ready(rt)
        rt.execution_task = asyncio.current_task()
        setup = (await rt.bridge.call('test/sleeping_setup')).structuredContent
        report['setup'] = setup
        assert setup['success'], setup
        rt.current_plan.colony_goals['MaintainSleeping'] = ColonyGoal(priority_class=3)
        facts = await sample(setup['pawn'])
        floor = await execute(('fixture-floor', [dict(kind='place_buildings', placements=[
            dict(def_name='SleepingSpot', x=setup['x'], z=setup['z'])])]), facts)
        rt.batch = await observe(rt.game)
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()
        assert floor.state == 'complete', floor
        report['floor_creation'] = floor.model_dump()
        setup['floor'] = floor.issued['0']['placed_thing_id']
        assert setup['floor'], 'Native placement identity missing'
        report['floor_assignment_setup'] = (await rt.bridge.call('home/building_config', thing=setup['floor'],
            owner=setup['pawn'].removeprefix('Thing_'), dryRun=False, watch=False)).structuredContent
        assert report['floor_assignment_setup']['success'], report['floor_assignment_setup']
        assert report['floor_assignment_setup']['refusedCount'] == 0, report['floor_assignment_setup']
        facts = await sample(setup['pawn'])
        assert setup['pawn'] in next(b for b in facts['upkeep']['beds'] if b['id'] == setup['floor'])['owners']
        compiled = await sleeping_method(rt, facts)
        assert compiled and compiled[1][0]['kind'] == 'place_buildings', compiled
        progress = await execute(compiled, facts)
        assert progress.state == 'waiting', progress
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline and progress.state == 'waiting':
            await advance()
        assert progress.state == 'complete', progress
        report['construction'] = progress.model_dump()
        print('Bed construction: native completed building observed', flush=True)
        facts = await sample(setup['pawn'])
        old = next(b for b in facts['upkeep']['beds'] if b['id'] == setup['floor'])
        assert setup['pawn'] in old['owners'], old
        compiled = await sleeping_method(rt, facts)
        assert compiled and compiled[1][0]['tool'] == 'home/upkeep_bed', compiled
        assignment = compiled[1][0]['arguments']
        assigned = await execute(compiled, facts)
        assert assigned.state == 'complete', assigned
        report['assignment'] = assigned.model_dump()
        try:
            stale = (await rt.bridge.call('home/upkeep_bed', **assignment)).structuredContent
        except BridgeError as error:
            stale = error.result.structuredContent
        assert stale and stale.get('success') is False, stale
        report['stale_assignment_refusal'] = stale
        await rt.bridge.call('test/sleeping_need', pawn=setup['pawn'])
        facts = await sample(setup['pawn'])
        assert rt.current_plan.control['upkeep']['MaintainSleeping']['targets'], 'Assignment alone certified use'
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            await advance()
            facts = await sample(setup['pawn'])
            if not rt.current_plan.control['upkeep']['MaintainSleeping']['targets']:
                break
        assert not rt.current_plan.control['upkeep']['MaintainSleeping']['targets'], 'Sleeping use not observed'
        assert any(b['id'] == setup['floor'] for b in facts['upkeep']['beds']), 'Floor capacity removed'
        target = next(b for b in facts['upkeep']['beds'] if b['id'] == assignment['bed'])
        assert setup['pawn'] in target['owners'] and setup['pawn'] in target['users'], target
        report['sleeping_use'] = target
        assert rt.counters['model_calls'] == 0
        report['outcome'] = 'passed'
        print('Sleeping use: assigned pawn observed in completed bed; floor retained', flush=True)
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
    parser.add_argument('--seconds', type=int, default=240)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
