"""Read native forecasts and observe power/rot progression in a disposable Docker fixture."""
import asyncio
import json
import os
from pathlib import Path
from types import SimpleNamespace

from session_checkpoint_acceptance import ready
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.native_forecasts import forecasts
from rimgovernor.store import Store
from rimgovernor.strategic_state import StrategicState


async def run():
    root = Path(os.environ['RIMGOVERNOR_BRIDGE_ROOT'])
    report = {'passed': False, 'cases': [], 'samples': [],
              'scope': 'Disposable fixture setup; ordinary native power charging, discharge and rot. No construction, pawn feeding or long-term survival acceptance.'}
    rt = BridgeRuntime(Store(root/'forecast.sqlite'), root, fresh=True, headless=True)
    state = StrategicState()

    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        print(name+': '+str(bool(passed)), flush=True)
        assert passed, name

    async def sample(label):
        facts = await rt.game.query('home/colony_facts')
        buildings = await rt.game.query('home/list_buildings', playerOnly=True)
        value = forecasts(facts, buildings)
        report['samples'].append(dict(label=label, facts=facts, buildings=buildings, forecasts=value))
        summary = rt.batch.summary.model_copy(update={'end_tick': facts['tick']})
        state.update(SimpleNamespace(summary=summary, native={'buildings': buildings}))
        return facts, buildings, value

    async def advance(ticks):
        if rt.review_task and not rt.review_task.done():
            await rt.review_task
        clock = await advance_game(rt, ticks, report, timeout=45)
        rt.supervisor.absorb(clock)
        rt.clock_events.extend(await rt.supervisor.poll())
        rt.receive_clock_events()
        if rt.review_task and not rt.review_task.done():
            await rt.review_task

    try:
        await ready(rt)
        report['setup'] = (await rt.bridge.call('test/forecast_setup')).structuredContent
        record('fixture_setup', report['setup'].get('success') is True, setup=report['setup'])
        before, _, first = await sample('setup')
        record('native_inputs_readable', first['food']['readable'] and first['animalFeed']['readable']
               and all(p['mood'] is not None and p['minorBreakThreshold'] is not None for p in first['people']))
        record('animal_demand_observed', any(p['id'] == report['setup']['animal'] and p['nutritionPerDay'] > 0
               for p in first['animalFeed']['consumers']))
        record('native_crop_work_observed', first['labor']['sowWork'] > 0 and first['labor']['harvestWork'] > 0)
        rice = next(s for s in before['foodSupply']['stocks'] if s['id'] == report['setup']['food'])
        record('native_rot_deadline_observed', rice['perishable'] is True and 0 < rice['rotTicks'] < 600, stock=rice)
        await advance(120)
        _, low_buildings, low = await sample('discharging')
        record('native_power_aggregate_readable', low_buildings['powerSummary']['readable'] is True
               and all(n['net_w'] is not None for n in low['power']))
        record('native_low_reserve_signal', state.latches.get('low_power') is True, power=low['power'])
        state.decided()
        state.update(SimpleNamespace(summary=rt.batch.summary, native={'buildings': {
            'powerNets': [], 'powerSummary': {'readable': False}}}))
        state = StrategicState(state.dump())
        record('unknown_preserves_persisted_risk', state.latches['low_power'] is True
               and not any(e['kind'] == 'power.reserve_recovered' for e in state.pending))
        report['refuel'] = (await rt.bridge.call('test/forecast_refuel')).structuredContent
        await advance(600)
        after, recovered_buildings, recovered = await sample('charging')
        record('live_reserve_recovery', state.latches.get('low_power') is False
               and any(e['kind'] == 'power.reserve_recovered' for e in state.pending)
               and sum(n['stored_wd'] or 0 for n in recovered['power']) > sum(n['stored_wd'] or 0 for n in low['power']),
               power=recovered['power'])
        rot_state = (await rt.bridge.call('test/forecast_state')).structuredContent
        record('ordinary_native_rot_removes_food', rot_state['food'] == rice['id']
               and rot_state['rotStage'] == 'Rotting'
               and not any(s['id'] == rice['id'] for s in after['foodSupply']['stocks']), native=rot_state)
        record('read_only_forecasts_leave_manual', rt.mode == 'manual' and not rt.current_plan.spec.steps)
        report['passed'] = True
    except BaseException as error:
        report['error'] = repr(error)
        raise
    finally:
        await rt.stop()
        report['stopped'] = True
        (root/'forecast-result.json').write_text(json.dumps(report, indent=2), encoding='utf8')


if __name__ == '__main__':
    asyncio.run(run())
