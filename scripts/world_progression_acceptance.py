"""Observe ordinary caravan work in a disposable, pre-staged Docker worker."""
import asyncio
import json
import os
import time
from pathlib import Path

from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.world_progression import caravan_outcome, survival_assessment
from rimbot.player_commands import apply_command
from session_checkpoint_acceptance import ready
from deterministic_foothold import NoInference


async def run():
    root = Path(os.environ['RIMBOT_BRIDGE_ROOT'])
    output = root.parent / 'world-progression'
    output.mkdir(exist_ok=False)
    store = Store(output / 'state.sqlite')
    NoInference.attempts = 0
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = {'passed': False, 'scope': 'Native world-progression audit', 'cases': {}}
    shared = os.environ.get('RIMBOT_SHARED_WORLD') == '1'
    async def command(*, waits=True, **payload):
        async with rt.lock:
            await rt.refresh_clock_events()
        async with asyncio.timeout(45):
            while rt.wake.is_set() or (rt.review_task and not rt.review_task.done()) or rt.handled_revision < rt.chat_revision:
                await asyncio.sleep(.1)
        result = await apply_command(rt, payload, token=rt.context_token, revision=rt.chat_revision)
        await rt.execute_manual_requests()
        progress = rt.current_plan.progress[result['step']]
        assert progress.state == ('waiting' if waits else 'complete'), progress.model_dump()
        report.setdefault('shared_steps', []).append(result['step'])
        return dict(accepted=True, observation=await read('home/world_progression'))
    async def read(name, **args):
        async with rt.lock:
            result = (await rt.bridge.call(name, **args)).structuredContent
        with (output / 'calls.jsonl').open('a') as stream:
            stream.write(json.dumps(dict(tool=name, arguments=args, result=result)) + '\n')
        (output / (name.replace('/', '-') + '.json')).write_text(json.dumps(result, indent=2))
        return result
    async def window(ticks=600):
        async with rt.lock:
            await rt.refresh_clock_events()
        async with asyncio.timeout(45):
            while rt.wake.is_set() or (rt.review_task and not rt.review_task.done()) or rt.handled_revision < rt.chat_revision:
                await asyncio.sleep(.1)
        async with rt.lock:
            await rt.supervisor.change('Superfast', max_ticks=ticks)
        async with asyncio.timeout(45):
            while True:
                async with rt.lock:
                    state = await rt.supervisor.call(op='status')
                if not state['active']:
                    break
                await asyncio.sleep(.1)
        if state['stopReason'] == 'force_paused':
            facts = await read('home/colony_facts', planning=True)
            assert facts.get('colonyNaming') and not report.get('bootstrap_names'), state
            goal = rt.current_plan.colony_goals.setdefault('ConfirmColonyNames', ColonyGoal(priority_class=0, source='PLAYER'))
            method, actions = await rt.controller.skills.compile('ConfirmColonyNames', facts, [])
            steps, _ = rt.controller.skills.steps('ConfirmColonyNames', method, actions, facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Confirm ordinary generated colony names in disposable survival run', steps=steps).decision(rt.current_plan),
                actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
            rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
            await rt.execute_manual_requests()
            assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps)
            goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
            report['bootstrap_names'] = facts['colonyNaming']
            rt.supervisor.absorb(state)
            rt.supervisor.allow_resume()
        elif state['stopReason'] == 'letter_pause' and 'Ancient danger (ThreatBig,' in state.get('stopDetail', ''):
            danger = await read('home/status', colonists=False, threats=True)
            letters = await read('rimworld/list_letters')
            report.setdefault('interruptions', []).append(dict(clock=state, danger=danger, letters=letters))
            assert state['pauseVerified'] and danger['counts']['hostileCount'] == 0 and danger['counts']['huntingPredatorCount'] == 0
            assert len(report['interruptions']) == 1, 'Unexpected repeated Ancient danger warning'
            rt.supervisor.absorb(state)
            rt.supervisor.allow_resume()
        elif state['stopReason'] == 'requested_pause':
            danger = await read('home/status', colonists=False, threats=True)
            assert state['owner'] == rt.supervisor.owner and state['pauseVerified'] and not rt.supervisor.hold
            assert danger['counts']['hostileCount'] == 0 and danger['counts']['huntingPredatorCount'] == 0
            report.setdefault('owner_review_pauses', []).append(state)
        else:
            assert state['pauseVerified'] and state['stopReason'] == 'tick_budget', state
        if shared:
            from rimbot.world_progression import reconcile_world
            async with rt.lock:
                await reconcile_world(rt)
                rt.persist()
        return state
    try:
        await ready(rt)
        report['world'] = await read('home/world_progression')
        report['catalog'] = await read('home/caravan')
        report['facts'] = await read('home/colony_facts', planning=True)
        report['initial_readiness'] = survival_assessment(report['facts'])
        assert report['world']['complete'] is True
        assert report['catalog']['accepted'] is True
        report['cases']['observations'] = 'passed'
        if os.environ.get('RIMBOT_CARAVAN_TRIP') == '1':
            observed = report['facts']
            supply_cells = observed.get('forbiddenSupplies', [])
            while observed.get('forbiddenSupplies'):
                goal = rt.current_plan.colony_goals.setdefault('AllowStartingSupplies', ColonyGoal(priority_class=2, source='PLAYER'))
                method, actions = await rt.controller.skills.compile('AllowStartingSupplies', observed, [])
                steps, _ = rt.controller.skills.steps('AllowStartingSupplies', method, actions, observed)
                assert steps, 'No starting supply permission method'
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Allow ordinary starting supplies for caravan acceptance', steps=steps).decision(rt.current_plan),
                    actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
                rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
                await rt.execute_manual_requests()
                assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps)
                goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
                observed = await read('home/colony_facts', planning=True)
            assert supply_cells, 'Baseline must expose ordinary starting supply cells'
            zone_args = dict(op='create', zoneType='stockpile', label='Expedition supplies',
                cells=';'.join(f"{c['x']},{c['z']}" for c in supply_cells),
                allowSplit=True, preset='everything', watch=False)
            preview = await read('home/zone_cells', **zone_args, dryRun=True)
            assert preview.get('success') is True
            report['supply_stockpile'] = await read('home/zone_cells', **zone_args, dryRun=False)
            catalog = await read('home/caravan')
            roster = await read('home/list_pawns', colonistsOnly=True, bio=True)
            pawn = roster['pawns'][0]['thingId']
            food = next(g for g in catalog['groups'] if g['defName'] == 'Pemmican')
            report['formation'] = None
            scope = await read('home/colony_identity')
            scope_args = {key: scope[key] for key in ('colonyId', 'loadToken', 'mapId')}
            for tile in catalog['neighbors']:
                arguments = dict(scope_args, action='form', pawnIds=pawn, cargoIds=food['id'], counts='60', destination=tile)
                preview = await read('home/caravan', **arguments, dryRun=True)
                if preview.get('accepted') is True:
                    if os.environ.get('RIMBOT_WORLD_MATRIX') == '1':
                        request = dict(kind='FormCaravan', pawn_ids=[pawn], cargo=[dict(group_id=food['id'], count=60)], destination=tile)
                        await command(kind='SetResourceReserve', resource='Pemmican', reserve=food['available'], waits=False)
                        before = len(rt.current_plan.spec.steps)
                        try:
                            await apply_command(rt, request, token=rt.context_token, revision=rt.chat_revision)
                        except ValueError as error:
                            assert 'reservation' in str(error).lower(), error
                            report['reserve_refusal'] = str(error)
                        else:
                            raise AssertionError('Cargo bypassed the player reserve')
                        assert len(rt.current_plan.spec.steps) == before
                        await command(kind='SetResourceReserve', resource='Pemmican', reserve=0, waits=False)
                        first = dict(request, cargo=[dict(group_id=food['id'], count=food['available'] * 3 // 4)])
                        pending = await apply_command(rt, first, token=rt.context_token, revision=rt.chat_revision)
                        second = dict(request, pawn_ids=[roster['pawns'][1]['thingId']],
                            cargo=[dict(group_id=food['id'], count=food['available'] // 2)])
                        try:
                            await apply_command(rt, second, token=rt.context_token, revision=rt.chat_revision)
                        except ValueError as error:
                            assert 'reservation' in str(error).lower(), error
                            report['competition_refusal'] = str(error)
                        else:
                            raise AssertionError('Competing manifests overcommitted native cargo')
                        assert not (await read('home/world_progression'))['assemblies']
                        await rt.cancel_plan_step(pending['step'])
                        report['cases']['resource_competition'] = 'passed'
                    report['formation'] = (await command(kind='FormCaravan', pawn_ids=[pawn],
                        cargo=[dict(group_id=food['id'], count=60)], destination=tile)) if shared else (
                        await read('home/caravan', **arguments, dryRun=False))
                    break
            assert report['formation'] and report['formation']['accepted'], report['formation']
            assert report['formation']['observation']['assemblies'], 'No native assembly lord'
            report['samples'] = []
            deadline = time.monotonic() + 600
            while time.monotonic() < deadline:
                await window()
                world = await read('home/world_progression')
                report['samples'].append(world)
                (output / 'progress.json').write_text(json.dumps(report, indent=2))
                caravan = next((c for c in world['caravans'] if pawn in {p['thingId'] for p in c['pawns']}), None)
                if caravan:
                    report['departed'] = caravan
                    assert any(i['defName'] == 'Pemmican' and i['count'] > 0 for p in caravan['pawns'] for i in p['inventory'])
                    outcome = caravan_outcome(world, scope=scope_args, pawn_ids=[pawn],
                        destination=tile, issued_tick=scope['tick'])
                    if outcome['state'] == 'arrived':
                        report['arrived'] = caravan
                        break
            assert report.get('arrived'), 'Ordinary caravan arrival not observed within wall bound'
            caravan_id = report['arrived']['id']
            next_tile = next(t for t in catalog['neighbors'] if t != tile)
            move_args = dict(scope_args, action='move', caravanId=caravan_id, destination=next_tile)
            preview = await read('home/caravan', **move_args, dryRun=True)
            assert preview.get('accepted') is True, preview
            report['movement_order'] = (await command(kind='RouteCaravan', caravan_id=caravan_id,
                destination=next_tile)) if shared else await read('home/caravan', **move_args, dryRun=False)
            assert report['movement_order']['accepted'] is True
            deadline = time.monotonic() + 600
            while time.monotonic() < deadline:
                await window()
                world = await read('home/world_progression')
                report['samples'].append(world)
                (output / 'progress.json').write_text(json.dumps(report, indent=2))
                outcome = caravan_outcome(world, scope=scope_args, pawn_ids=[pawn],
                    destination=next_tile, issued_tick=scope['tick'], caravan_id=caravan_id)
                assert outcome['state'] not in ('invalidated', 'blocked'), outcome
                if outcome['state'] == 'arrived':
                    report['moved'] = world
                    break
            assert report.get('moved'), 'World tile movement was not observed'
            return_args = dict(scope_args, action='return', caravanId=caravan_id)
            report['return_order'] = (await command(kind='RouteCaravan', caravan_id=caravan_id,
                return_home=True)) if shared else await read('home/caravan', **return_args, dryRun=False)
            assert report['return_order']['accepted'] is True
            deadline = time.monotonic() + 600
            while time.monotonic() < deadline:
                await window()
                world = await read('home/world_progression')
                report['samples'].append(world)
                if not any(c['id'] == caravan_id for c in world['caravans']):
                    home = await read('home/list_pawns', colonistsOnly=True)
                    if any(p['thingId'] == pawn and p.get('dead') is False for p in home['pawns']):
                        report['returned'] = home
                        break
            assert report.get('returned'), 'Living returning colonist not observed on home map'
            if shared:
                report['plan'] = rt.current_plan.model_dump(mode='json')
                assert all(rt.current_plan.progress[s].state == 'complete' for s in report['shared_steps'])
            report['cases']['caravan_round_trip'] = 'passed'
        if os.environ.get('RIMBOT_QUEST_PROBE') == '1':
            preview = await read('test/join_incident', dryRun=True)
            assert preview['eligible'], 'Ordinary join incident is ineligible in this scenario'
            report['quest_incident'] = await read('test/join_incident', dryRun=False)
            assert report['quest_incident']['applied'] and len(report['quest_incident']['joined']) == 1
            assert any(q['state'] == 'EndedSuccess' for q in report['quest_incident']['questStates']), 'Native join quest did not succeed'
            roster = await read('home/list_pawns', colonistsOnly=True)
            assert set(report['quest_incident']['joined']) <= {p['thingId'] for p in roster['pawns'] if p.get('dead') is False}
            offer = await read('test/trade_quest_offer', dryRun=True)
            assert offer['eligible'], 'Ordinary native trade quest cannot be generated in this scenario'
            report['quest_offer'] = await read('test/trade_quest_offer', dryRun=False)
            world = await read('home/world_progression')
            quest = next(q for q in world['quests'] if q['id'] == report['quest_offer']['questId'])
            assert quest['state'] == 'NotYetAccepted' and quest['canAccept'] is True
            report['quest_acceptance'] = await command(kind='AcceptQuest', waits=False,
                quest_id=quest['id'], pawn_id=quest['eligiblePawns'][0],
                reward_choice=quest['rewardChoices'][0]['index'] if quest['rewardChoices'] else -1)
            accepted = next(q for q in report['quest_acceptance']['observation']['quests'] if q['id'] == quest['id'])
            assert accepted['state'] == 'Ongoing' and accepted['acceptedTick'] >= 0
            report['cases']['native_quest_progression'] = 'passed'
        days = int(os.environ.get('RIMBOT_SURVIVAL_DAYS', '0'))
        if days:
            baseline = await read('home/list_pawns', colonistsOnly=True)
            expected = {p['thingId'] for p in baseline['pawns']}
            start = await read('home/world_progression')
            report['survival'] = dict(start_tick=start['ticksGame'], required_days=days, samples=[])
            await rt.set_mode('automate')
            deadline = time.monotonic() + 1800
            while True:
                world = await read('home/world_progression')
                assert time.monotonic() < deadline, 'Survival window did not complete within its wall bound'
                if world['ticksGame'] - start['ticksGame'] < days * 60000:
                    await asyncio.sleep(5)
                roster = await read('home/list_pawns', colonistsOnly=True, includeDead=True, health=True)
                world = await read('home/world_progression')
                assert all(world[k] == start[k] for k in ('colonyId', 'loadToken', 'mapId'))
                living = {p['thingId'] for p in roster['pawns'] if p.get('dead') is False}
                assert expected <= living, 'A baseline colonist is dead or absent'
                facts = await read('home/colony_facts', planning=True)
                sample = dict(tick=world['ticksGame'], living=sorted(living), readiness=survival_assessment(facts))
                assert sample['tick'] >= (report['survival']['samples'][-1]['tick']
                    if report['survival']['samples'] else start['ticksGame']), 'Native time rewound'
                report['survival']['samples'].append(sample)
                (output / 'progress.json').write_text(json.dumps(report, indent=2))
                if world['ticksGame'] - start['ticksGame'] >= days * 60000:
                    break
            report['survival']['end_tick'] = world['ticksGame']
            report['survival']['passed'] = True
            report['cases']['multi_day_survival'] = 'passed'
            await rt.set_mode('manual')
        if os.environ.get('RIMBOT_WORLD_MATRIX') == '1':
            preview = await read('test/world_incident', definition='ColdSnap', dryRun=True)
            assert preview['eligible'], 'Native cold snap is unavailable in this scenario'
            report['cold_snap'] = await read('test/world_incident', definition='ColdSnap', dryRun=False)
            assert report['cold_snap']['applied']
            cold_start = (await read('home/world_progression'))['ticksGame']
            while True:
                facts = await read('home/colony_facts', planning=True)
                readiness = survival_assessment(facts)
                report['cold_readiness'] = readiness
                if readiness['checks']['cold_exposure_observed'] is True:
                    break
                assert facts['tick'] - cold_start < 60000, 'No native freezing exposure within a day'
                await window(6000)
            assert readiness['winter_readiness_observed'] is False, 'Bare baseline must not certify winter readiness'
            report['cases']['cold_readiness_refusal'] = 'passed'
            preview = await read('test/world_incident', definition='AnimalInsanitySingle', dryRun=True)
            assert preview['eligible'], 'Native mad-animal incident is unavailable'
            before = await read('home/world_progression')
            report['emergency'] = await read('test/world_incident', definition='AnimalInsanitySingle', dryRun=False)
            assert report['emergency']['applied']
            async with rt.lock:
                try:
                    clock = await rt.supervisor.change('Superfast', max_ticks=600)
                except ValueError as error:
                    clock = await rt.supervisor.call(op='status')
                    report['emergency_refusal'] = str(error)
            assert not clock['active'] and clock['pauseVerified'], clock
            report['emergency_clock'] = clock
            after = await read('home/world_progression')
            assert after['ticksGame'] == before['ticksGame'], 'Unresolved emergency advanced simulation'
            report['cases']['emergency_stop'] = 'passed'
        report['model_calls'] = rt.counters['model_calls']
        report['model_attempts'] = NoInference.attempts
        assert report['model_calls'] == 0 and report['model_attempts'] == 0
        report['passed'] = True
    except Exception as error:
        report['error'] = repr(error)
        if rt.connected:
            try:
                report['failure_world'] = await read('home/world_progression')
                report['failure_plan'] = rt.current_plan.model_dump(mode='json')
            except Exception as inspection_error:
                report['failure_inspection_error'] = repr(inspection_error)
        raise
    finally:
        try:
            await rt.stop()
        finally:
            store.close()
            (output / 'result.json').write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    asyncio.run(run())
