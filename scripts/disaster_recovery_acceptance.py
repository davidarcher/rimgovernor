"""Bounded native condition observation and powerless-cooking fallback acceptance."""
import asyncio
import json
import os
from pathlib import Path

from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.colony_policy import ColonyPolicy
from rimbot.store import Store


async def run():
    root = Path(os.environ['RIMBOT_BRIDGE_ROOT'])
    rt = BridgeRuntime(Store(root/'disaster.sqlite'), root, fresh=True, headless=True)
    report = {'passed': False, 'cases': [], 'samples': [],
              'scope': 'Test-only environmental/stove setup; native condition expiry and ordinary campfire construction. No sustained disaster survival claim.'}

    def save():
        (root/'disaster-result.json').write_text(json.dumps(report, indent=2), encoding='utf8')

    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        save()
        print(name+': '+str(bool(passed)), flush=True)
        assert passed, name

    async def settle():
        async with asyncio.timeout(60):
            while rt.wake.is_set() or rt.deliberating or (rt.review_task and not rt.review_task.done()):
                await asyncio.sleep(.1)

    async def sample(label):
        value = await rt.game.query('home/colony_facts', planning=True)
        report['samples'].append(dict(label=label, facts=value))
        save()
        return value

    async def compile_goal(identity):
        await settle()
        value = await sample(identity)
        people = (await rt.game.query('home/list_pawns', colonistsOnly=True, bio=True, work=True, health=True))['pawns']
        goal = rt.current_plan.colony_goals.setdefault(identity, ColonyGoal(priority_class=2, source='PLAYER'))
        selected = await rt.controller.skills.compile(identity, value, people)
        assert selected, identity
        method, actions = selected
        steps, _ = rt.controller.skills.steps(identity, method, actions, value)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Bounded disaster native acceptance', steps=steps).decision(rt.current_plan),
            actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
        rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
        for _ in steps:
            await rt.execute_manual_requests()
        goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
        return actions, steps

    async def advance():
        await settle()
        await rt.supervisor.change('Superfast', max_ticks=600)
        async with asyncio.timeout(60):
            while True:
                clock = (await rt.bridge.call('home/supervised_play', op='status')).structuredContent
                if not clock['active']:
                    break
                await asyncio.sleep(.15)
        record('native_tick_window', clock['pauseVerified'] and clock['lastTick'] > clock['startTick']
               and clock['stopReason'] in ('tick_budget', 'requested_pause'), clock=clock)
        rt.supervisor.absorb(clock)
        rt.clock_events.extend(await rt.supervisor.poll())
        rt.receive_clock_events()
        await settle()

    try:
        await ready(rt)
        await settle()
        for _ in range(10):
            if not (await sample('starting-supplies')).get('forbiddenSupplies'):
                break
            await compile_goal('AllowStartingSupplies')
        report['setup'] = (await rt.bridge.call('test/disaster_setup')).structuredContent
        before = await sample('fixture')
        conditions = {c['defName']: c for c in before['environment']['conditions']}
        record('compound_native_conditions', {'SolarFlare', 'Eclipse'} <= conditions.keys(), conditions=conditions)
        record('permanent_duration_guard', conditions['Eclipse']['permanent']
               and conditions['Eclipse']['ticksLeft'] is None)
        record('powerless_stove_observed', any(b['id'].removeprefix('Thing_') == report['setup']['stove']
               and not b['usable'] for b in before['cooking']))
        await compile_goal('EnsureWorkAssignments')
        if before['resources'].get('WoodLog', 0) < 30:
            rt.controller.policy = ColonyPolicy(wood_min=1, wood_target=30)
            await compile_goal('MaintainWood')
            for _ in range(20):
                await advance()
                stocked = await sample('wood-acquisition')
                if stocked['resources'].get('WoodLog', 0) >= 30:
                    break
            else:
                raise AssertionError('Ordinary wood acquisition did not supply the fallback')
            rt.controller.policy = ColonyPolicy()
        construction_stock = await sample('construction-stock')
        actions, steps = await compile_goal('EnsureCooking')
        placements = [p for a in actions for p in a.get('placements', [])]
        record('campfire_fallback_selected', len(placements) == 1 and placements[0]['def_name'] == 'Campfire', actions=actions)
        target = placements[0]
        for index in range(20):
            await advance()
            after = await sample('labor-'+str(index))
            buildings = await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True)
            completed = [b for b in buildings.get('buildings', []) if b.get('defName') == 'Campfire'
                         and b.get('position') == {'x': target['x'], 'z': target['z']}]
            if completed:
                break
        else:
            raise AssertionError('Campfire did not complete within 12000 native ticks')
        record('native_campfire_completed', bool(completed), buildings=completed,
               progress={s.id: rt.current_plan.progress[s.id].model_dump() for s in steps})
        remaining = {c['defName'] for c in after['environment']['conditions']}
        record('native_condition_expiry', 'SolarFlare' not in remaining and 'Eclipse' in remaining)
        record('construction_stock_consumed', after['resources'].get('WoodLog', 0) < construction_stock['resources'].get('WoodLog', 0),
               before=construction_stock['resources'], after=after['resources'])
        report['passed'] = True
    except BaseException as error:
        report['error'] = repr(error)
        raise
    finally:
        save()
        try:
            await rt.stop()
            report['stopped'] = True
        finally:
            save()


if __name__ == '__main__':
    asyncio.run(run())
