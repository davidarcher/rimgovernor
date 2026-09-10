from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.colony_plan import ColonyGoal, PlanSpec, StepProgress
from rimbot.colony_skills import ColonySkills
from rimbot.resource_accounting import validate_allocations
from rimbot.wall_upgrade import method, validate_bundle, reconcile, release_pending
from test_construction_ownership import scenario


def fixture(stock=20, corner=False):
    plan, facts, _ = scenario()
    wall = dict(id='built-wall', defName='Wall', flammability=1, count=1)
    facts.update(resources={'BlocksGranite': stock}, policyResources={'BlocksGranite': {}},
                 definitions={'Wall': dict(available=True, stuff='WoodLog', costs={'WoodLog': 5})})
    facts['upkeep']['structures'] = [wall]
    plan.colony_goals['MaintainStoneShell'] = ColonyGoal(priority_class=4)
    plan.control['upkeep'] = {'MaintainStoneShell': dict(known=True, targets=[wall])}
    site = dict(x=10, z=20, nx=1, nz=0, left='side-left', right='side-right',
                backupCells=[dict(x=11, z=z) for z in (19, 20, 21)])
    if corner:
        site.update(nz=1, backupCells=[])
    result = dict(success=True, tick=10, sites=[site], materials=[dict(defName='BlocksGranite', costs={'BlocksGranite': 5})])
    def reply(tool, args, **kwargs):
        if tool == 'home/wall_upgrade_sites':
            return deepcopy(result)
        if tool == 'home/place_building':
            return dict(canPlace=(args['x'], args['z']) != (10, 20), costList=[dict(defName='BlocksGranite', count=5)],
                materials={'rows': [dict(defName='BlocksGranite', available=stock)]})
        raise AssertionError(tool)
    game = SimpleNamespace(invoke=AsyncMock(side_effect=reply), query=AsyncMock(return_value=facts))
    return SimpleNamespace(current_plan=plan, game=game, context_token='load'), facts


async def bundle(rt, facts):
    key, actions = await method(rt, facts)
    steps, costs = ColonySkills(rt).steps('MaintainStoneShell', key, actions, facts)
    return PlanSpec(steps=rt.current_plan.spec.steps + steps), steps, costs


@pytest.mark.asyncio
async def test_all_four_stone_walls_reserved_before_native_demolition():
    rt, facts = fixture()
    spec, steps, costs = await bundle(rt, facts)
    assert len(steps) == 6
    assert sum(c.get('BlocksGranite', 0) for slots in costs.values() for c in slots.values()) == 20
    allowed = await validate_bundle(spec, rt.current_plan, rt.game)
    assert allowed == {steps[2].id}
    with pytest.raises(ValueError, match='No legal costed placement'):
        await validate_allocations(spec, rt.current_plan, rt.game)
    allocations = await validate_allocations(spec, rt.current_plan, rt.game, deferred_wall_steps=allowed)
    assert sum(c.get('BlocksGranite', 0) for slots in allocations.values() for c in slots.values()) == 20
    assert all(s.after[0].step == steps[i-1].id for i, s in enumerate(steps[1:], 1))


@pytest.mark.asyncio
async def test_corner_reserves_permanent_wall_and_keeps_salvage_approaches_open():
    rt, facts = fixture(stock=5, corner=True)
    spec, steps, costs = await bundle(rt, facts)
    assert len(steps) == 2 and steps[0].action.wall_guard.backups == []
    assert steps[0].action.wall_guard.material == 'BlocksGranite'
    assert sum(c.get('BlocksGranite', 0) for slots in costs.values() for c in slots.values()) == 5
    allowed = await validate_bundle(spec, rt.current_plan, rt.game)
    assert allowed == {steps[1].id}
    allocations = await validate_allocations(spec, rt.current_plan, rt.game, deferred_wall_steps=allowed)
    assert sum(c.get('BlocksGranite', 0) for slots in allocations.values() for c in slots.values()) == 5
    assert steps[1].after[0].step == steps[0].id
    assert PlanSpec.model_validate_json(spec.model_dump_json()) == spec
    malformed = spec.model_dump()
    malformed['steps'][1]['action']['wall_guard']['backups'].append(dict(step='wall',slot=0))
    with pytest.raises(ValueError, match='open corner approach'):
        PlanSpec.model_validate(malformed)


