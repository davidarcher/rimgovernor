"""Deliver surviving saved quantity portions through ordinary shared hauling."""
import asyncio
import time

from rimbot.bridge_observation import observe
from rimbot.colony_plan import CommitSteps
from rimbot.colony_upkeep import upkeep_nodes, upkeep_method, reconcile_upkeep
from rimbot.native_scenario import advance_game
from rimbot.production_policy import sync_production_policy


async def deliver(rt, report, tracking, seconds):
    result = report['resumed_haul'] = dict(samples=[], actions=[])
    deadline = time.monotonic() + seconds
    goal = rt.current_plan.colony_goals['SecureSupplies']
    pending_steps = []
    while time.monotonic() < deadline:
        facts = await rt.game.query('home/colony_facts', planning=True)
        assert not facts['upkeep']['errors'], facts['upkeep']['errors']
        ledger = next(r for r in facts['upkeep']['hauling'] if r['id'] == tracking)
        result['samples'].append(ledger)
        assert not ledger['blocker'], ledger
        reconcile_upkeep(rt, facts)
        if ledger['complete']:
            assert ledger['originalCount'] == 10 and ledger['requiredCount'] >= 10
            assert result['actions'], 'Saved fixture quantities need an observed ordinary hauling order'
            result.update(outcome='passed', delivery=ledger)
            print('saved quantities: ordinary pawn delivery observed after paired restart', flush=True)
            return
        if pending_steps:
            states = [rt.current_plan.progress[s.id] for s in pending_steps]
            assert not any(p.state in ('blocked', 'cancelled') for p in states), [p.model_dump() for p in states]
            if all(p.state == 'complete' for p in states): pending_steps = []
        if not pending_steps:
            upkeep_nodes(facts, rt.current_plan.control, rt.current_plan.colony_goals, plan=rt.current_plan)
            ids = {p['id'] for p in ledger['portions']}
            state = rt.current_plan.control['upkeep']['SecureSupplies']
            state['targets'] = [r for r in state['targets'] if r['id'] in ids]
            if state['targets']:
                roster = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True, health=True)
                compiled = await upkeep_method(rt, 'SecureSupplies', facts, roster['pawns'])
                assert compiled, 'No ordinary hauling method for saved surviving portions'
                key, actions = compiled
                assert all(a['kind'] == 'native_operation' and a['arguments']['action'] == 'haul' for a in actions)
                pending_steps, _ = rt.controller.skills.steps('SecureSupplies', key, actions, facts)
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Deliver observed surviving saved portions', steps=pending_steps).decision(rt.current_plan),
                    actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
                goal.steps.extend(s.id for s in pending_steps)
                goal.evidence.setdefault('methods', {})[key] = [s.id for s in pending_steps]
                result['actions'].append(dict(key=key, steps=[s.model_dump() for s in pending_steps]))
        rt.execution_task = asyncio.current_task()
        rt.handled_revision, rt.mode = rt.chat_revision, 'automate'
        await rt.hands.advance(rt)
        rt.mode = 'manual'
        async with rt.lock:
            await sync_production_policy(rt)
        await advance_game(rt, 600, result)
        rt.batch = await observe(rt.game)
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()
    raise AssertionError('Ordinary saved-quantity hauling did not complete within the bounded window')
