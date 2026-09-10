from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
import pytest
from rimgovernor.colony_plan import ColonyPlan, PlanSpec, StepProgress
from rimgovernor.construction_preflight import preflight_construction, ConstructionRefusal
from rimgovernor.hands import Hands, Blocked
from rimgovernor.spatial import GeometryConflict, validate_geometry, entrance_cells, native_footprint
from test_construction_preflight import plan, footprint


def buildings(identity, *positions):
    return dict(id=identity, title=identity, completion_criteria='Built', action=dict(
        kind='place_buildings', placements=[dict(def_name='Bed', x=x, z=z) for x, z in positions]))


def combined(*steps):
    return PlanSpec.model_validate(dict(steps=[*plan().model_dump()['steps'], *steps]))


@pytest.mark.parametrize('entrance,inside,outside', [
    ('south', (12, 11), (12, 9)), ('north', (12, 12), (12, 14)),
    ('east', (12, 12), (14, 12)), ('west', (11, 12), (9, 12))])
def test_every_entrance_protects_both_approaches_in_either_plan_order(entrance, inside, outside):
    for point in (inside, outside):
        for reverse in (False, True):
            spec = combined(buildings('bed', point))
            spec.steps[0].action.entrance = entrance
            assert point in entrance_cells(spec.steps[0].action)
            if reverse:
                spec.steps.reverse()
            with pytest.raises(GeometryConflict, match='entrance'):
                validate_geometry(spec)


def test_farm_inside_room_and_nested_room_refused_but_indoor_storage_allowed():
    zone = dict(id='zone', title='Zone', completion_criteria='Exists', action=dict(
        kind='create_zone', zone_type='growing', crop='Plant_Rice', label='Food',
        patches=[dict(x=11, z=11, width=1, height=1)]))
    with pytest.raises(GeometryConflict):
        validate_geometry(combined(zone))
    zone['action']['zone_type'] = 'stockpile'
    validate_geometry(combined(zone))
    spec = plan()
    outer = spec.steps[0].model_copy(deep=True)
    outer.id = 'outer'
    outer.action.bounds.x = outer.action.bounds.z = 5
    outer.action.bounds.width = outer.action.bounds.height = 20
    spec.steps.append(outer)
    with pytest.raises(GeometryConflict):
        validate_geometry(spec)


@pytest.mark.asyncio
@pytest.mark.parametrize('same_step', [False, True])
async def test_native_multicell_overlap_rejected_even_with_distinct_anchors(same_step):
    steps = [buildings('beds', (20, 20), (21, 20))] if same_step else [
        buildings('first', (20, 20)), buildings('second', (21, 20))]
    spec = PlanSpec.model_validate(dict(steps=steps))
    async def preview(name, args, **kw):
        result = footprint(args, canPlace=True)
        result['rotations'][0]['occupiedCells'].append(dict(x=args['x'], z=21))
        result['rotations'][0]['occupiedCells'].append(dict(x=args['x']+1, z=21))
        return result
    game = SimpleNamespace(invoke=AsyncMock(side_effect=preview))
    with pytest.raises(GeometryConflict):
        await preflight_construction(spec, ColonyPlan(), game)
    assert all(c.kwargs == {'allow_write': False} for c in game.invoke.await_args_list)


@pytest.mark.asyncio
async def test_new_zone_rechecks_retained_building_footprint_without_requiring_placeable():
    current = ColonyPlan(spec=PlanSpec.model_validate(dict(steps=[buildings('bed', (20, 20))])))
    spec = current.spec.model_copy(deep=True)
    spec.steps += PlanSpec.model_validate(dict(steps=[dict(id='field', title='Field',
        completion_criteria='Exists', action=dict(kind='create_zone', zone_type='growing',
        crop='Plant_Rice', label='Field', patches=[dict(x=20, z=21, width=1, height=1)]))])).steps
    async def preview(name, args, **kw):
        if name=='home/spatial_access':return dict(success=True,accepted=True,pawnCount=1)
        result = footprint(args, canPlace=False)
        result['rotations'][0]['occupiedCells'].append(dict(x=20, z=21))
        return result
    game = SimpleNamespace(invoke=AsyncMock(side_effect=preview))
    with pytest.raises(GeometryConflict):
        await preflight_construction(spec, current, game)
    spec.steps[-1].action.patches[0].x = 30
    await preflight_construction(spec, current, game)


@pytest.mark.asyncio
async def test_unknown_footprint_refuses_admission():
    with pytest.raises(ConstructionRefusal) as error:
        await preflight_construction(plan(), ColonyPlan(),
            SimpleNamespace(invoke=AsyncMock(return_value={'canPlace': True})))
    assert 'footprint' in error.value.evidence['native']['error']


@pytest.mark.asyncio
async def test_dispatch_rechecks_native_footprint_at_entrance_before_any_write():
    spec = combined(buildings('bed', (11, 11)))
    current = ColonyPlan(spec=spec, progress={s.id: StepProgress() for s in spec.steps})
    p = spec.steps[-1].action.placements[0]
    preview = footprint(dict(x=p.x, z=p.z, rotation=p.rotation), canPlace=True)
    preview['rotations'][0]['occupiedCells'].append(dict(x=12, z=11))
    rt = SimpleNamespace(current_plan=current, context_token='load', chat_revision=0,
        handled_revision=0, mode='automate', persist=Mock(),
        game=SimpleNamespace(query=AsyncMock(return_value={'buildings': []})),
        inspect_native=AsyncMock(return_value=preview), native=AsyncMock())
    with pytest.raises(Blocked) as error:
        await Hands().place(rt, p, current.progress['bed'], '0', 0, 'load', 0)
    assert error.value.failure.code == 'spatial_conflict'
    rt.native.assert_not_awaited()
    assert not current.progress['bed'].issued


@pytest.mark.parametrize('rows', [[], [{'occupiedCells': []}], [
    {'rotation': 'south', 'occupiedCells': [{'x': 20, 'z': 20}]}], [
    {'occupiedCells': [{'x': 20, 'z': 21}]}]])
def test_footprint_requires_nonempty_matching_rotation_and_anchor(rows):
    p = PlanSpec.model_validate(dict(steps=[buildings('bed', (20, 20))])).steps[0].action.placements[0]
    with pytest.raises(ValueError):
        native_footprint({'rotations': rows}, p)
