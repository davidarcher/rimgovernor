"""Observe ordinary caravan work in a disposable, pre-staged Docker worker."""
import asyncio
import json
import os
import time
import shutil
import tempfile
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
    # GABS claim files need Linux filesystem rename/locking semantics during Docker runs.
    # Copy all runtime evidence back only after the owned game has stopped.
    if os.name == 'posix':
        from rimbot.headless import isolated_root
        root = isolated_root(root, Path(tempfile.mkdtemp(prefix='rimbot-world-')) / 'run')
    store = Store(output / 'state.sqlite')
    NoInference.attempts = 0
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = {'passed': False, 'scope': 'Native world-progression audit', 'cases': {}}
    shared = os.environ.get('RIMBOT_SHARED_WORLD') == '1'
    diplomacy = os.environ.get('RIMBOT_DIPLOMACY') == '1'
    recovery = os.environ.get('RIMBOT_RECOVERY') == '1'
    quest_trade = os.environ.get('RIMBOT_QUEST_TRADE') == '1'
    settlement_trip = diplomacy or quest_trade
    prepared_days = os.environ.get('RIMBOT_PREPARED_DAYS') == '1'
    logistics = os.environ.get('RIMBOT_LOGISTICS') == '1' or diplomacy
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
            for attempt in range(3):
                try:
                    result = (await rt.bridge.call(name, **args)).structuredContent
                    break
                except Exception as error:
                    # Only replay pure observations after GABS refuses ownership.
                    if (name not in ('home/world_progression', 'home/list_pawns', 'home/colony_facts')
                            or 'Failed to claim runtime ownership' not in str(error) or attempt == 2):
                        raise
                    report.setdefault('ownership_read_retries', []).append(str(error))
                    await asyncio.sleep(.5)
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
        elif state['stopReason'] == 'notification_batch' and state.get('stopDetail', '').startswith('1 new notification(s): Mad '):
            danger = await read('home/status', colonists=True, threats=True)
            hostiles = danger['threats']['hostiles']
            report.setdefault('distant_animal_notifications', []).append(dict(clock=state, danger=danger))
            assert state['pauseVerified'] and danger['counts']['huntingPredatorCount'] == 0
            assert hostiles and all(isinstance(h.get('distanceToNearestColonist'), (int, float))
                and h['distanceToNearestColonist'] > state['hostileWithin'] for h in hostiles), danger
            # Explicit scenario review of a distant notice; native proximity/injury guards remain unchanged.
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
        if settlement_trip or prepared_days or recovery:
            async with rt.lock:
                await rt.refresh_clock_events()
            async with asyncio.timeout(45):
                while rt.wake.is_set() or (rt.review_task and not rt.review_task.done()) or rt.handled_revision < rt.chat_revision:
                    await asyncio.sleep(.1)
            facts = await read('home/colony_facts', planning=True)
            from rimbot.food_forecast import acquisition_targets
            targets, _ = acquisition_targets(facts, rt.controller.policy.food_target_days)
            if targets:
                goal = rt.current_plan.colony_goals.setdefault('EnsureFoodSupply', ColonyGoal(priority_class=1, source='PLAYER'))
                compiled = await rt.controller.skills.compile('EnsureFoodSupply', facts, [])
                if compiled:
                    method, actions = compiled
                    assert method.startswith('acquire-'), 'Expected bounded ordinary food gathering'
                    steps, _ = rt.controller.skills.steps('EnsureFoodSupply', method, actions, facts)
                    await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                        reason='Maintain actual home food during the explicit expedition', steps=steps).decision(rt.current_plan),
                        actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
                    rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
                    await rt.execute_manual_requests()
                    assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps)
                    goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
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
        if os.environ.get('RIMBOT_EXPIRED_QUEST') == '1':
            offer = await read('test/trade_quest_offer', definition='ThreatReward_Raid_Joiner', dryRun=False)
            assert offer['eligible'] and offer['questId']
            initial = await read('home/world_progression')
            quest = next(q for q in initial['quests'] if q['id'] == offer['questId'])
            assert quest['state'] == 'NotYetAccepted' and 0 < quest['expiresInTicks'] <= 30000
            deadline = time.monotonic() + 300
            while time.monotonic() < deadline:
                await window(6000)
                observed = await read('home/world_progression')
                terminal = next(q for q in observed['quests'] if q['id'] == offer['questId'])
                if terminal['state'] == 'EndedOfferExpired':
                    break
            assert terminal['state'] == 'EndedOfferExpired' and terminal['canAccept'] is False
            scope_args = {k: initial[k] for k in ('colonyId', 'loadToken', 'mapId')}
            refused = await read('home/accept_quest', **scope_args, questId=quest['id'], pawnId=quest['eligiblePawns'][0], dryRun=True)
            assert refused['accepted'] is False
            from rimbot.world_progression import quest_outcome
            assert quest_outcome(observed, quest['id'], scope=scope_args, issued_tick=initial['ticksGame']) == 'blocked'
            report['expired_quest'] = terminal
            report['expired_refusal'] = refused
            report['cases']['native_offer_expired_without_acceptance_or_replay'] = 'passed'
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
            if supply_cells:
                zone_args = dict(op='create', zoneType='stockpile', label='Expedition supplies',
                    cells=';'.join(f"{c['x']},{c['z']}" for c in supply_cells),
                    allowSplit=True, preset='everything', watch=False)
                preview = await read('home/zone_cells', **zone_args, dryRun=True)
                assert preview.get('success') is True
                report['supply_stockpile'] = await read('home/zone_cells', **zone_args, dryRun=False)
            else:
                report['supply_stockpile'] = 'Existing native storage in the prepared colony'
            catalog = await read('home/caravan')
            roster = await read('home/list_pawns', colonistsOnly=True, bio=True)
            pawn = roster['pawns'][0]['thingId']
            if settlement_trip:
                negotiators = [(s['level'], p['thingId']) for p in roster['pawns']
                    for s in p.get('bio', {}).get('skills', []) if s['name'] == 'Social' and not s['disabled']]
                assert negotiators, 'No native capable negotiator'
                pawn = max(negotiators)[1]
            if settlement_trip and not supply_cells:
                center = roster['pawns'][0]['position']
                cells = await read('home/get_cells_plus', x=center['x'] - 4, z=center['z'] - 4, width=9, height=9)
                vacant = [c for c in cells['cells'] if c.get('walkable') is True
                    and not c.get('fogged') and not c.get('zoneId') and not c.get('things')]
                assert len(vacant) >= 4, 'Observe available storage cells near the home crew'
                zone_args = dict(op='create', zoneType='stockpile', label='Expedition return storage',
                    cells=';'.join(f"{c['x']},{c['z']}" for c in vacant[:12]),
                    allowSplit=True, preset='everything', watch=False)
                preview = await read('home/zone_cells', **zone_args, dryRun=True)
                assert preview.get('success') is True
                report['return_stockpile'] = await read('home/zone_cells', **zone_args, dryRun=False)
            trade_quest = None
            if quest_trade:
                assert shared
                await window(6000)
                report['offer_evaluation'] = []
                available_stock = (await read('home/colony_facts', planning=True)).get('resources', {})
                for attempt in range(16):
                    offer = await read('test/trade_quest_offer', dryRun=False)
                    if not offer.get('eligible') or not offer.get('questId'):
                        break
                    world = await read('home/world_progression')
                    candidate = next(q for q in world['quests'] if q['id'] == offer['questId'])
                    objective = candidate['tradeRequests'][0]
                    sources = await read('home/resource_sources', resource=objective['resource'])
                    if (objective['resource'] in ('Plasteel', 'MedicineHerbal')
                            and available_stock.get(objective['resource'], 0) + sum(s.get('yield', 0) for s in sources.get('sources', [])) < objective['count']):
                        ore = await read('home/list_things', match='MineablePlasteel' if objective['resource'] == 'Plasteel' else 'Plant_HealrootWild',
                            category='all', ownership='ours', maxPositionsPerDef=40)
                        positions = [pos for row in ore.get('things', []) for pos in row.get('positions', [])]
                        for pos in positions[:3]:
                            vicinity = await read('home/get_cells_plus', x=pos['x'] - 3, z=pos['z'] - 3, width=7, height=7)
                            approach = next((c for c in vicinity['cells'] if c.get('walkable') is True and not c.get('fogged')), None)
                            if not approach:
                                continue
                            preview = await read('home/order', action='goto', pawn=pawn,
                                x=approach['x'], z=approach['z'], watch=False, dryRun=True)
                            if preview.get('success') is not True:
                                continue
                            await command(kind='DraftPawn', pawn=pawn, drafted=True, waits=False)
                            await command(kind='MovePawn', pawn=pawn, x=approach['x'], z=approach['z'])
                            deadline = time.monotonic() + 120
                            while time.monotonic() < deadline:
                                await window(600)
                                people = await read('home/list_pawns', colonistsOnly=True)
                                current = next(p for p in people['pawns'] if p['thingId'] == pawn)
                                if current['position'] == dict(x=approach['x'], z=approach['z']):
                                    break
                            assert current['position'] == dict(x=approach['x'], z=approach['z']), 'Prospector did not reach observed ore approach'
                            await command(kind='DraftPawn', pawn=pawn, drafted=False, waits=False)
                            sources = await read('home/resource_sources', resource=objective['resource'])
                            report.setdefault('prospecting', []).append(dict(ore=pos, approach=approach, sources=sources))
                            if sources.get('sources'):
                                break
                    choice = next((c for c in candidate['rewardChoices']
                        if any(r.get('items') for r in c['rewards'])), None)
                    obtainable = (available_stock.get(objective['resource'], 0)
                        + sum(s.get('yield', 0) for s in sources.get('sources', []))) >= objective['count']
                    report['offer_evaluation'].append(dict(quest=candidate['id'], objective=objective,
                        obtainable=obtainable, item_reward=choice is not None))
                    if obtainable and choice and objective['count'] <= 100:
                        trade_quest = candidate
                        break
                    await window(600)
                assert trade_quest, 'No bounded ordinary obtainable trade offer among the native candidates'
                report['trade_quest'] = trade_quest
                await command(kind='AcceptQuest', quest_id=trade_quest['id'], pawn_id=pawn,
                    reward_choice=choice['index'], waits=False)
                await apply_command(rt, dict(kind='CreateGoal', goal='MaintainResource', resource=objective['resource'],
                    quantity=objective['count']), token=rt.context_token, revision=rt.chat_revision)
                goal_id = 'MaintainResource-' + objective['resource']
                goal = rt.current_plan.colony_goals[goal_id]
                facts = await read('home/colony_facts', planning=True)
                compiled = await rt.controller.skills.compile(goal_id, facts, roster['pawns'])
                if compiled:
                    method, actions = compiled
                    assert actions and all(a.get('tool') == 'home/acquire_resource' for a in actions), 'Scenario requires ordinary obtainable requested resources'
                    source_cells = [(a['arguments']['x'], a['arguments']['z']) for a in actions]
                    for work in goal.evidence['work_types']:
                        skill = next(iter(work.get('skills', [])), None)
                        eligible = [(s['level'], p['thingId']) for p in roster['pawns'] for s in p.get('bio', {}).get('skills', [])
                            if s['name'] == skill and not s['disabled']]
                        assert eligible, work
                        await command(kind='SetWorkPriority', pawn=max(eligible)[1], work_type=work['name'], priority=1, waits=False)
                    steps, _ = rt.controller.skills.steps(goal_id, method, actions, facts)
                    await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                        reason='Acquire native requested goods for the explicit trade expedition', steps=steps).decision(rt.current_plan),
                        actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
                    rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
                    await rt.execute_manual_requests()
                    assert all(rt.current_plan.progress[s.id].state == 'complete' for s in steps)
                    goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
                    deadline = time.monotonic() + 600
                    while time.monotonic() < deadline:
                        await window(6000)
                        stock = await read('home/colony_facts', planning=True)
                        if stock.get('resources', {}).get(objective['resource'], 0) >= objective['count']:
                            break
                    assert stock.get('resources', {}).get(objective['resource'], 0) >= objective['count'], 'Requested goods were not actually produced'
                    report['quest_acquisition'] = dict(resource=objective['resource'], required=objective['count'], observed=stock['resources'][objective['resource']])
                    await read('home/zone_cells', op='create', zoneType='stockpile', label='Quest cargo pickup',
                        cells=';'.join(f'{x},{z}' for x,z in source_cells), allowSplit=True, preset='everything', watch=False, dryRun=False)
                else:
                    report['quest_acquisition'] = dict(resource=objective['resource'], required=objective['count'],
                        observed=facts['resources'][objective['resource']], source='Existing native stock')
                catalog = await read('home/caravan')
            def travel_food(catalog):
                for definition in ('Pemmican', 'MealSurvivalPack', 'MealSimple'):
                    count = ((250 if diplomacy else 200 if quest_trade else 25 if recovery else 60)
                        if definition == 'Pemmican' else (20 if settlement_trip else 6))
                    groups = [g for g in catalog['groups'] if g['defName'] == definition]
                    if sum(g['available'] for g in groups) < count:
                        continue
                    remaining, cargo = count, []
                    for group in groups:
                        take = min(remaining, group['available'])
                        if take:
                            cargo.append(dict(group_id=group['id'], count=take))
                            remaining -= take
                    return dict(groups[0], manifest=cargo)
                return None
            food = travel_food(catalog)
            if food is None and settlement_trip:
                facts = await read('home/colony_facts', planning=True)
                bench = next((b for b in facts.get('cooking', []) if b['usable'] and 'CookMealSimple' in b['recipes']), None)
                assert bench, 'Prepare a colony with an ordinary usable cooking station'
                cooks = [(s['level'], p['thingId']) for p in roster['pawns'] for s in p.get('bio', {}).get('skills', [])
                    if s['name'] == 'Cooking' and not s['disabled']]
                assert cooks
                await command(kind='SetWorkPriority', pawn=max(cooks)[1], work_type='Cooking', priority=1, waits=False)
                await command(kind='CreateBill', bench=bench['id'].removeprefix('Thing_'), recipe='CookMealSimple', target_count=40, waits=False)
                deadline = time.monotonic() + 900
                while time.monotonic() < deadline:
                    await window(6000)
                    catalog = await read('home/caravan')
                    food = travel_food(catalog)
                    if food:
                        break
                report['travel_meal_preparation'] = food
            assert food, 'Prepared native travel food is unavailable'
            manifest = list(food['manifest'])
            if quest_trade:
                goods = next(g for g in catalog['groups'] if g['defName'] == objective['resource'])
                manifest.append(dict(group_id=goods['id'], count=objective['count']))
            if logistics:
                silver = next(g for g in catalog['groups'] if g['defName'] == 'Silver')
                manifest.append(dict(group_id=silver['id'], count=120 if diplomacy else 20))
            report['formation'] = None
            scope = await read('home/colony_identity')
            scope_args = {key: scope[key] for key in ('colonyId', 'loadToken', 'mapId')}
            for tile in catalog['neighbors']:
                arguments = dict(scope_args, action='form', pawnIds=pawn, cargoIds=','.join(c['group_id'] for c in manifest), counts=','.join(str(c['count']) for c in manifest), destination=tile)
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
                        cargo=manifest, destination=tile)) if shared else (
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
                    assert any(i['defName'] == food['defName'] and i['count'] > 0 for p in caravan['pawns'] for i in p['inventory'])
                    outcome = caravan_outcome(world, scope=scope_args, pawn_ids=[pawn],
                        destination=tile, issued_tick=scope['tick'])
                    if outcome['state'] == 'arrived':
                        report['arrived'] = caravan
                        break
            assert report.get('arrived'), 'Ordinary caravan arrival not observed within wall bound'
            caravan_id = report['arrived']['id']
            if logistics:
                assert shared, 'Logistics acceptance requires shared Hands'
                report['hold'] = await command(kind='HoldCaravan', caravan_id=caravan_id, waits=False)
                assert not next(c for c in report['hold']['observation']['caravans'] if c['id'] == caravan_id)['moving']
            next_tile = next(t for t in catalog['neighbors'] if t != tile)
            if settlement_trip:
                settlements = (await read('home/world', settlementRadius=100))['settlements']
                if quest_trade:
                    settlement = next(s for s in settlements if s['tile'] == objective['tile'])
                else:
                    settlement = None
                    report['diplomatic_destinations'] = []
                    for candidate in settlements:
                        if candidate['isPlayer'] or candidate['relation'] == 'Hostile':
                            continue
                        potential = await read('home/caravan', **scope_args, action='visit', caravanId=caravan_id,
                            destination=candidate['tile'], dryRun=True)
                        report['diplomatic_destinations'].append(dict(settlement=candidate, preview=potential))
                        route = potential.get('route', {})
                        if (potential.get('accepted') and route.get('canTrade') is True
                                and route['estimatedTicks'] <= 180000 and route['foodDays'] >= route['estimatedTicks'] / 30000 + .5):
                            settlement = candidate
                            break
                    assert settlement, 'No observed settlement meeting native negotiation and trip limits'
                next_tile = settlement['tile']
            move_args = dict(scope_args, action='visit' if settlement_trip else 'move', caravanId=caravan_id, destination=next_tile)
            preview = await read('home/caravan', **move_args, dryRun=True)
            assert preview.get('accepted') is True, preview
            report['movement_order'] = (await command(kind='RouteCaravan', caravan_id=caravan_id,
                destination=next_tile, visit_settlement=settlement_trip)) if shared else await read('home/caravan', **move_args, dryRun=False)
            assert report['movement_order']['accepted'] is True
            deadline = time.monotonic() + 600
            while time.monotonic() < deadline:
                await window(6000 if settlement_trip else 600)
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
            if quest_trade:
                from rimbot.world_progression import inventory_totals
                before = next(c for c in world['caravans'] if c['id'] == caravan_id)
                baseline_items = inventory_totals([i for p in before['pawns'] for i in p['inventory']])
                report['fulfillment'] = await command(kind='FulfillQuest', quest_id=trade_quest['id'], caravan_id=caravan_id)
                await window(600)
                after = await read('home/world_progression')
                assert next(q for q in after['quests'] if q['id'] == trade_quest['id'])['state'] == 'EndedSuccess'
                party = next(c for c in after['caravans'] if c['id'] == caravan_id)
                actual_items = inventory_totals([i for p in party['pawns'] for i in p['inventory']])
                rewards = [i for r in choice['rewards'] for i in (r.get('items') or [])]
                assert rewards and all(actual_items.get(i['defName'], 0) >= baseline_items.get(i['defName'], 0) + i['count'] for i in rewards)
                report['quest_rewards'] = rewards
                report['cases']['native_trade_quest_fulfilled_and_rewards_received'] = 'passed'
            if diplomacy:
                report['gift'] = await command(kind='GiftToSettlement', caravan_id=caravan_id,
                    faction_id=preview['route']['factionId'], silver=80, waits=False)
                after_faction = next(f for f in report['gift']['observation']['factions'] if f['id'] == preview['route']['factionId'])
                assert after_faction['goodwill'] > preview['route']['goodwill'], 'Native goodwill did not improve'
                report['cases']['native_settlement_visit_and_diplomacy'] = 'passed'
            if recovery:
                from rimbot.expedition_policy import evaluate_world, policy_for
                await command(kind='HoldCaravan', caravan_id=caravan_id, waits=False)
                deadline = time.monotonic() + 900
                while time.monotonic() < deadline:
                    await window(6000)
                    world = await read('home/world_progression')
                    assessment = evaluate_world(policy_for(rt.current_plan), world, await read('home/colony_facts'))
                    party = next(c for c in assessment['caravans'] if c['id'] == caravan_id)
                    assert party['healthy'], 'Ration recovery scenario must retain living mobile crew'
                    if party['recovery_required']:
                        report['short_supplied_party'] = party
                        break
                assert report.get('short_supplied_party'), 'Native ration depletion did not reach the recovery threshold'
            return_args = dict(scope_args, action='return', caravanId=caravan_id)
            report['return_order'] = (await command(kind='RouteCaravan', caravan_id=caravan_id,
                return_home=True, storage_resources=['Silver'] if logistics else [])) if shared else await read('home/caravan', **return_args, dryRun=False)
            assert report['return_order']['accepted'] is True
            deadline = time.monotonic() + 600
            while time.monotonic() < deadline:
                await window(6000 if settlement_trip else 600)
                world = await read('home/world_progression')
                report['samples'].append(world)
                if not any(c['id'] == caravan_id for c in world['caravans']):
                    home = await read('home/list_pawns', colonistsOnly=True)
                    if (any(p['thingId'] == pawn and p.get('dead') is False for p in home['pawns'])
                            and (not logistics or all(rt.current_plan.progress[s].state == 'complete' for s in report['shared_steps']))):
                        report['returned'] = home
                        break
            assert report.get('returned'), 'Living returning colonist not observed on home map'
            if shared:
                report['plan'] = rt.current_plan.model_dump(mode='json')
                assert all(rt.current_plan.progress[s].state == 'complete' for s in report['shared_steps'])
            report['cases']['caravan_round_trip'] = 'passed'
            if recovery:
                report['cases']['native_short_supplied_party_return'] = 'passed'
            if logistics:
                report['cases']['native_return_storage_and_hold'] = 'passed'
        if os.environ.get('RIMBOT_MULTIMAP') == '1':
            assert shared and report.get('returned'), 'Multi-map probe requires a completed shared round trip'
            sites = await read('test/settle_caravan')
            assert sites['maximumSettlements'] >= 2 and sites['candidates'], sites
            catalog = await read('home/caravan')
            food = next(g for g in catalog['groups'] if g['defName'] == 'Pemmican')
            selected = None
            for candidate in sites['candidates']:
                preview = await read('home/caravan', **scope_args, action='form', pawnIds=pawn,
                    cargoIds=food['id'], counts='60', destination=candidate)
                if preview.get('accepted'):
                    selected = candidate
                    break
            assert selected is not None, 'No ordinary reachable settlement candidate'
            await command(kind='FormCaravan', pawn_ids=[pawn], cargo=[dict(group_id=food['id'], count=60)], destination=selected)
            deadline = time.monotonic() + 600
            while time.monotonic() < deadline:
                await window()
                world = await read('home/world_progression')
                party = next((c for c in world['caravans'] if any(p['thingId'] == pawn for p in c['pawns'])), None)
                if party and party['tile'] == selected and not party['moving']:
                    break
            assert party and party['tile'] == selected and not party['moving']
            report['settlement_order'] = await read('test/settle_caravan', caravanId=party['id'], dryRun=False)
            assert report['settlement_order']['accepted']
            async with asyncio.timeout(120):
                while True:
                    world = await read('home/world_progression')
                    homes = [m for m in world['maps'] if m['home']]
                    if len(homes) >= 2 and world['mapId'] != scope_args['mapId']:
                        break
                    await asyncio.sleep(1)
            assert any(m['id'] == scope_args['mapId'] and m['pawns'] for m in homes)
            assert any(m['id'] != scope_args['mapId'] and any(p['thingId'] == pawn for p in m['pawns']) for m in homes)
            stale = await read('home/caravan', **scope_args, action='stop', caravanId=party['id'], dryRun=False)
            assert stale['accepted'] is False and 'changed' in stale['reason']
            report['multiple_maps'] = world
            report['stale_map_refusal'] = stale
            report['cases']['multiple_active_maps_and_scope_invalidation'] = 'passed'
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
            report['survival'] = dict(start_tick=start['ticksGame'], required_days=days, samples=[],
                control='Existing native work and shared food gathering' if prepared_days else 'Full deterministic colony controller')
            rt.current_plan.control.setdefault('policy', {})['execution_speed'] = 'Superfast'
            if not prepared_days:
                await rt.set_mode('automate')
            deadline = time.monotonic() + 1800
            while True:
                world = await read('home/world_progression')
                assert time.monotonic() < deadline, 'Survival window did not complete within its wall bound'
                if world['ticksGame'] - start['ticksGame'] < days * 60000:
                    if prepared_days:
                        await window(6000)
                    else:
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
        if os.environ.get('RIMBOT_WORLD_MATRIX') == '1' or os.environ.get('RIMBOT_EMERGENCY_PROBE') == '1':
            preview = await read('test/world_incident', definition='AnimalInsanitySingle', dryRun=True)
            assert preview['eligible'], 'Native mad-animal incident is unavailable'
            before = await read('home/world_progression')
            report['emergency'] = await read('test/world_incident', definition='AnimalInsanitySingle', dryRun=False)
            assert report['emergency']['applied']
            async with rt.lock:
                try:
                    clock = await rt.supervisor.call(op='start', owner=rt.supervisor.owner,
                        leaseMs=15000, speed='Superfast', mode='colony', hostileWithin=250, maxTicks=600)
                    rt.supervisor.absorb(clock)
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
        try:
            report['native_attention'] = (await rt.bridge.core('games_get_attention', gameId=rt.bridge.game_id)).structuredContent
        except Exception as attention_error:
            report['attention_error'] = repr(attention_error)
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
            if os.name == 'posix':
                shutil.copytree(root, output / 'native-runtime')
            (output / 'result.json').write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    asyncio.run(run())