@pytest.mark.asyncio
@pytest.mark.parametrize('change', ['source', 'uncertain', 'backup', 'dependency', 'material', 'site', 'orphan', 'early_cleanup'])
async def test_deferred_placement_requires_exact_owned_safe_bundle(change):
    rt, facts = fixture()
    spec, steps, _ = await bundle(rt, facts)
    if change == 'source': rt.current_plan.colony_goals['shelter'].source = 'PLAYER'
    elif change == 'uncertain': rt.current_plan.progress['wall'].issued['0']['confirmed'] = False
    elif change == 'backup': steps[0].action.placements[0].x += 1
    elif change == 'dependency': steps[1].after = []
    elif change == 'material': steps[2].action.placements[0].materials = ['WoodLog']
    elif change == 'site': steps[1].action.wall_guard.nx = -1
    elif change == 'orphan': spec.steps = [s for s in spec.steps if s.id != steps[2].id]
    else: steps[3].after = []
    with pytest.raises(ValueError):
        await validate_bundle(spec, rt.current_plan, rt.game)


@pytest.mark.asyncio
async def test_native_demolition_receipt_waits_and_loss_does_not_count():
    rt, facts = fixture()
    spec, steps, _ = await bundle(rt, facts)
    rt.current_plan.spec = spec
    for step in steps:
        rt.current_plan.progress[step.id] = StepProgress()
    progress = rt.current_plan.progress[steps[1].id]
    progress.state = 'waiting'
    progress.issued['0'] = dict(confirmed=True, load_token='load', player_direction=0,
        issued_tick=1, wall_removal_id='removal', wall_target='built-wall')
    facts['upkeep']['wallRemoval'] = []
    reconcile(rt, facts)
    assert progress.state == 'waiting'
    row = dict(id='removal', target='built-wall', complete=False, blocker='Wall destroyed by fire', playerOwned=False)
    facts['upkeep']['wallRemoval'] = [row]
    reconcile(rt, facts)
    assert progress.failure.code == 'wall_removal_invalidated'
    progress.state, progress.failure = 'waiting', None
    row.update(blocker=None, complete=True, completedTick=9)
    reconcile(rt, facts)
    assert progress.state == 'complete'


@pytest.mark.asyncio
async def test_roundtrip_keeps_reference_bundle_and_load_change_blocks_removal():
    rt, facts = fixture()
    spec, steps, _ = await bundle(rt, facts)
    assert PlanSpec.model_validate_json(spec.model_dump_json()) == spec
    rt.current_plan.spec = spec
    for step in steps: rt.current_plan.progress[step.id] = StepProgress()
    progress = rt.current_plan.progress[steps[1].id]
    progress.state = 'waiting'
    progress.issued['0'] = dict(confirmed=True, load_token='old', player_direction=0, wall_removal_id='removal')
    facts['upkeep']['wallRemoval'] = []
    reconcile(rt, facts)
    assert progress.failure.code == 'wall_removal_invalidated'


@pytest.mark.asyncio
@pytest.mark.parametrize('change', ['none', 'player', 'missing', 'designated', 'uncertain', 'issued_successor'])
async def test_retirement_requires_native_cancellation_and_preserves_issued_work(change):
    rt, facts = fixture()
    spec, steps, _ = await bundle(rt, facts)
    rt.current_plan.spec = spec
    for step in steps: rt.current_plan.progress[step.id] = StepProgress()
    rt.current_plan.progress[steps[0].id].state = 'complete'
    progress = rt.current_plan.progress[steps[1].id]
    progress.state = 'blocked'
    progress.issued['0'] = dict(confirmed=change != 'uncertain', load_token='old', player_direction=0,
        wall_removal_id='removal', wall_target='built-wall')
    row = dict(id='removal', target='built-wall', complete=False, blocker='Automation stopped', retired=True,
        playerOwned=change == 'player', targetPresent=change != 'missing', designated=change == 'designated')
    facts['upkeep']['wallRemoval'] = [row]
    if change == 'issued_successor':
        rt.current_plan.progress[steps[2].id].issued['0'] = dict(confirmed=False)
    reconcile(rt, facts)
    retired = change in ('none', 'issued_successor')
    assert (progress.state == 'cancelled') == retired
    assert rt.current_plan.progress[steps[0].id].state == 'complete'
    assert rt.current_plan.progress['wall'].state == 'complete'
    successor = rt.current_plan.progress[steps[2].id]
    assert (successor.state == 'cancelled') == (change == 'none')
    if retired:
        assert progress.issued['0']['retirement'] == row
        assert all(rt.current_plan.progress[s.id].state == 'cancelled' for s in steps[3:])
        revision = rt.current_plan.revision
        reconcile(rt, facts)
        assert rt.current_plan.revision == revision
        restored = type(rt.current_plan).model_validate_json(rt.current_plan.model_dump_json())
        assert steps[1].id in restored.cancelled_ids


