"""Observe competing native construction, partial restart and dependent pawn work."""
import argparse
import asyncio
from copy import deepcopy
import json
from pathlib import Path
import time
import traceback

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge import BridgeError, runtime_file_read
from rimbot.colony_plan import CommitSteps, Decision, PlanStep
from rimbot.headless import isolated_root, prepare
from rimbot.player_commands import apply_command
from rimbot.session_checkpoint import create_checkpoint, prepare_resume, stop_for_restart
from rimbot.store import Store


async def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    root = isolated_root(args.source_root, args.output/'bridge'); prepare(root)
    store = Store(args.output/'state.sqlite')
    rt = BridgeRuntime(store, root, fresh=True, headless=True, model_factory=lambda _: NoInference())
    report = dict(outcome='failed', cases=[], save_edits=[])
    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        (args.output/'progress.json').write_text(json.dumps(report, indent=2))
        print(name+': '+str(bool(passed)), flush=True)
        assert passed, name
    async def facts(): return await rt.game.query('home/colony_facts', planning=True)
    async def buildings(): return await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True)
    async def wood_inventory():
        census = await rt.game.query('home/list_things', match='WoodLog', ownership='all', includeHeld=True, maxPositionsPerDef=200)
        return sum(r['total'] for r in census['things'] if r['defName'] == 'WoodLog'), census
    async def construction_consumption(identity):
        ledger = (await rt.bridge.call('test/construction_ledger')).structuredContent
        assert ledger['readable'], ledger
        step = next(s for s in rt.current_plan.spec.steps if s.id == identity)
        cells = {(p.x, p.z) for p in step.action.placements}
        events = [e for e in ledger['events'] if e['defName'] == 'Wall' and (e['x'], e['z']) in cells]
        completed = [e for e in events if e['outcome'] == 'CompleteConstruction']
        assert len(completed) == len(cells) and {(e['x'], e['z']) for e in completed} == cells, events
        return sum(e['consumed'].get('WoodLog', 0) for e in events), events
    async def reconcile():
        await rt.projects.reconcile(rt.game, plan=rt.current_plan)
        rt.reconcile_plan()
    async def window(ticks=600):
        if rt.review_task and not rt.review_task.done(): await rt.review_task
        await rt.supervisor.change('Superfast', max_ticks=ticks)
        async with asyncio.timeout(60):
            while True:
                state = (await runtime_file_read(rt.bridge.call, 'home/supervised_play', op='status')).structuredContent
                if not state['active']: break
                await asyncio.sleep(.2)
        if state['stopReason'] == 'letter_pause':
            status = await rt.game.query('home/status', colonists=False, threats=True)
            record('inspected_native_letter_pause', state['pauseVerified'] and status['counts']['hostileCount'] == 0
                and status['counts']['huntingPredatorCount'] == 0, clock=state,
                letters=await rt.game.invoke('rimworld/list_letters', {}))
            rt.supervisor.absorb(state); rt.supervisor.allow_resume()
        else:
            assert state['stopReason'] in ('tick_budget', 'requested_pause') and state['pauseVerified'], state
        if rt.review_task and not rt.review_task.done(): await rt.review_task
        await reconcile()
        return state
    async def dispatch(ids, limit=12):
        # The explicitly requested PLAYER steps use the same scoped Hands entry.
        async with rt.lock:
            await rt.refresh_clock_events()
        if rt.review_task and not rt.review_task.done(): await rt.review_task
        if rt.chat_revision > rt.handled_revision or rt.wake.is_set(): await rt.review()
        rt.manual_execution = (rt.context_token, rt.chat_revision, rt.current_plan.revision)
        try:
            await rt.hands.advance(rt, max_operations=limit, only_ids=set(ids))
        finally:
            rt.manual_execution = None
        await reconcile()
    async def finish(identity):
        deadline = time.monotonic()+args.seconds
        while rt.current_plan.progress[identity].state != 'complete' and time.monotonic() < deadline:
            if rt.current_plan.progress[identity].state == 'blocked': break
            await window()
        record('ordinary_pawn_completion_'+identity, rt.current_plan.progress[identity].state == 'complete',
            progress=rt.current_plan.progress[identity].model_dump(), buildings=await buildings(), facts=await facts())
    try:
        await ready(rt); rt.execution_task = asyncio.current_task()
        allow = await rt.controller.skills.designator('Designator_Unforbid')
        forbid = await rt.controller.skills.designator('Designator_Forbid')
        initial = await facts()
        for cell in initial.get('forbiddenSupplies', []):
            await rt.game.invoke('rimworld/apply_architect_designator', dict(designatorId=allow,
                x=cell['x'], z=cell['z'], keepSelected=False, dryRun=False), allow_write=True)
        stock = await rt.game.query('home/list_things', match='WoodLog', includeHeld=False, maxPositionsPerDef=200)
        wood = next(r for r in stock['things'] if r['defName'] == 'WoodLog')
        positions = wood['positions']
        report['initial_wood'] = wood
        assert len(positions) > 1, 'Competition needs separate ordinary stock stacks'
        roster = await rt.game.query('home/list_pawns', colonistsOnly=True, work=True)
        for pawn in roster['pawns']:
            if any(w['name'] == 'Construction' and not w['disabled'] for w in pawn['work']['types']):
                result = await apply_command(rt, dict(kind='SetWorkPriority', pawn=pawn['thingId'], work_type='Construction', priority=1),
                    token=rt.context_token, revision=rt.chat_revision)
                await rt.execute_manual_requests()
        native = await facts()
        cells = sorted((c for c in native['cells'] if c['walkable'] and not c['occupied'] and not c.get('zone')),
            key=lambda c: (c['x']-native['center']['x'])**2+(c['z']-native['center']['z'])**2)
        placements = []
        for c in cells:
            if any(max(abs(c['x']-p['x']), abs(c['z']-p['z'])) < 2 for p in placements): continue
            preview = await rt.inspect_native('home/place_building', dict(defName='Wall', stuff='WoodLog',
                x=c['x'], z=c['z'], rotation='north', dryRun=True))
            if preview.get('canPlace') is not True: continue
            placements.append(dict(def_name='Wall', materials=['WoodLog'], x=c['x'], z=c['z']))
            if len(placements) == 16: break
        assert len(placements) == 16
        steps = [PlanStep(id=identity, title=identity, source='PLAYER', priority=priority,
            action=dict(kind='place_buildings', placements=batch), completion_criteria='All native walls built')
            for identity, priority, batch in [('first', 90, placements[:8]), ('later', 10, placements[8:])]]
        steps.append(PlanStep(id='dependent', title='Dependent spot', source='PLAYER',
            after=[dict(step='later', when='complete')], completion_criteria='Native sleeping spot built',
            action=dict(kind='place_buildings', placements=[dict(def_name='SleepingSpot', x=placements[0]['x']+1, z=placements[0]['z'])])))
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision, reason='Competing native construction', steps=steps).decision(rt.current_plan),
            actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
        costs = deepcopy(rt.current_plan.control['costs'])
        # Keep one ordinary stack accessible after admitting both costed projects.
        available_stacks = []
        for p in positions:
            c = p.get('position', p)
            try:
                preview = await rt.inspect_native('rimworld/apply_architect_designator', dict(designatorId=forbid,
                    x=c['x'], z=c['z'], keepSelected=False, dryRun=True))
            except BridgeError as error:
                preview = json.loads(error.detail)
                if preview.get('acceptedCellCount') != 0: raise
            if preview.get('acceptedCellCount', 0) > 0:
                available_stacks.append(p)
        assert len(available_stacks) > 1, 'Need at least two accessible ordinary wood stacks'
        held = available_stacks[1:]
        for p in held:
            c = p.get('position', p)
            await rt.game.invoke('rimworld/apply_architect_designator', dict(designatorId=forbid,
                x=c['x'], z=c['z'], keepSelected=False, dryRun=False), allow_write=True)
        constrained = (await facts())['resources']['WoodLog']
        first_cost = sum(v.get('WoodLog', 0) for v in costs['first'].values())
        later_cost = sum(v.get('WoodLog', 0) for v in costs['later'].values())
        record('ordinary_stock_change_creates_competition', first_cost <= constrained < first_cost+later_cost,
               available=constrained, costs=costs)
        inventory_before_first, stock_before_first = await wood_inventory()
        await dispatch(['first', 'later', 'dependent'], limit=3)
        record('interrupted_batch_retains_unissued_reservations', len(rt.current_plan.progress['first'].issued) == 3
            and not rt.current_plan.progress['later'].issued and not rt.current_plan.progress['dependent'].issued,
            plan=rt.current_plan.model_dump(), buildings=await buildings())
        rt.execution_task = None
        checkpoint = await create_checkpoint(rt, rt.context_token)
        expected_plan, expected_buildings = rt.current_plan.model_dump(), await buildings()
        old_token = rt.context_token
        await stop_for_restart(rt, old_token, checkpoint['manifest_path']); await rt.stop(); store.close()
        data, state = prepare_resume(checkpoint['manifest_path'])
        store = Store(state/'bridge.sqlite')
        rt = BridgeRuntime(store, root, fresh=True, headless=True, resume=checkpoint['manifest_path'], model_factory=lambda _: NoInference())
        await ready(rt); rt.execution_task = asyncio.current_task()
        actual = await buildings()
        record('paused_restart_preserves_partial_project_and_costs', rt.mode == 'manual' and rt.context_token != old_token
            and rt.current_plan.model_dump() == expected_plan and rt.current_plan.control['costs'] == costs
            and {b['thingId'] for b in actual['buildings']} == {b['thingId'] for b in expected_buildings['buildings']}
            and rt.counters['actions'] == 0, checkpoint=checkpoint)
        ledger = (await rt.bridge.call('test/construction_ledger')).structuredContent
        record('native_consumption_observer_starts_before_pawn_work', ledger['readable'] and not ledger['events'])
        first_receipts = deepcopy(rt.current_plan.progress['first'].issued)
        await dispatch(['first', 'later', 'dependent'])
        later = rt.current_plan.progress['later']
        record('affordable_project_dispatches_before_competitor', len(rt.current_plan.progress['first'].issued) == 8
            and later.state == 'blocked' and later.failure.code == 'construction_resources' and not later.issued
            and not rt.current_plan.progress['dependent'].issued, plan=rt.current_plan.model_dump())
        await finish('first')
        after_first, stock_after_first = await wood_inventory()
        consumed_first, first_events = await construction_consumption('first')
        record('first_project_actually_consumes_native_materials', inventory_before_first-after_first == consumed_first
            and consumed_first >= first_cost,
            before=inventory_before_first, after=after_first, consumed=consumed_first, base_cost=first_cost, events=first_events,
            before_census=stock_before_first, after_census=stock_after_first)
        for p in held:
            c = p.get('position', p)
            await rt.game.invoke('rimworld/apply_architect_designator', dict(designatorId=allow,
                x=c['x'], z=c['z'], keepSelected=False, dryRun=False), allow_write=True)
        restocked = (await facts())['resources']['WoodLog']
        inventory_before_later, stock_before_later = await wood_inventory()
        retry = Decision(expected_revision=rt.current_plan.revision, disposition='continue', assessment='Native stock restored',
            rationale='Explicitly resume the retained competing project', reply='Resume retained work', retry_steps=['later'])
        await rt.commit_strategy(retry, actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
        await dispatch(['later', 'dependent'])
        record('restock_resumes_same_project_without_replaying_first', len(later.issued) == 8
            and all(rt.current_plan.progress['first'].issued[k] == v for k, v in first_receipts.items())
            and not rt.current_plan.progress['dependent'].issued, restocked=restocked, plan=rt.current_plan.model_dump())
        await finish('later')
        after_later, stock_after_later = await wood_inventory()
        consumed_later, later_events = await construction_consumption('later')
        record('competing_project_actually_consumes_native_materials', inventory_before_later-after_later == consumed_later
            and consumed_later >= later_cost,
            before=inventory_before_later, after=after_later, consumed=consumed_later, base_cost=later_cost, events=later_events,
            before_census=stock_before_later, after_census=stock_after_later)
        await dispatch(['dependent'])
        await finish('dependent')
        record('dependent_chain_waits_for_actual_pawn_completion', rt.current_plan.progress['dependent'].state == 'complete', plan=rt.current_plan.model_dump())
        before = rt.counters['actions']
        await dispatch(['first', 'later', 'dependent'])
        record('completed_chain_does_not_duplicate_orders', rt.counters['actions'] == before and rt.counters['model_calls'] == 0)
        report['outcome'] = 'passed'
    except Exception as error:
        report.update(error=str(error), traceback=traceback.format_exc(), plan=rt.current_plan.model_dump())
    finally:
        rt.mode, rt.execution_task = 'manual', None
        await rt.stop(); store.close()
        report['cleanup'] = 'owned runtime stopped'
        (args.output/'result.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k: report.get(k) for k in ('outcome', 'error')}), flush=True)
    if report['outcome'] != 'passed': raise SystemExit(1)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=480)
    asyncio.run(run(parser.parse_args()))
