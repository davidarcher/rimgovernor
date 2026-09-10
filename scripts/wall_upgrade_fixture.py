"""Native ordinary-work acceptance within the disposable storeroom scenario."""
import asyncio
import time

from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.colony_upkeep import upkeep_nodes, reconcile_upkeep
from rimbot.wall_upgrade import method


async def verify_upgrade(rt, report, seconds, *, interrupt=False, corner=False, material_loss=False):
    facts = await rt.game.query('home/colony_facts', planning=True)
    wooden = {r['id'] for r in facts['upkeep']['structures'] if r['defName'] == 'Wall' and r['flammability'] > 0}
    wooden -= {r.get('wall_target') for p in rt.current_plan.progress.values() for r in p.issued.values()}
    walls = [r['current'] for r in facts['upkeep']['construction'] if r['definition'] == 'Wall' and r['present'] and r['current'] in wooden]
    setup = (await rt.bridge.call('test/stone_upgrade_setup', walls=';'.join(walls), corner=corner)).structuredContent
    result = report['wall_upgrade_material_loss' if material_loss else 'wall_upgrade_interruption' if interrupt else 'wall_upgrade'] = dict(setup=setup, methods=[], enclosure=[], samples=[])
    async def advance_safely(ticks=300):
        from rimbot.native_scenario import advance_game
        from rimbot.production_policy import sync_production_policy
        from rimbot.bridge_observation import observe
        async with rt.lock:
            await sync_production_policy(rt)
        await advance_game(rt, ticks, result)
        rt.batch = await observe(rt.game)
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()
    assert setup['success'] and (interrupt or material_loss or setup['initialBlocks'] == 0), setup
    goal_id = 'MaintainStoneShell'
    goal = rt.current_plan.colony_goals.setdefault(goal_id, ColonyGoal(priority_class=4))
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        facts = await rt.game.query('home/colony_facts', planning=True)
        result['samples'].append(facts)
        assert not facts['upkeep']['errors'], facts['upkeep']['errors']
        upkeep_nodes(facts, rt.current_plan.control, rt.current_plan.colony_goals, plan=rt.current_plan)
        state = rt.current_plan.control['upkeep'][goal_id]
        state['targets'] = [r for r in state['targets'] if r['id'] == setup['target']]
        if facts['definitions']['TableStonecutter']['available'] is False:
            from rimbot.colony_skills import SkillBlocked
            try:
                await method(rt, facts)
            except SkillBlocked as error:
                assert 'Native construction prerequisite unavailable: TableStonecutter' in str(error)
                assert 'TableStonecutter' in goal.evidence['required_capabilities']
                result['missing_research_refusal'] = str(error)
            else:
                raise AssertionError('Missing stonecutter research did not block construction')
            result['research_fixture'] = (await rt.bridge.call('test/stonecutting_prerequisite')).structuredContent
            assert result['research_fixture']['success']
            continue
        compiled = await method(rt, facts)
        if compiled is None:
            assert goal.evidence.get('waiting_for_stone_blocks')
            await advance_safely()
            continue
        key, actions = compiled
        if key.startswith('wall-'):
            result['produced_blocks'] = facts.get('resources', {}).get(setup['stone'], 0)
            assert result['produced_blocks'] >= goal.evidence['wall_upgrade']['reserved_blocks']
        steps, _ = rt.controller.skills.steps(goal_id, key, actions, facts)
        removal_step = next((s for s in steps if getattr(s.action, 'completion', None) == 'wall_removed'
                             and s.action.wall_guard.permanent is None), None)
        permanent_step = next((s for s in steps if getattr(s.action, 'replacement_of', None) is not None), None)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
            reason='Native stone wall acceptance', steps=steps).decision(rt.current_plan),
            actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
        goal.steps.extend(s.id for s in steps)
        goal.evidence.setdefault('methods', {})[key] = [s.id for s in steps]
        while time.monotonic() < deadline:
            rt.execution_task = asyncio.current_task()
            rt.handled_revision, rt.mode = rt.chat_revision, 'automate'
            await rt.hands.advance(rt)
            rt.mode = 'manual'
            if (interrupt or material_loss) and removal_step and rt.current_plan.progress[removal_step.id].issued:
                receipt = rt.current_plan.progress[removal_step.id].issued['0']
                assert receipt['confirmed'] and receipt['wall_removal_id']
                if material_loss:
                    loss = (await rt.bridge.call('test/wall_material_loss', material=setup['stone'])).structuredContent
                    assert loss['success'] and loss['before'] > 0 and loss['after'] == 0, loss
                    result['loss'] = loss
                    depleted = await rt.game.query('home/colony_facts', planning=True)
                    reconcile_upkeep(rt, depleted)
                    stopped = next(r for r in depleted['upkeep']['wallRemoval'] if r['id'] == receipt['wall_removal_id'])
                    assert stopped['blocker'].startswith('Materials no longer cover'), stopped
                    assert rt.current_plan.progress[removal_step.id].state == 'blocked'
                else:
                    await rt.halt()
                await advance_safely(1800)
                facts = await rt.game.query('home/colony_facts', planning=True)
                reconcile_upkeep(rt, facts)
                row = next(r for r in facts['upkeep']['wallRemoval'] if r['id'] == receipt['wall_removal_id'])
                assert not row['complete'] and row['blocker'] == 'Automation stopped; pending demolition invalidated', row
                assert any(r['id'] == setup['target'] for r in facts['upkeep']['structures'])
                assert row['retired'] and row['targetPresent'] and not row['designated'], row
                assert rt.current_plan.progress[removal_step.id].state == 'cancelled'
                assert rt.current_plan.progress[permanent_step.id].state == 'cancelled'
                assert not rt.current_plan.progress[permanent_step.id].issued
                count = len(facts['upkeep']['wallRemoval'])
                if material_loss:
                    result['restored'] = (await rt.bridge.call('test/wall_material_loss', material=setup['stone'], restore=loss['before'])).structuredContent
                    assert result['restored']['success'] and result['restored']['after'] >= loss['before']
                rt.handled_revision, rt.mode = rt.chat_revision, 'automate'
                await rt.hands.advance(rt)
                rt.mode = 'manual'
                after = await rt.game.query('home/colony_facts', planning=True)
                assert len(after['upkeep']['wallRemoval']) == count
                assert any(r['id'] == setup['target'] for r in after['upkeep']['structures'])
                result.update(outcome='passed', held_original=setup['target'], removal=row,
                    progress=[rt.current_plan.progress[s.id].model_dump() for s in steps])
                print('stone upgrade: ' + ('material loss' if material_loss else 'Manual') + ' retired pending demolition and unissued replacement without replay', flush=True)
                return
            await advance_safely()
            facts = await rt.game.query('home/colony_facts', planning=True)
            result['latest_facts'] = facts
            reconcile_upkeep(rt, facts)
            enclosure = (await rt.bridge.call('test/wall_enclosure', **{k: setup[k] for k in ('x', 'z', 'nx', 'nz')})).structuredContent
            result['enclosure'].append(enclosure)
            assert enclosure['enclosed'] and enclosure['fullyRoofed'] and not enclosure['pendingCollapse'], enclosure
            progress = [rt.current_plan.progress[s.id] for s in steps]
            assert not any(p.state in ('blocked', 'cancelled') for p in progress), [p.model_dump() for p in progress]
            if all(p.state == 'complete' for p in progress):
                break
        if not all(rt.current_plan.progress[s.id].state == 'complete' for s in steps):
            result['unfinished_buildings'] = await rt.game.query('home/list_buildings', status='pending', aggregate=False)
            result['unfinished_workers'] = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True, health=True)
            raise AssertionError('Native stone work timed out')
        result['methods'].append(dict(key=key, steps=[s.model_dump() for s in steps],
            progress=[rt.current_plan.progress[s.id].model_dump() for s in steps]))
        print('stone upgrade: ' + key + ' shared actions completed', flush=True)
        if key.startswith('wall-'):
            rows = facts['upkeep']['construction']
            origin = rt.current_plan.progress[permanent_step.id].issued['0']['placed_thing_id']
            permanent = next(r for r in rows if r['origin'] == origin)
            assert permanent['present'] and permanent['stage'] == 'built' and permanent['stuff'] == setup['stone']
            assert next(r for r in rows if r['current'] == setup['target'])['present'] is False
            backups = {rt.current_plan.progress[ref.step].issued[str(ref.slot)]['placed_thing_id']
                       for ref in removal_step.action.wall_guard.backups}
            assert len(backups) == setup['backupCount'] and all(not r['present'] for r in rows if r['origin'] in backups)
            result['permanent'] = permanent
            result['outcome'] = 'passed'
            return
    raise AssertionError('Native stone production or wall replacement timed out')