@pytest.mark.asyncio
async def test_changed_direction_releases_pending_and_uncertain_demolition_before_clock():
    rt, facts = fixture()
    spec, steps, _ = await bundle(rt, facts)
    rt.current_plan.spec = spec
    for step in steps: rt.current_plan.progress[step.id] = StepProgress()
    progress = rt.current_plan.progress[steps[1].id]
    progress.state = 'waiting'
    progress.issued['0'] = dict(confirmed=True, load_token='load', player_direction=0)
    rt.game.invoke = AsyncMock(return_value=dict(success=True))
    await release_pending(rt, changed_only=True)
    rt.game.invoke.assert_not_awaited()
    rt.current_plan.control['player_direction'] = 1
    await release_pending(rt, changed_only=True)
    rt.game.invoke.assert_awaited_once_with('home/upkeep_wall', dict(action='release', dryRun=False), allow_write=True)
    progress.issued['0'] = dict(confirmed=False)
    await release_pending(rt)
    assert rt.game.invoke.await_count == 2
    await release_pending(rt)
    assert rt.game.invoke.await_count == 2
    rt.current_plan.control.pop('wall_release_signature')
    rt.game.invoke.return_value = dict(success=False)
    with pytest.raises(ValueError, match='could not be invalidated'):
        await release_pending(rt)


@pytest.mark.asyncio
async def test_retired_targets_do_not_starve_independent_wall_batches():
    from rimbot.strategic_state import fingerprint
    rt, facts = fixture()
    state = rt.current_plan.control['upkeep']['MaintainStoneShell']
    retired = [dict(id='retired-' + str(i), count=1) for i in range(9)]
    state['targets'] = retired + state['targets']
    rt.current_plan.colony_goals['MaintainStoneShell'].evidence['methods'] = {
        'wall-' + fingerprint(row['id'])[:12]: ['retired-step'] for row in retired}
    key, actions = await method(rt, facts)
    assert key == 'wall-' + fingerprint('built-wall')[:12]
    assert len(actions) == 6
    rt.game.invoke.assert_awaited_once()


@pytest.mark.asyncio
async def test_stonecutter_uses_verified_rotation_that_fits_existing_shelter():
    from rimbot.development import placement
    from rimbot.colony_plan import ColonyPlan
    f = dict(definitions={'TableStonecutter': dict(available=True, stuff='WoodLog')}, center=dict(x=10, z=10),
             cells=[dict(x=10, z=z, walkable=True, occupied=False, indoors=True) for z in range(10, 13)])
    rt = SimpleNamespace(current_plan=ColonyPlan(), game=SimpleNamespace(), inspect_native=AsyncMock(return_value=dict(canPlace=True,
        rotations=[dict(rotation='north', accepted=True, blockingThings=[], occupiedCells=[dict(x=x, z=10) for x in range(10, 13)]),
                   dict(rotation='east', accepted=True, blockingThings=[], occupiedCells=[dict(x=10, z=z) for z in range(10, 13)])])))
    result = await placement(rt, f, 'TableStonecutter', indoors=True, rotations='all')
    assert result['placements'][0]['rotation'] == 'east'
    assert rt.inspect_native.await_count == 1 and rt.inspect_native.call_args.args[1]['rotation'] == 'all'


@pytest.mark.asyncio
async def test_manual_release_passes_real_gameplay_write_boundary():
    from rimbot.bridge_game import BridgeGame
    rt, facts = fixture()
    spec, steps, _ = await bundle(rt, facts)
    rt.current_plan.spec = spec
    for step in steps: rt.current_plan.progress[step.id] = StepProgress()
    rt.current_plan.progress[steps[1].id] = StepProgress(state='waiting', issued={'0': dict(confirmed=True)})
    bridge = SimpleNamespace(call=AsyncMock(return_value=SimpleNamespace(structuredContent=dict(success=True))))
    rt.game = BridgeGame(bridge)
    rt.game.schemas['home/upkeep_wall'] = dict(type='object', additionalProperties=False,
        properties=dict(action=dict(type='string'), dryRun=dict(type='boolean')))
    rt.mode = 'manual'
    await release_pending(rt)
    bridge.call.assert_awaited_once_with('home/upkeep_wall', action='release', dryRun=False)


