"""Compound native recovery through shared methods, Hands and ordinary pawn work."""
import asyncio
import json
import os
from pathlib import Path

from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.colony_skills import SkillBlocked
from rimbot.store import Store


async def run():
    root = Path(os.environ['RIMBOT_BRIDGE_ROOT'])
    rt = BridgeRuntime(Store(root/'compound.sqlite'), root, fresh=True, headless=True)
    report = dict(passed=False, cases=[], samples=[], methods=[],
                  scope='Disposable native compound damage/crop-loss inputs; ordinary native repairs, refueling, sowing, condition expiry and electrical service. No models or cheats in recovery execution.')

    def save():
        (root/'compound-result.json').write_text(json.dumps(report, indent=2), encoding='utf8')

    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        save()
        print(name+': '+str(bool(passed)), flush=True)
        assert passed, name

    async def settle():
        async with asyncio.timeout(90):
            while rt.wake.is_set() or rt.deliberating or (rt.review_task and not rt.review_task.done()):
                await asyncio.sleep(.1)

    async def sample(label):
        value = await rt.game.query('home/colony_facts', planning=True)
        report['samples'].append(dict(label=label, facts=value))
        save()
        return value

    async def commit(identity, method, actions, facts):
        goal = rt.current_plan.colony_goals.setdefault(identity, ColonyGoal(priority_class=2, source='PLAYER'))
        steps, _ = rt.controller.skills.steps(identity, method, actions, facts)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Compound native recovery acceptance', steps=steps).decision(rt.current_plan),
            actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
        goal = rt.current_plan.colony_goals[identity]
        goal.steps.extend(s.id for s in steps if s.id not in goal.steps)
        rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
        for _ in steps:
            await rt.execute_manual_requests()
        goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
        report['methods'].append(dict(identity=identity, method=method, actions=actions,
                                     progress={s.id: rt.current_plan.progress[s.id].model_dump() for s in steps}))
        save()
        return steps

    async def compile_goal(identity, optional=False):
        await settle()
        facts = await sample(identity)
        people = (await rt.game.query('home/list_pawns', colonistsOnly=True, bio=True, work=True, health=True))['pawns']
        rt.current_plan.colony_goals.setdefault(identity, ColonyGoal(priority_class=2, source='PLAYER'))
        try:
            selected = await rt.controller.skills.compile(identity, facts, people)
        except SkillBlocked as error:
            if not optional:
                raise
            report.setdefault('refusals', []).append(dict(reason=str(error),
                native=rt.current_plan.colony_goals[identity].evidence.get('refusals', [])))
            save()
            return []
        if selected is None:
            if not optional:
                raise AssertionError('No method: '+identity)
            return []
        return await commit(identity, *selected, facts)

    async def advance(ticks=600):
        await settle()
        clock = await advance_game(rt, ticks, report, timeout=180)
        rt.supervisor.absorb(clock)
        rt.clock_events.extend(await rt.supervisor.poll())
        rt.receive_clock_events()
        await settle()

    try:
        await ready(rt)
        await settle()
        for _ in range(10):
            if not (await sample('starting-stock')).get('forbiddenSupplies'):
                break
            await compile_goal('AllowStartingSupplies')
        for _ in range(3):
            await compile_goal('EnsureWorkAssignments', optional=True)
        setup = report['setup'] = (await rt.bridge.call('test/disaster_compound')).structuredContent
        before = await sample('compound-start')
        record('native_damage_and_crop_loss', setup['wallAfter'] < setup['wallBefore'] and setup['cropLoss'] == 36, setup=setup)
        record('native_compound_capabilities', before['recovery']['roofHazard']
               and any(b['thingId'] == setup['stove'] and b['powerOn'] is False for b in before['recovery']['buildings']))
        preview = await rt.inspect_native('home/recover_service', dict(thingId=setup['campfire'], pawn=setup['pawn'], method='refuel', dryRun=True))
        record('inaccessible_fuel_refused', preview.get('accepted') is False, preview=preview)
        from rimbot.colony_skills import native
        await commit('RecoverDisasterServices', 'refuge-acceptance', [native('home/recovery_area', pawn=setup['pawn'], areaId=setup['refuge'])], before)
        restricted = (await rt.game.query('home/recovery_state'))['restrictions']
        record('native_roofed_work_restriction', any(p['pawn'] == setup['pawn'] and p['area'] == setup['refuge'] and p['leased'] for p in restricted))
        other = next(p for p in restricted if p['pawn'] != setup['pawn'])
        await commit('RecoverDisasterServices', 'refuge-expiry', [native('home/recovery_area', pawn=other['pawn'], areaId=setup['refuge'])], before)
        # Explicit simulated player setting, outside the recovery method.
        report['player_override'] = (await rt.bridge.call('home/pawn_config', pawn=setup['pawn'], allowedArea='none', dryRun=False)).structuredContent
        await advance()
        expired = (await rt.game.query('home/recovery_state'))['restrictions']
        record('bounded_restriction_expired', any(p['pawn'] == other['pawn'] and p['area'] == other['area'] and not p['leased'] for p in expired))
        refusal = await rt.inspect_native('home/recovery_area', dict(pawn=setup['pawn'], areaId=setup['refuge'], dryRun=True))
        record('player_override_preserved', refusal.get('accepted') is False, refusal=refusal)
        await advance()
        for _ in range(4):
            if not (await rt.game.query('home/recovery_state'))['roofHazard']:
                break
            await advance()
        record('native_roof_hazard_expired', not (await rt.game.query('home/recovery_state'))['roofHazard'])
        report['released_supplies'] = (await rt.bridge.call('test/disaster_supplies', available=True)).structuredContent
        stock = await sample('permitted-fuel')
        await compile_goal('RecoverDisasterServices', optional=True)
        restored = None
        for index in range(15):
            await advance(6000)
            after = await sample('recovery-'+str(index))
            by_id = {b['thingId']: b for b in after['recovery']['buildings']}
            crops = next(f for f in after['farms'] if f['id'] == setup['zoneId'])
            conditions = after['environment']['conditions']
            if index == 0:
                record('temporary_fallback_during_outage', any(c['defName'] == 'SolarFlare' for c in conditions)
                       and by_id[setup['stove']]['powerOn'] is False, buildings=by_id)
            ready_services = (by_id[setup['wall']]['hitPoints'] == by_id[setup['wall']]['maxHitPoints']
                              and by_id[setup['generator']]['hitPoints'] == by_id[setup['generator']]['maxHitPoints']
                              and by_id[setup['generator']]['fuel'] > 0 and by_id[setup['stove']]['powerOn'] is True
                              and by_id[setup['campfire']]['fuel'] > 0 and crops['plantedCells'] >= setup['cropLoss'])
            if ready_services and not conditions:
                restored = (after, by_id, crops)
                break
            waiting = any(p.state in ('pending', 'executing', 'waiting') for s, p in rt.current_plan.progress.items()
                          if s in rt.current_plan.colony_goals['RecoverDisasterServices'].steps)
            if not waiting:
                await compile_goal('RecoverDisasterServices', optional=True)
        record('native_services_and_production_restored', restored is not None,
               progress={s: p.model_dump() for s, p in rt.current_plan.progress.items()})
        after, buildings, crops = restored
        record('one_day_disruption_observed', after['tick'] - setup['tick'] >= 60000)
        record('native_fuel_consumption', after['resources'].get('WoodLog', 0) < stock['resources'].get('WoodLog', 0),
               before=stock['resources'], after=after['resources'], buildings=buildings, crops=crops)
        record('normal_cooking_service', any(b['id'] == setup['stove'] and b['usable'] for b in after['cooking']))
        record('shared_hands_recovery_dispatched', any(
            any(a.get('tool') == 'home/recover_service' for a in method['actions'])
            and any(p['issued'].get('0', {}).get('confirmed') for p in method['progress'].values())
            for method in report['methods']))
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
