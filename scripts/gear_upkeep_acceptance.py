"""Verify ordinary apparel work and negative admission cases in an isolated native game."""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_observation import observe
from rimbot.colony_plan import ColonyGoal, CommitSteps, PlanStep
from rimbot.config import ModelRole
from rimbot.gear_upkeep import compile_upkeep, compile_method
from rimbot.headless import isolated_root, prepare
from rimbot.store import Store
from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready


async def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    root = isolated_root(args.source_root, args.output/'bridge')
    prepare(root)
    store = Store(args.output/'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', scope='Scripted native apparel replacement through shared Hands; no model inference', checks={})
    async def fixture(mode):
        value = (await rt.bridge.call('test/gear_fixture', mode=mode)).structuredContent
        assert value.get('success'), value
        return value
    async def inspect():
        return await rt.inspect_native('home/gear_upkeep', dict(dryRun=True))
    try:
        await ready(rt)
        await rt.set_mode('manual')
        setup = await fixture('setup')
        report['setup'] = setup
        observed = await inspect()
        report['initial'] = observed
        pawn = next(p for p in observed['pawns'] if p['pawn'] == setup['pawn'])
        if args.production:
            incompatible = await fixture('incompatible')
            for label, target in [('missing_definition', 'Thing_UnavailableDefinition999999'), ('incompatible_size', incompatible['target'])]:
                try:
                    result = await rt.inspect_native('home/gear_upkeep', dict(pawn=setup['pawn'], target=target,
                        expectedLoadout=pawn['loadout'], dryRun=True))
                except Exception as error:
                    result = getattr(getattr(error, 'result', None), 'structuredContent', None)
                    assert result and any(s in str(error) for s in ('Apparel unavailable', 'Body, age or definition incompatible',
                        'Apparel policy excludes item')), str(error)
                assert result.get('success') is False, result
                report['checks'][label] = result
            from rimbot.production_policy import sync_production_policy
            rt.current_plan.control['resource_policy'] = {'Apparel_BasicShirt': {'reserve': 999}}
            async with rt.lock: await sync_production_policy(rt)
            try:
                result = await rt.inspect_native('home/gear_upkeep', dict(pawn=setup['pawn'], target=setup['target'],
                    expectedLoadout=pawn['loadout'], dryRun=True))
            except Exception as error:
                result = getattr(getattr(error, 'result', None), 'structuredContent', None)
                assert result and 'protected by resource policy' in str(error), str(error)
            assert result.get('success') is False, result
            report['checks']['resource_floor'] = result
            rt.current_plan.control['resource_policy'] = {}
            async with rt.lock: await sync_production_policy(rt)
        candidate = next(c for c in pawn['candidates'] if c['target'] == setup['target'])
        assert candidate['gain'] > 0
        arguments = dict(pawn=setup['pawn'], target=setup['target'], expectedLoadout=pawn['loadout'], dryRun=True)
        for mode in ('force', 'forbid', 'policy'):
            await fixture(mode)
            try:
                refusal = await rt.inspect_native('home/gear_upkeep', arguments)
            except Exception as error:
                refusal = getattr(getattr(error, 'result', None), 'structuredContent', None)
                assert refusal and ('Loadout or apparel assignment changed' in str(error)
                    or 'Item unavailable or forbidden' in str(error)), str(error)
            report['checks'][mode] = refusal
            assert refusal.get('success') is False, refusal
            if mode == 'force': await fixture('unforce')
            elif mode == 'forbid': await fixture('allow')
        setup = await fixture('setup')
        observed = await inspect()
        selected = next(p for p in observed['pawns'] if p['pawn'] == setup['pawn'])
        selected['candidates'] = [c for c in selected['candidates'] if c['target'] == setup['target']]
        _, actions = compile_upkeep(ColonyGoal(priority_class=3), dict(success=True, pawns=[selected]))
        step = PlanStep(id='gear-acceptance', title='Replace damaged shirt', action=actions[0],
                        completion_criteria='Exact replacement shirt observed worn')
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Native apparel acceptance', steps=[step]).decision(rt.current_plan), actor=ModelRole.STRATEGIST,
            expected_token=rt.context_token, expected_revision=rt.chat_revision)
        rt.manual_requests.append((step.id, rt.context_token, rt.chat_revision))
        await rt.execute_manual_requests()
        progress = rt.current_plan.progress[step.id]
        report['issued'] = progress.model_dump()
        assert progress.state == 'waiting', progress
        start_tick = (await rt.game.query('home/status', colonists=False))['time']['ticksGame']
        started = time.monotonic()
        while time.monotonic()-started < args.seconds:
            await rt.bridge.call('rimworld/set_time_speed', speed='Superfast', ultraSpeedBoost=False)
            await asyncio.sleep(.5)
            await rt.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            async with rt.lock:
                rt.batch = await observe(rt.game)
                rt.reconcile_plan()
            progress = rt.current_plan.progress[step.id]
            if progress.state == 'complete': break
            assert progress.state == 'waiting', progress
            if rt.batch.summary.end_tick-start_tick > 6000: raise AssertionError('Dressing exceeded 6000 native ticks')
        else: raise AssertionError('Timed out waiting for actual worn apparel')
        report['completed'] = progress.model_dump()
        report['final'] = await inspect()
        final = next(p for p in report['final']['pawns'] if p['pawn'] == setup['pawn'])
        worn = next(w for w in final['worn'] if w['gear']['thingId'] == setup['target'])
        assert not worn['forced'], worn
        assigned = await fixture('weapon_assigned')
        assigned_state = await inspect()
        assigned_pawn = next(p for p in assigned_state['pawns'] if p['pawn'] == assigned['pawn'])
        assert not any(c['target'] == assigned['target'] for c in assigned_pawn['candidates']), assigned_pawn
        report['checks']['player_weapon_preserved'] = assigned_pawn
        report['weapons'] = []
        modes = ['weapon_setup', 'weapon_damage']
        if args.production: modes.extend(['armor_setup', 'cold_setup', 'heat_setup'])
        for mode in modes:
            setup = await fixture(mode)
            observed = await inspect()
            pawn = next(p for p in observed['pawns'] if p['pawn'] == setup['pawn'])
            pawn['candidates'] = [c for c in pawn['candidates'] if c['target'] == setup['target']]
            assert pawn['candidates'], pawn
            _, actions = compile_upkeep(ColonyGoal(priority_class=3), dict(success=True, pawns=[pawn]))
            step = PlanStep(id=mode, title=mode, action=actions[0], completion_criteria='Exact weapon equipped')
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Native weapon acceptance', steps=[step]).decision(rt.current_plan), actor=ModelRole.STRATEGIST,
                expected_token=rt.context_token, expected_revision=rt.chat_revision)
            rt.manual_requests.append((step.id, rt.context_token, rt.chat_revision))
            await rt.execute_manual_requests()
            assert rt.current_plan.progress[step.id].state == 'waiting', rt.current_plan.progress[step.id]
            started = time.monotonic()
            while time.monotonic()-started < args.seconds:
                await rt.bridge.call('rimworld/set_time_speed', speed='Superfast', ultraSpeedBoost=False)
                await asyncio.sleep(.5)
                await rt.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                async with rt.lock:
                    rt.batch = await observe(rt.game)
                    rt.reconcile_plan()
                progress = rt.current_plan.progress[step.id]
                if progress.state == 'complete': break
                assert progress.state == 'waiting', progress
            else: raise AssertionError('Weapon equip timed out')
            after = await inspect()
            after_pawn = next(p for p in after['pawns'] if p['pawn'] == setup['pawn'])
            if mode == 'cold_setup': assert after_pawn['comfortableMin'] < pawn['comfortableMin'], after_pawn
            if mode == 'heat_setup': assert after_pawn['comfortableMax'] > pawn['comfortableMax'], after_pawn
            if mode == 'armor_setup':
                assert next(w for w in after_pawn['worn'] if w['gear']['thingId'] == setup['target'])['gear']['armorSharp'] > 0
            report['weapons'].append(dict(mode=mode, setup=setup, completed=progress.model_dump(), observed=after))
        if args.production:
            setup = await fixture('setup')
            preparation = await fixture('production_setup')
            facts = await rt.game.query('home/colony_facts', planning=True)
            facts['gearUpkeep']['pawns'] = [p for p in facts['gearUpkeep']['pawns'] if p['pawn'] == setup['pawn']]
            goal = rt.current_plan.colony_goals['MaintainEquipment'] = ColonyGoal(priority_class=3)
            method, actions = await compile_method(rt, facts)
            assert actions[0]['tool'] == 'home/bills', actions
            steps, _ = rt.controller.skills.steps('MaintainEquipment', method, actions, facts)
            for step in steps: step.source = 'PLAYER'
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Native bounded clothing production', steps=steps).decision(rt.current_plan), actor=ModelRole.STRATEGIST,
                expected_token=rt.context_token, expected_revision=rt.chat_revision)
            goal.steps.extend(s.id for s in steps)
            goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
            rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
            await rt.execute_manual_requests()
            assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps), [rt.current_plan.progress[s.id] for s in steps]
            report['production'] = dict(preparation=preparation, actions=actions, initial_cloth=facts['resources'].get('Cloth', 0))
            started = time.monotonic()
            while time.monotonic()-started < args.seconds * 4:
                await rt.bridge.call('rimworld/set_time_speed', speed='Superfast', ultraSpeedBoost=False)
                await asyncio.sleep(2)
                await rt.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                observed = await inspect()
                pawn = next(p for p in observed['pawns'] if p['pawn'] == setup['pawn'])
                (args.output/'production-progress.json').write_text(json.dumps(pawn, indent=2))
                fresh_worn = [w for w in pawn['worn'] if w['gear']['defName'] == 'Apparel_BasicShirt'
                    and w['gear']['thingId'] != setup['damaged'] and w['gear']['hitPoints'] > w['gear']['maxHitPoints'] * .5]
                if fresh_worn:
                    report['production']['worn'] = fresh_worn
                    break
                shirts = [c for c in pawn['candidates'] if c['gear']['defName'] == 'Apparel_BasicShirt']
                if shirts and not report['production'].get('wear_step'):
                    pawn['candidates'] = shirts
                    _, actions = compile_upkeep(goal, dict(success=True, pawns=[pawn]))
                    step = PlanStep(id='produced-shirt-wear', title='Wear produced shirt', action=actions[0], completion_criteria='Produced shirt worn')
                    await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                        reason='Native produced apparel completion', steps=[step]).decision(rt.current_plan), actor=ModelRole.STRATEGIST,
                        expected_token=rt.context_token, expected_revision=rt.chat_revision)
                    rt.manual_requests.append((step.id, rt.context_token, rt.chat_revision))
                    await rt.execute_manual_requests()
                    assert rt.current_plan.progress[step.id].state == 'waiting', rt.current_plan.progress[step.id]
                    report['production']['wear_step'] = step.id
            else: raise AssertionError('Production did not result in a usable worn shirt')
            final_facts = await rt.game.query('home/colony_facts', planning=False)
            report['production']['final_cloth'] = final_facts['resources'].get('Cloth', 0)
            assert report['production']['final_cloth'] < report['production']['initial_cloth']
        report['outcome'] = 'passed'
    except Exception as error:
        report.update(error=str(error), traceback=traceback.format_exc())
    finally:
        try:
            if rt.connected:
                await rt.bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                await rt.bridge.core('games_stop', gameId=rt.bridge.game_id)
        except Exception as error: report['cleanup_error'] = str(error)
        await rt.stop()
        store.close()
        (args.output/'result.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k: report.get(k) for k in ('outcome', 'error')}), flush=True)
    return report['outcome'] == 'passed'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=180)
    parser.add_argument('--production', action='store_true')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
