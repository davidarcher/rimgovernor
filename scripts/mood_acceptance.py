"""Local Docker mood relief: shared compilation/Hands and actual native need recovery."""
import asyncio
from rimgovernor.native_scenario import advance_game
import argparse
import json
import os
import traceback
from pathlib import Path
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.bridge import BridgeError
from rimgovernor.bridge_observation import observe
from rimgovernor.colony_plan import ColonyGoal, CommitSteps
from rimgovernor.mood_control import assess
from rimgovernor.store import Store
from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready


SCENARIOS = ('joy', 'rest', 'food', 'forced', 'schedule', 'mental', 'stale_job', 'stale_schedule')


async def run(scenarios):
    root = Path(os.environ['RIMGOVERNOR_BRIDGE_ROOT'])
    store = Store(root/'mood.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = {'passed':False, 'cases':[], 'requested_scenarios':scenarios,
        'scope':'Selected scenarios only. Test-only deficit setup; ordinary native need recovery through shared Hands. No model inference.'}

    def save():
        (root/'mood-result.json').write_text(json.dumps(report, indent=2))

    async def people():
        return (await rt.game.query('home/list_pawns', colonistsOnly=True, needs=True, thoughts=True, bio=True, schedule=True))['pawns']

    async def advance():
        rt.mode = 'manual'
        if rt.review_task and not rt.review_task.done(): await rt.review_task
        clock = await advance_game(rt, 600, report, timeout=90)
        rt.supervisor.absorb(clock)
        assert clock['pauseVerified'] and clock['lastTick'] > clock['startTick'], clock
        await rt.refresh_clock_events()
        return clock

    try:
        await ready(rt)
        report['identity'] = rt.identity
        report['schema'] = await rt.game.describe('home/relieve_need')
        for scenario in scenarios:
            case = {'scenario':scenario, 'passed':False, 'samples':[]}
            report['cases'].append(case)
            rt.mode = 'manual'
            case['setup'] = (await rt.bridge.call('test/mood_setup', scenario=scenario)).structuredContent
            rows = await people()
            p = next(p for p in rows if p['thingId'] == case['setup']['pawn'])
            case['before'] = p
            if scenario in ('forced','schedule','mental','stale_job','stale_schedule'):
                args = dict(pawn=p['thingId'], need='joy', expectedJob=p['jobLoadId'],
                            expectedSchedule=p['schedule']['current'], dryRun=False)
                if scenario == 'stale_job': args['expectedJob'] = -999
                if scenario == 'stale_schedule': args['expectedSchedule'] = 'stale'
                if scenario == 'mental': assert p['mentalState'], 'Fixture did not establish an active break'
                expected = {'forced':'Current job changed or player work is protected',
                    'stale_job':'Current job changed or player work is protected',
                    'schedule':'Native need priority or player timetable prevents recovery now',
                    'stale_schedule':'Player timetable changed',
                    'mental':'Pawn unavailable, drafted or in an active mental break'}[scenario]
                try:
                    case['refusal'] = (await rt.bridge.call('home/relieve_need', **args)).structuredContent
                except BridgeError as error:
                    assert expected in str(error), str(error)
                    case['refusal'] = dict(success=False, error=str(error), transport='native tool refusal')
                assert case['refusal']['success'] is False, case
                assert expected in str(case['refusal'].get('error')), case['refusal']
                case['after'] = next(p for p in await people() if p['thingId'] == case['setup']['pawn'])
                assert case['after']['jobLoadId'] == p['jobLoadId'], case
                case['passed'] = True
                save()
                continue
            identity = 'EnsureMood-'+p['thingId']
            rt.current_plan.colony_goals[identity] = ColonyGoal(priority_class=2)
            facts = await rt.game.query('home/colony_facts', planning=True)
            facts['mood'] = assess(rows, [], {})
            name, actions = await rt.controller.skills.compile(identity, facts, rows)
            assert name == scenario, (name, scenario)
            steps, _ = rt.controller.skills.steps(identity, name, actions, facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Mood need recovery acceptance', steps=steps).decision(rt.current_plan), actor='strategist',
                expected_token=rt.context_token, expected_revision=rt.chat_revision)
            rt.batch = await observe(rt.game)
            rt.update_strategy_state()
            rt.strategic_state.decided()
            rt.handled_revision = rt.chat_revision
            rt.execution_task = asyncio.current_task()
            rt.mode = 'automate'
            await rt.hands.advance(rt)
            progress = rt.current_plan.progress[steps[0].id]
            case['issued'] = progress.model_dump()
            assert progress.state == 'waiting', progress.model_dump()
            for _ in range(35):
                clock = await advance()
                row = next(p for p in await people() if p['thingId'] == case['setup']['pawn'])
                case['samples'].append({'clock':clock, 'pawn':row})
                save()
                assert row['jobPlayerForced'] is False, 'Need relief must remain ordinary interruptible work'
                rt.batch = await observe(rt.game)
                rt.reconcile_plan()
                if progress.state == 'complete': break
                assert progress.state == 'waiting', progress.model_dump()
            assert progress.state == 'complete', dict(scenario=scenario, needs=row['needs'], progress=progress.model_dump())
            assert row['needs'][scenario] >= .5, row
            assert row['schedule']['hours'] == p['schedule']['hours'], row
            case.update(passed=True, completed=progress.model_dump())
            print(scenario+': passed', flush=True)
            save()
        assert rt.counters['model_calls'] == 0
        report['passed'] = True
    except Exception as error:
        report.update(error=str(error), traceback=traceback.format_exc())
    finally:
        rt.mode = 'manual'
        rt.execution_task = None
        await rt.stop()
        store.close()
        save()
    print(json.dumps({'passed':report['passed'], 'error':report.get('error')}), flush=True)
    return report['passed']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--scenarios', choices=SCENARIOS, nargs='+', default=list(SCENARIOS))
    raise SystemExit(0 if asyncio.run(run(parser.parse_args().scenarios)) else 1)
