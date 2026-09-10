"""Verify native feed production, animal access and ordinary ingestion."""
import argparse
import asyncio
import json
import time
import traceback
from copy import deepcopy
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimgovernor.animal_feed import feed_method
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.colony_plan import ColonyGoal, CommitSteps
from rimgovernor.colony_upkeep import upkeep_nodes
from rimgovernor.headless import isolated_root, prepare
from rimgovernor.native_scenario import advance_game
from rimgovernor.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare(root)
    store = Store(args.output / 'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', samples=[], actions=[], scope='Declared hungry pet, butcher spot, ingredients outside its allowed area and enabled cooks; no feed or bill supplied.')
    try:
        report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config, {'model': 'no inference'})
        await ready(rt)
        rt.execution_task = asyncio.current_task()
        report['setup'] = (await rt.bridge.call('test/feed_setup')).structuredContent
        assert report['setup']['success'], report['setup']
        goal = rt.current_plan.colony_goals['MaintainAnimalFeed'] = ColonyGoal(priority_class=3)
        deadline = time.monotonic() + args.seconds
        ate = False
        while time.monotonic() < deadline:
            facts = await rt.game.query('home/colony_facts', planning=True)
            upkeep_nodes(facts, rt.current_plan.control)
            state = rt.current_plan.control['upkeep']['MaintainAnimalFeed']
            pet = next(a for a in facts['upkeep']['animals'] if a['id'] == report['setup']['pet'])
            ate |= pet['food'] > report['setup']['food'] + .15
            if not report['samples']:
                assert state['targets'] and pet['reachableStoredFeed'] == []
                assert facts['resources'].get('Kibble', 0) == 0
            report['samples'].append(dict(tick=facts['tick'], state=deepcopy(state), pet=pet,
                feed_definitions=facts['upkeep'].get('feedDefinitions'), kibble=facts['resources'].get('Kibble', 0)))
            assert state['known'], state
            assert pet['release'] is False and pet['slaughter'] is False
            if not state['targets'] and ate:
                assert report['actions'], 'Fixture did not require feed acquisition'
                break
            if state['targets']:
                compiled = await feed_method(rt, facts)
                if compiled:
                    method, actions = compiled
                    steps, _ = rt.controller.skills.steps('MaintainAnimalFeed', method, actions, facts)
                    await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                        reason='Native feed reserve acceptance', steps=steps).decision(rt.current_plan),
                        actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
                    goal.steps.extend(s.id for s in steps)
                    goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
                    for _ in range(len(steps) + 2):
                        rt.handled_revision = rt.chat_revision
                        rt.mode = 'automate'
                        await rt.hands.advance(rt)
                        rt.mode = 'manual'
                        if all(rt.current_plan.progress[s.id].state == 'complete' for s in steps):
                            break
                    assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps), 'Native production action did not complete'
                    report['actions'].append(dict(actions=actions, receipts=[rt.current_plan.progress[s.id].model_dump() for s in steps]))
                    print('Native feed production bill observed', flush=True)
            await advance_game(rt, 600, report)
        assert not state['targets'] and ate, 'Reachable reserve and native ingestion were not both observed'
        report['ate'] = ate
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
    parser.add_argument('--seconds', type=int, default=300)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