@pytest.mark.asyncio
@pytest.mark.parametrize('invalid', ['suspended', 'blocked_replacement', 'cancelled', 'blocked_removal'])
async def test_clock_invalidates_demolition_when_its_batch_loses_admission(invalid):
    rt, facts = fixture()
    spec, steps, _ = await bundle(rt, facts)
    rt.current_plan.spec = spec
    for step in steps: rt.current_plan.progress[step.id] = StepProgress()
    rt.current_plan.progress[steps[1].id] = StepProgress(state='waiting', issued={'0':
        dict(confirmed=True, load_token='load', player_direction=0)})
    if invalid == 'suspended': rt.current_plan.colony_goals['MaintainStoneShell'].status = 'suspended'
    elif invalid == 'cancelled': rt.current_plan.colony_goals['MaintainStoneShell'].cancelled = True
    elif invalid == 'blocked_removal': rt.current_plan.progress[steps[1].id].state = 'blocked'
    else: rt.current_plan.progress[steps[2].id].state = 'blocked'
    rt.game.invoke = AsyncMock(return_value=dict(success=True))
    await release_pending(rt, changed_only=True)
    rt.game.invoke.assert_awaited_once_with('home/upkeep_wall', dict(action='release', dryRun=False), allow_write=True)


@pytest.mark.asyncio
async def test_native_demolition_transfers_project_slot_until_exact_replacement_is_built(tmp_path):
    from rimbot.projects import ProjectBook
    from rimbot.wall_upgrade import project_handoffs
    from rimbot.spatial import validate_geometry, GeometryConflict
    rt, facts = fixture()
    spec, steps, _ = await bundle(rt, facts)
    plan = rt.current_plan
    plan.spec = spec
    for step in steps: plan.progress[step.id] = StepProgress()
    plan.progress['wall'].state = 'waiting'
    progress = plan.progress[steps[1].id]
    progress.state = 'waiting'
    progress.issued['0'] = dict(confirmed=True, load_token='c:m:l', player_direction=0,
        issued_tick=1, wall_removal_id='removal', wall_target='built-wall')
    facts['upkeep']['construction'][0]['present'] = False
    facts['upkeep']['wallRemoval'] = [dict(id='removal', target='built-wall', complete=True,
        completedTick=9, blocker=None, playerOwned=False)]
    def query(tool, **kwargs):
        if tool == 'home/colony_identity': return dict(colonyId='c', mapId='m', loadToken='l')
        if tool == 'home/colony_facts': return facts
        return dict(buildings=[], skipped={})
    rt.game.query = AsyncMock(side_effect=query)
    book = ProjectBook()
    project = book.upsert(dict(title='Original wall', source_step='wall', targets=[dict(
        kind='building', def_name='Wall', x=10, z=20, stuff='WoodLog')]))
    await book.reconcile(rt.game, plan=plan)
    assert progress.state == 'complete' and project.state == 'pending'
    validate_geometry(spec, current=plan)
    changed = spec.model_copy(deep=True)
    changed.steps[0].action.placements[0].materials = ['Steel']
    with pytest.raises(GeometryConflict): validate_geometry(changed, current=plan)
    destination = plan.progress[steps[2].id]
    destination.state = 'waiting'
    destination.issued['0'] = dict(confirmed=True, outcome='placed', placed_thing_id='stone-blueprint', stuff='BlocksGranite')
    successor = dict(origin='stone-blueprint', current='stone-built', stage='built', present=True,
        blocker=None, definition='Wall', stuff='BlocksGranite', x=10, z=20, rotation=0)
    facts['upkeep']['construction'].append(successor)
    await book.reconcile(rt.game, plan=plan)
    assert project.state == 'complete' and project.matched_ids == ['stone-built']
    from rimbot.plan_archive import bind_archive, prepare_archive, finish_archive
    from rimbot.store import Store
    removal = steps[1]
    plan.control.setdefault('retired_steps', {})[removal.id] = removal.model_dump()
    plan.spec.steps = [s for s in plan.spec.steps if s.id != removal.id]
    for step in plan.spec.steps: step.after = [d for d in step.after if d.step != removal.id]
    store = Store(tmp_path / 'handoff.sqlite')
    bind_archive(plan, store, 'colony')
    snapshot, records, methods, evidence = prepare_archive(plan)
    store.archive_and_set('colony', 'plan', snapshot, records, methods, evidence)
    finish_archive(plan, snapshot, records, methods, evidence)
    assert removal.id not in plan.progress
    plan.control.pop('wall_handoffs')
    await book.reconcile(rt.game, plan=plan)
    assert project.state == 'complete' and project.matched_ids == ['stone-built']
    validate_geometry(plan.spec, current=plan)
    successor['present'] = False
    destination.state = 'blocked'
    await book.reconcile(rt.game, plan=plan)
    assert project.state != 'complete'
    facts['upkeep']['wallRemoval'][0]['playerOwned'] = True
    assert await project_handoffs(plan, rt.game) == {}
    with pytest.raises(GeometryConflict): validate_geometry(plan.spec, current=plan)
    store.close()


