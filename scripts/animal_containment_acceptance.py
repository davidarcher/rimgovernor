"""Verify ordinary fence construction and handler delivery into a native pen."""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.animal_upkeep import containment_method
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.colony_upkeep import upkeep_nodes
from rimbot.headless import isolated_root, prepare
from rimbot.native_scenario import advance_game
from rimbot.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare(root)
    store = Store(args.output / 'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', samples=[], actions=[], scope='Open ground, wood, loose muffalo and pet are declared inputs; ordinary construction and native handling establish containment.')

    async def sample():
        facts = await rt.game.query('home/colony_facts', planning=True)
        upkeep_nodes(facts, rt.current_plan.control)
        state = rt.current_plan.control['upkeep']['MaintainAnimalContainment']
        assert state['known'], state
        state['targets'] = [r for r in state['targets'] if r['id'] == report['animals']['animal']]
        report['samples'].append(dict(tick=facts['tick'], animals=facts['upkeep']['animals']))
        return facts

    async def advance():
        await advance_game(rt, 600, report)
        rt.batch = await observe(rt.game)
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()

    async def execute(goal_id, compiled, facts):
        method, actions = compiled
        steps, costs = rt.controller.skills.steps(goal_id, method, actions, facts)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Native animal containment acceptance', steps=steps).decision(rt.current_plan),
            actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
        goal = rt.current_plan.colony_goals[goal_id]
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
            if any(p.state == 'waiting' for p in states):
                await advance()
        assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps), 'Native work deadline expired'
        report['actions'].append(dict(goal=goal_id, actions=actions, receipts=[rt.current_plan.progress[s.id].model_dump() for s in steps]))
        print(goal_id + ': native action postconditions observed', flush=True)

    try:
        report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config, {'model': 'no inference'})
        await ready(rt)
        rt.execution_task = asyncio.current_task()
        report['ground'] = (await rt.bridge.call('test/storeroom_setup')).structuredContent
        report['animals'] = (await rt.bridge.call('test/containment_setup')).structuredContent
        assert report['ground']['success'] and report['animals']['success']
        for name in ('MaintainAnimalContainment', 'EnsureWorkAssignments'):
            rt.current_plan.colony_goals[name] = ColonyGoal(priority_class=3)
        facts = await sample()
        people = (await rt.game.query('home/list_pawns', colonistsOnly=True, bio=True, work=True, health=True))['pawns']
        work = await rt.controller.skills.compile('EnsureWorkAssignments', facts, people)
        if work:
            await execute('EnsureWorkAssignments', work, facts)
        for expected in ('build_room_shell', 'place_buildings'):
            facts = await sample()
            compiled = await containment_method(rt, facts, people)
            assert compiled and compiled[1][0]['kind'] == expected, compiled
            await execute('MaintainAnimalContainment', compiled, facts)
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            facts = await sample()
            if not rt.current_plan.control['upkeep']['MaintainAnimalContainment']['targets']:
                break
            people = (await rt.game.query('home/list_pawns', colonistsOnly=True, work=True, health=True))['pawns']
            assert await containment_method(rt, facts, people) is None
            await advance()
        animal = next(a for a in facts['upkeep']['animals'] if a['id'] == report['animals']['animal'])
        pet = next(a for a in facts['upkeep']['animals'] if a['id'] == report['animals']['pet'])
        assert animal['contained'] is True and animal['pen'], animal
        assert pet['requiresPen'] is False and not pet['release'] and not pet['slaughter'], pet
        assert not animal['release'] and not animal['slaughter']
        assert rt.counters['model_calls'] == 0
        report.update(outcome='passed', contained=animal, pet=pet)
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
    parser.add_argument('--seconds', type=int, default=240)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
