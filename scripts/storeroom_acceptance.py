"""Observe a newly constructed supply room, roof, filtered zone and delivered medicine."""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
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
    report = dict(outcome='failed', samples=[], actions=[], scope='Open ground, wood, exposed medicine and enabled workers are fixture inputs. Room, roof and delivery require ordinary pawn work.')

    async def sample():
        facts = await rt.game.query('home/colony_facts', planning=True)
        report['samples'].append(facts)
        assert not facts['upkeep']['errors'], facts['upkeep']['errors']
        upkeep_nodes(facts, rt.current_plan.control)
        state = rt.current_plan.control['upkeep']['SecureSupplies']
        state['targets'] = [r for r in state['targets'] if r['id'] == report['setup']['medicine']]
        return facts

    async def advance():
        status = await rt.game.query('home/status', colonists=False, threats=True)
        assert status['threats'].get('hostileCount') == 0 and status['threats'].get('huntingPredatorCount') == 0
        await rt.bridge.call('rimworld/set_time_speed', speed='Superfast', ultraSpeedBoost=False)
        await asyncio.sleep(1)
        await rt.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
        rt.batch = await observe(rt.game)
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()

    try:
        report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config, {'model': 'no inference'})
        await ready(rt)
        rt.execution_task = asyncio.current_task()
        report['setup'] = (await rt.bridge.call('test/storeroom_setup')).structuredContent
        assert report['setup']['success'], report['setup']
        goal = rt.current_plan.colony_goals['SecureSupplies'] = ColonyGoal(priority_class=3)
        for expected in ('build_room_shell', 'create_zone', 'native_operation'):
            deadline = time.monotonic() + args.seconds
            compiled = None
            while time.monotonic() < deadline:
                facts = await sample()
                roster = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True, health=True)
                compiled = await upkeep_method(rt, 'SecureSupplies', facts, roster['pawns'])
                if compiled:
                    break
                assert goal.evidence.get('waiting_for_storage_roof'), 'Storage method returned no action without a roof wait'
                await advance()
            assert compiled and compiled[1][0]['kind'] == expected, compiled
            method, actions = compiled
            steps, costs = rt.controller.skills.steps('SecureSupplies', method, actions, facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Native storeroom acceptance', steps=steps).decision(rt.current_plan),
                actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
            goal.steps.extend(s.id for s in steps)
            goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
            rt.current_plan.control.setdefault('costs', {}).update(costs)
            rt.handled_revision = rt.chat_revision
            rt.mode = 'automate'
            await rt.hands.advance(rt)
            rt.mode = 'manual'
            progress = rt.current_plan.progress[steps[-1].id]
            deadline = time.monotonic() + args.seconds
            while progress.state in ('pending', 'executing', 'waiting') and time.monotonic() < deadline:
                await advance()
                facts = await sample()
                reconcile_upkeep(rt, facts)
                if progress.state in ('pending', 'executing'):
                    rt.handled_revision = rt.chat_revision
                    rt.mode = 'automate'
                    await rt.hands.advance(rt)
                    rt.mode = 'manual'
            assert progress.state == 'complete', progress.model_dump()
            report['actions'].append(dict(action=actions, progress=progress.model_dump()))
            if expected == 'create_zone':
                capacity = next(r for r in (await sample())['upkeep']['storageCapacity']
                                if r['item'] == report['setup']['medicine'])
                assert capacity['unreservedCoveredCapacity'] >= 5 and capacity['acceptingCells'] > 0, capacity
                report['filtered_capacity'] = capacity
            print(expected + ': native completion observed', flush=True)
        facts = await sample()
        target = next(r for r in facts['upkeep']['items'] if r['id'] == report['setup']['medicine'])
        assert target['roofed'] and target['inStorage'] and target['count'] == 5, target
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
    parser.add_argument('--seconds', type=int, default=240)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
