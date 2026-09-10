"""Native husbandry outcomes in a disposable local Docker fixture; no inference."""
from rimgovernor.native_scenario import advance_game
import asyncio
import json
import os
from pathlib import Path

from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.bridge import BridgeError
from rimgovernor.colony_plan import CommitSteps
from rimgovernor.config import ModelRole
from rimgovernor.husbandry import refresh_husbandry, husbandry_method
from rimgovernor.player_commands import apply_command
from rimgovernor.store import Store
from session_checkpoint_acceptance import ready
from deterministic_foothold import NoInference


async def run():
    root = Path(os.environ['RIMGOVERNOR_BRIDGE_ROOT'])
    report = {'passed': False, 'cases': [], 'samples': [],
              'scope': 'Seeded native fixture; ordinary feeding, birth, training, milk and wool outcomes. No local-model interpretation or seasonal survival claim.'}
    rt = BridgeRuntime(Store(root/'husbandry.sqlite'), root, fresh=True, headless=True,
                       model_factory=lambda _: NoInference())

    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        (root/'husbandry-result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
        print(name + ': ' + str(bool(passed)), flush=True)
        assert passed, name

    async def sample():
        herd = await rt.game.invoke('home/husbandry_facts', {})
        facts = await rt.game.query('home/colony_facts')
        report['samples'].append(dict(herd=herd, facts=facts))
        return herd, facts

    async def refusal(arguments, message):
        try:
            result = await rt.game.invoke('home/husbandry_config', arguments)
        except BridgeError as error:
            if message not in str(error): raise
            return {'success': False, 'error': str(error)}
        return result

    def stock(facts, resource):
        # Products already carried by a pawn remain actual collected stock.
        rows = facts['nativeForecastInputs']['combinedFoodSupply']['stocks']
        return max(facts['resources'].get(resource, 0),
                   sum(row['count'] for row in rows if row['defName'] == resource))

    def stored_nutrition(facts):
        return sum(row['nutrition'] for row in facts['nativeForecastInputs']['combinedFoodSupply']['stocks'])

    async def window(ticks):
        if rt.review_task and not rt.review_task.done(): await rt.review_task
        clock = await advance_game(rt, ticks, report)
        rt.supervisor.absorb(clock)
        rt.clock_events.extend(await rt.supervisor.poll())
        rt.receive_clock_events()
        if rt.review_task and not rt.review_task.done(): await rt.review_task

    try:
        await ready(rt)
        setup = (await rt.bridge.call('test/husbandry_setup')).structuredContent
        report['setup'] = setup
        record('fixture_setup', setup.get('success') is True)
        initial, initial_facts = await sample()
        ids = {a['id']: a for a in initial['animals']}
        mother, dog, cow = (ids[setup[k]] for k in ('mother', 'dog', 'cow'))
        record('native_product_and_pregnancy_prerequisites', mother['pregnant'] is True
               and mother['woolFull'] is True and cow['milkFull'] is True)
        identity = {k: rt.identity[k] for k in ('colonyId', 'loadToken', 'mapId')}
        refused = await refusal(dict(identity, animal=mother['id'],
            expected=mother['settingsToken'], census=mother['censusToken'], slaughter=True, dryRun=True), 'Animal protected')
        record('pregnant_animal_protected', refused.get('success') is False, receipt=refused)
        command = dict(kind='MaintainHerd', race=dog['race'], minimum=1, maximum=2,
                       feed_days=1, trainables=['Obedience'])
        accepted = await apply_command(rt, command, token=rt.context_token, revision=rt.chat_revision)
        await refresh_husbandry(rt, initial_facts)
        method, actions = await husbandry_method(rt, accepted['goal'])
        facts = dict(tick=initial['tick'])
        steps, _ = rt.controller.skills.steps(accepted['goal'], method, actions, facts)
        # Run the compiled settings request as explicit player work in Manual.
        for step in steps: step.source = 'PLAYER'
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Native husbandry acceptance', steps=steps).decision(rt.current_plan),
            actor=ModelRole.STRATEGIST, expected_token=rt.context_token, expected_revision=rt.chat_revision)
        goal = rt.current_plan.colony_goals[accepted['goal']]
        goal.steps = [s.id for s in steps]
        rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
        await rt.execute_manual_requests()
        async with asyncio.timeout(90):
            while any(rt.current_plan.progress[s.id].state not in ('complete', 'blocked') for s in steps):
                await asyncio.sleep(.2)
        record('shared_hands_training_request', all(rt.current_plan.progress[s.id].state == 'complete' for s in steps),
               progress={s.id: rt.current_plan.progress[s.id].model_dump() for s in steps})
        stale = await refusal(dict(identity, animal=dog['id'],
            expected=dog['settingsToken'], census=dog['censusToken'], trainable='Obedience', dryRun=True), 'Animal/settings changed')
        record('stale_animal_settings_refused', stale.get('success') is False, receipt=stale)
        achieved = set()
        for index in range(80):
            await window(1000 if index < 8 else 3000)
            herd, facts = await sample()
            animals = {a['id']: a for a in herd['animals']}
            if any(setup['mother'] in a['parents'] and a['id'] not in ids for a in animals.values()): achieved.add('birth')
            if 'birth' in achieved and animals[setup['mother']]['pregnant'] is True and animals[setup['mother']]['gestation'] < .5:
                achieved.add('breeding')
            if any(r['name'] == 'Obedience' and r['learned'] is True for r in animals[setup['dog']]['training']): achieved.add('training')
            if (animals[setup['cow']]['milkFullness'] < .5 and stock(facts, 'Milk') > stock(initial_facts, 'Milk')): achieved.add('milk')
            if (animals[setup['mother']]['woolFullness'] < .5 and facts['resources'].get('WoolMuffalo', 0) > initial_facts['resources'].get('WoolMuffalo', 0)): achieved.add('wool')
            if (animals[setup['cow']]['foodLevel'] > cow['foodLevel'] and stored_nutrition(facts) < stored_nutrition(initial_facts)): achieved.add('feeding')
            if all(animals[setup[k]]['contained'] is True for k in ('mother', 'father', 'cow')): achieved.add('containment')
            print('Observed outcomes: ' + ', '.join(sorted(achieved)), flush=True)
            if achieved == {'birth', 'breeding', 'training', 'milk', 'wool', 'feeding', 'containment'}: break
        for outcome in ('birth', 'breeding', 'training', 'milk', 'wool', 'feeding', 'containment'):
            record('ordinary_native_' + outcome, outcome in achieved)
        accepted = await apply_command(rt, dict(kind='MaintainHerd', race='Muffalo', minimum=2, maximum=2,
            breeding_pairs=1, allow_slaughter=True, feed_days=1), token=rt.context_token, revision=rt.chat_revision)
        await refresh_husbandry(rt, facts)
        method, actions = await husbandry_method(rt, accepted['goal'])
        victim = actions[0]['arguments']['animal']
        record('breeding_adults_preserved_by_cull_selection', victim not in (setup['mother'], setup['father']))
        steps, _ = rt.controller.skills.steps(accepted['goal'], method, actions, {'tick': herd['tick']})
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Native population target acceptance', steps=steps).decision(rt.current_plan),
            actor=ModelRole.STRATEGIST, expected_token=rt.context_token, expected_revision=rt.chat_revision)
        rt.current_plan.colony_goals[accepted['goal']].steps = [s.id for s in steps]
        rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
        await rt.execute_manual_requests()
        async with asyncio.timeout(90):
            while any(rt.current_plan.progress[s.id].state not in ('complete', 'blocked') for s in steps):
                await asyncio.sleep(.2)
        record('shared_hands_population_request', all(rt.current_plan.progress[s.id].state == 'complete' for s in steps),
            progress={s.id: rt.current_plan.progress[s.id].model_dump() for s in steps})
        for _ in range(30):
            await window(1000)
            herd, facts = await sample()
            if any(c['animal'] == victim and c['dead'] is True for c in herd['corpses']): break
        record('ordinary_native_slaughter_exact_corpse', any(c['animal'] == victim and c['dead'] is True for c in herd['corpses']))
        record('breeding_pair_survived_cull', {setup['mother'], setup['father']} <= {a['id'] for a in herd['animals']})
        seasonal = await apply_command(rt, dict(kind='MaintainHerd', race='Cow', minimum=1, maximum=1,
            feed_days=120, feed_resource='Hay'), token=rt.context_token, revision=rt.chat_revision)
        await refresh_husbandry(rt, facts)
        goal = rt.current_plan.colony_goals[seasonal['goal']]
        capacity = goal.evidence['husbandry']
        record('seasonal_feed_deficit_not_certified_by_pen', not capacity['satisfied']
            and 'Stored feed below seasonal reserve target' in capacity['blockers'], assessment=capacity)
        feed_goal = rt.current_plan.colony_goals[goal.evidence['feed_goal']]
        record('seasonal_feed_uses_shared_resource_goal', feed_goal.target['quantity'] > facts['resources'].get('Hay', 0)
            and feed_goal.evidence['herd_owner'] == seasonal['goal'], target=feed_goal.target)
        report['passed'] = True
    except BaseException as error:
        report['error'] = repr(error)
        raise
    finally:
        try:
            await rt.stop()
            report['stopped'] = True
        except BaseException as error:
            report['passed'], report['cleanup_error'] = False, repr(error)
            raise
        finally:
            (root/'husbandry-result.json').write_text(json.dumps(report, indent=2), encoding='utf8')


if __name__ == '__main__': asyncio.run(run())
