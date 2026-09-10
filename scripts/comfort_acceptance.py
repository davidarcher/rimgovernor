"""Verify ordinary construction and use of dining and recreation furniture."""
import argparse
import asyncio
from copy import deepcopy
import json
import time
import traceback
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimgovernor.bridge_observation import observe
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.colony_plan import ColonyGoal, CommitSteps
from rimgovernor.comfort_upkeep import comfort_evidence, comfort_method
from rimgovernor.headless import isolated_root, prepare
from rimgovernor.native_scenario import advance_game
from rimgovernor.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare(root)
    store = Store(args.output / 'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', samples=[], actions=[], scope='Declared warm room, wood, builders and later hunger/joy needs; dining and recreation furniture requires ordinary construction and use.')

    async def sample():
        facts = await rt.game.query('home/colony_facts', planning=True)
        facts['upkeep_context'] = rt.context_token
        facts['comfortUpkeep'] = comfort_evidence(facts, rt.current_plan.control)
        report['samples'].append(dict(tick=facts['tick'], deficits=deepcopy(facts['comfortUpkeep']),
            native=facts['upkeep'].get('comfort')))
        assert facts['comfortUpkeep'] is not None, facts['upkeep'].get('errors')
        return facts

    async def advance(ticks=600):
        await advance_game(rt, ticks, report)
        rt.batch = await observe(rt.game)
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()

    try:
        report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config, {'model': 'no inference'})
        await ready(rt)
        rt.execution_task = asyncio.current_task()
        report['setup'] = (await rt.bridge.call('test/sleeping_setup')).structuredContent
        assert report['setup']['success'], report['setup']
        assert (await rt.bridge.call('test/comfort_inputs', needs=False)).structuredContent['success']
        goal = rt.current_plan.colony_goals['EnsureComfort'] = ColonyGoal(priority_class=4)
        for expected in ('Table1x2c', 'DiningChair', 'HorseshoesPin'):
            facts = await sample()
            method, actions = await comfort_method(rt, facts)
            assert actions[0]['placements'][0]['def_name'] == expected
            steps, costs = rt.controller.skills.steps('EnsureComfort', method, actions, facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Native startup comfort acceptance', steps=steps).decision(rt.current_plan),
                actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
            goal.steps.extend(s.id for s in steps)
            goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
            rt.current_plan.control.setdefault('costs', {}).update(costs)
            deadline = time.monotonic() + args.seconds
            while time.monotonic() < deadline:
                rt.handled_revision = rt.chat_revision
                rt.mode = 'automate'
                await rt.hands.advance(rt)
                rt.mode = 'manual'
                states = [rt.current_plan.progress[s.id] for s in steps]
                assert not any(p.state in ('blocked', 'cancelled') for p in states), [p.model_dump() for p in states]
                if all(p.state == 'complete' for p in states):
                    break
                await advance()
            assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps)
            report['actions'].append(dict(actions=actions, receipts=[rt.current_plan.progress[s.id].model_dump() for s in steps]))
            print(expected + ': native construction observed', flush=True)
        facts = await sample()
        assert all(r['kind'] == 'use' for r in facts['comfortUpkeep'])
        before = (await rt.game.query('home/list_pawns', colonistsOnly=True, schedule=True))['pawns']
        report['schedules_before'] = {p['thingId']: p['schedule'] for p in before}
        assert (await rt.bridge.call('test/comfort_inputs', needs=True)).structuredContent['success']
        deadline = time.monotonic() + args.seconds
        while facts['comfortUpkeep'] and time.monotonic() < deadline:
            assert await comfort_method(rt, facts) is None
            await advance(300)
            facts = await sample()
        assert facts['comfortUpkeep'] == [], 'Native dining and recreation use were not both observed'
        after = (await rt.game.query('home/list_pawns', colonistsOnly=True, schedule=True))['pawns']
        report['schedules_after'] = {p['thingId']: p['schedule'] for p in after}
        # Current assignment can change with the clock; the saved timetable must not.
        for p in after:
            assert p['schedule']['hours'] == report['schedules_before'][p['thingId']]['hours']
        assert rt.counters['model_calls'] == 0
        report['outcome'] = 'passed'
    except Exception as error:
        report.update(error=str(error), traceback=traceback.format_exc())
    finally:
        report['plan'] = rt.current_plan.model_dump()
        rt.mode, rt.execution_task = 'manual', None
        await rt.stop()
        store.close()
        (args.output / 'result.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k: report.get(k) for k in ('outcome', 'error')}), flush=True)
    return report['outcome'] == 'passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=600)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