@pytest.mark.asyncio
async def test_furniture_placement_preserves_native_stockpile_cells():
    from rimbot.development import placement
    from rimbot.colony_plan import ColonyPlan
    from rimbot.colony_skills import SkillBlocked
    facts = dict(definitions={'TableStonecutter': dict(available=True)}, center=dict(x=10, z=10),
        cells=[dict(x=10, z=10, walkable=True, occupied=False, indoors=True, zone='stockpile')])
    rt = SimpleNamespace(current_plan=ColonyPlan(), game=SimpleNamespace(), inspect_native=AsyncMock())
    with pytest.raises(SkillBlocked, match='No safe observed placement'):
        await placement(rt, facts, 'TableStonecutter', indoors=True, rotations='all')
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_stonecutter_preserves_selected_corners_interior_construction_approach(monkeypatch):
    from rimbot.colony_skills import SkillBlocked
    rt, facts = fixture(stock=0, corner=True)
    facts['definitions']['TableStonecutter'] = dict(available=True, stuff='WoodLog')
    facts['center'] = dict(x=9, z=19)
    facts['cells'] = [dict(x=x, z=z, walkable=True, occupied=False, indoors=True)
                      for x in (8, 9) for z in (18, 19, 20)]
    monkeypatch.setattr('rimbot.production_policy.resource_method', AsyncMock(side_effect=SkillBlocked(
        'No available native production recipe and workbench for BlocksGranite')))
    def preview(tool, args):
        return dict(canPlace=True, rotations=[dict(rotation='east', accepted=True, blockingThings=[],
            occupiedCells=[dict(x=args['x'], z=args['z']+delta) for delta in (-1, 0, 1)])])
    rt.inspect_native = AsyncMock(side_effect=preview)
    key, actions = await method(rt, facts)
    assert key == 'stonecutter'
    assert actions[0]['placements'][0] == dict(def_name='TableStonecutter', x=8, z=19, rotation='east', materials=['WoodLog'])


@pytest.mark.asyncio
async def test_stonecutter_falls_back_outdoors_without_using_wall_work_area(monkeypatch):
    from rimbot.colony_skills import SkillBlocked
    rt, facts = fixture(stock=0)
    facts['definitions']['TableStonecutter'] = dict(available=True, stuff='WoodLog')
    facts['center'] = dict(x=11, z=20)
    facts['cells'] = [dict(x=x, z=z, walkable=True, occupied=False, indoors=False)
                      for x in (11, 12) for z in (19, 20, 21)]
    monkeypatch.setattr('rimbot.production_policy.resource_method', AsyncMock(side_effect=SkillBlocked(
        'No available native production recipe and workbench for BlocksGranite')))
    rt.inspect_native = AsyncMock(side_effect=lambda tool, args: dict(canPlace=True,
        rotations=[dict(rotation='east', accepted=True, blockingThings=[],
            occupiedCells=[dict(x=args['x'], z=args['z']+delta) for delta in (-1, 0, 1)])]))
    key, actions = await method(rt, facts)
    assert key == 'stonecutter'
    assert actions[0]['placements'][0] == dict(def_name='TableStonecutter', x=12, z=20, rotation='east', materials=['WoodLog'])


@pytest.mark.asyncio
async def test_retired_wall_does_not_start_resource_production_again(monkeypatch):
    from rimbot.colony_skills import SkillBlocked
    from rimbot.strategic_state import fingerprint
    rt, facts = fixture(stock=0)
    goal = rt.current_plan.colony_goals['MaintainStoneShell']
    goal.evidence['methods'] = {'wall-' + fingerprint('built-wall')[:12]: ['retired-removal']}
    production = AsyncMock()
    monkeypatch.setattr('rimbot.production_policy.resource_method', production)
    with pytest.raises(SkillBlocked, match='previously admitted replacement'):
        await method(rt, facts)
    production.assert_not_awaited()
    rt.game.invoke.assert_not_awaited()
