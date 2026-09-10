from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.colony_plan import ColonyPlan, ColonyGoal, PlanStep, StepProgress, Zone, Rectangle
from rimbot.upkeep_storage import covered_storage


def runtime():
    return SimpleNamespace(current_plan=ColonyPlan(colony_goals={'SecureSupplies': ColonyGoal(priority_class=3)}),
        inspect_native=AsyncMock(return_value={'success': True, 'cellsAccepted': 4}))


def facts():
    return dict(center=dict(x=10, z=10), cells=[dict(x=x, z=z, roofed=True, walkable=True,
        zone=False, occupied=False, storageEmpty=True) for x in (10, 11) for z in (10, 11)])


@pytest.mark.asyncio
async def test_covered_storage_has_exact_filters_and_native_dispatch_guard():
    rt = runtime()
    _, actions = await covered_storage(rt, facts(), [dict(defName='MedicineHerbal')])
    zone = Zone.model_validate(actions[0])
    assert zone.allow == ['MedicineHerbal'] and zone.preset == 'nothing' and zone.covered_empty
    args = rt.inspect_native.await_args.args[1]
    assert args['requireCoveredEmpty'] is True and args['dryRun'] is True
    assert zone.patches[0].cells() == [(10, 10), (11, 10), (10, 11), (11, 11)]


@pytest.mark.asyncio
@pytest.mark.parametrize('field,value', [('roofed', False), ('storageEmpty', False), ('zone', True),
    ('occupied', True), ('walkable', False), ('storageEmpty', None)])
async def test_storage_preserves_occupied_or_unknown_cells(field, value):
    rt, f = runtime(), facts()
    f['cells'][0][field] = value
    assert await covered_storage(rt, f, [dict(defName='MedicineHerbal')]) is None
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_storage_preserves_committed_player_geometry_and_walkways():
    rt = runtime()
    rt.current_plan.spec.reserved_walkways = [Rectangle(x=10, z=10, width=1, height=1)]
    rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    assert await covered_storage(rt, facts(), [dict(defName='MedicineHerbal')]) is None
    rt.current_plan.spec.reserved_walkways = []
    step = PlanStep(id='player', title='Reserved room', completion_criteria='Built', action=dict(
        kind='build_room_shell', bounds=dict(x=8, z=8, width=6, height=6), wall_def='Wall',
        door_def='Door', materials=['WoodLog'], entrance='south'))
    rt.current_plan.spec.steps = [step]
    rt.current_plan.progress['player'] = StepProgress()
    assert await covered_storage(rt, facts(), [dict(defName='MedicineHerbal')]) is None
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_storage_refusal_and_repeated_capacity_failures_do_not_expand_without_bound():
    rt = runtime()
    rt.inspect_native.return_value = {'success': True, 'cellsAccepted': 3}
    assert await covered_storage(rt, facts(), [dict(defName='MedicineHerbal')]) is None
    rt.inspect_native.return_value = {'success': True, 'cellsAccepted': 4}
    for n in range(3):
        step = PlanStep(id=f'store{n}', title='Storage', goal_id='SecureSupplies', completion_criteria='Native zone',
            action=dict(kind='create_zone', zone_type='stockpile', label=f'Supplies{n}',
                patches=[dict(x=n, z=0, width=1, height=1)]))
        rt.current_plan.spec.steps.append(step)
        rt.current_plan.progress[step.id] = StepProgress(state='complete')
    assert await covered_storage(rt, facts(), [dict(defName='MedicineHerbal')]) is None


@pytest.mark.asyncio
@pytest.mark.parametrize('refusal,creates', [('no_storage', True), ('work_disabled', False), ('not_reachable', False)])
async def test_supply_method_only_creates_storage_for_observed_storage_failure(refusal, creates):
    from test_colony_upkeep import facts as upkeep_facts, item
    from rimbot.colony_upkeep import upkeep_nodes, upkeep_method
    from rimbot.colony_skills import SkillBlocked
    rt = runtime()
    rt.context_token, rt.chat_revision = 'colony:load', 0
    f = facts() | upkeep_facts()
    f['upkeep']['items'] = [item(defName='MedicineHerbal')]
    upkeep_nodes(f, rt.current_plan.control)
    rt.inspect_native.side_effect = [dict(success=False, errorKind=refusal, error=refusal),
        dict(success=True, cellsAccepted=4)]
    people = [dict(thingId='Thing_Human1', health=dict(needsTend=False, bleeding=False),
        work={'types': [dict(name='Hauling', disabled=False, priority=1)]})]
    if creates:
        _, actions = await upkeep_method(rt, 'SecureSupplies', f, people)
        assert actions[0]['kind'] == 'create_zone'
    else:
        with pytest.raises(SkillBlocked):
            await upkeep_method(rt, 'SecureSupplies', f, people)
        assert rt.inspect_native.await_count == 1
@pytest.mark.asyncio
async def test_storeroom_requires_native_geometry_and_retains_single_room_limit():
    from rimbot.upkeep_storage import supply_storeroom
    from rimbot.colony_skills import SkillBlocked
    rt = runtime()
    f = dict(center=dict(x=10, z=10), definitions={k: dict(available=True, stuff='WoodLog') for k in ('Wall', 'Door')},
        cells=[dict(x=x, z=z, walkable=True, occupied=False, zone=False, supportsLight=True, storageEmpty=True)
               for x in range(9, 17) for z in range(9, 17)])
    async def inspect(tool, args):
        if tool == 'home/place_building':
            return dict(canPlace=True, rotations=[dict(accepted=True, blockingThings=[])])
        return dict(success=True, pawns=[dict(targets=[dict(nativeReachable=True, projectedReachable=True)])])
    rt.inspect_native.side_effect = inspect
    _, actions = await supply_storeroom(rt, f)
    step = PlanStep(id='room', title='Supply room', goal_id='SecureSupplies', completion_criteria='Native shell', action=actions[0])
    rt.current_plan.spec.steps.append(step)
    rt.current_plan.progress['room'] = StepProgress(state='waiting')
    with pytest.raises(SkillBlocked, match='no duplicate room'):
        await supply_storeroom(rt, f)
    rt.current_plan.spec.steps = []
    rt.inspect_native.side_effect = None
    rt.inspect_native.return_value = dict(canPlace=False)
    with pytest.raises(SkillBlocked, match='No safe accessible'):
        await supply_storeroom(rt, f)


@pytest.mark.asyncio
async def test_new_supply_room_waits_for_complete_native_roof_before_storage(monkeypatch):
    from rimbot.upkeep_storage import supply_storeroom
    rt = runtime()
    step = PlanStep(id='room', title='Supply room', goal_id='SecureSupplies', completion_criteria='Native shell', action=dict(
        kind='build_room_shell', bounds=dict(x=8, z=8, width=6, height=6), wall_def='Wall',
        door_def='Door', materials=['WoodLog'], entrance='south'))
    rt.current_plan.spec.steps = [step]
    rt.current_plan.progress['room'] = StepProgress(state='complete')
    monkeypatch.setattr('rimbot.shelter_handoff.verified_room', AsyncMock(return_value=None))
    assert await covered_storage(rt, facts(), [dict(defName='MedicineHerbal')]) is None
    assert rt.current_plan.colony_goals['SecureSupplies'].evidence['waiting_for_storage_roof']
    assert await supply_storeroom(rt, facts()) is None
    rt.inspect_native.assert_not_awaited()
