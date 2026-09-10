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
            setup = (await rt.bridge.call('test/upkeep_setup', fireSize=.1 if field == 'fire' else 0)).structuredContent
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
            state['targets'] = [r for r in state['targets'] if r['id'] == setup[field]]
            assert state['targets'], dict(goal=goal_id, state=state)
            goal = rt.current_plan.colony_goals[goal_id] = ColonyGoal(priority_class=3)
            compiled = await upkeep_method(rt, goal_id, facts, roster['pawns'])
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
