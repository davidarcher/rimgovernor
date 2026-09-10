"""Verify ordinary wild medicine harvesting replenishes observed colony reserves."""
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
from rimbot.colony_upkeep import upkeep_nodes
from rimbot.medical_reserves import reserve_method
from rimbot.headless import isolated_root, prepare
from rimbot.store import Store


async def run(args):
    root = isolated_root(args.source_root, args.output / 'bridge')
    config = prepare(root)
    store = Store(args.output / 'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', samples=[], actions=[], scope='Declared medicine shortage, mature wild healroot and skilled enabled plant workers; medicine requires ordinary native harvesting.')
    try:
        report['manifest'] = capture_manifest(Path(__file__).resolve().parents[1], root, config, {'model': 'no inference'})
        await ready(rt)
        rt.execution_task = asyncio.current_task()
        report['setup'] = (await rt.bridge.call('test/medicine_setup')).structuredContent
        assert report['setup']['success'], report['setup']
        goal = rt.current_plan.colony_goals['MaintainMedicalReserves'] = ColonyGoal(priority_class=3)
        deadline = time.monotonic() + args.seconds
        care = (await rt.game.query('home/list_pawns', colonistsOnly=True, health=True))['pawns']
        report['care_before'] = {p['thingId']: p['health']['medicalCare'] for p in care}
        while time.monotonic() < deadline:
            facts = await rt.game.query('home/colony_facts', planning=True)
            upkeep_nodes(facts, rt.current_plan.control)
            state = rt.current_plan.control['upkeep']['MaintainMedicalReserves']
            report['samples'].append(dict(tick=facts['tick'], state=state, resources=facts['resources']))
            assert state['known'], state
            if not state['targets']:
                assert report['actions'], 'Fixture did not require replenishment'
                break
            compiled = await reserve_method(rt, facts)
            if compiled:
                method, actions = compiled
                steps, _ = rt.controller.skills.steps('MaintainMedicalReserves', method, actions, facts)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Native medicine reserve acceptance', steps=steps).decision(rt.current_plan),
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
                    assert all(rt.current_plan.progress[s.id].state in ('pending', 'executing', 'complete') for s in steps), 'Native designation blocked'
                assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps), 'Bounded Hands passes did not issue all designations'
                report['actions'].append(dict(actions=actions, receipts=[rt.current_plan.progress[s.id].model_dump() for s in steps]))
            status = await rt.game.query('home/status', colonists=False, threats=True)
            assert status['threats'].get('hostileCount') == 0 and status['threats'].get('huntingPredatorCount') == 0
            await rt.bridge.call('rimworld/set_time_speed', speed='Superfast', ultraSpeedBoost=False)
            await asyncio.sleep(1)
            await rt.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
        assert not state['targets'], state
        after = (await rt.game.query('home/list_pawns', colonistsOnly=True, health=True))['pawns']
        report['care_after'] = {p['thingId']: p['health']['medicalCare'] for p in after}
        assert report['care_after'] == report['care_before']
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
