from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimgovernor.colony_plan import ColonyPlan, PlanStep, StepProgress
from rimgovernor.construction_preflight import ConstructionRefusal, preflight_construction
from rimgovernor.shell_site import ShellSiteRefusal
from rimgovernor.spatial import projected_obstruction
from test_construction_preflight import native_reply, plan


def fixture():
    current = ColonyPlan(spec=plan(), progress={'room': StepProgress(state='complete')})
    spec = current.spec.model_copy(deep=True)
    spec.steps.append(PlanStep(id='custom', title='Custom furniture', completion_criteria='Built',
        action=dict(kind='place_buildings', placements=[dict(def_name='CustomPartition', x=12, z=8)])))
    return current, spec


def game(passability='Impassable', is_door=False):
    async def invoke(name, args, **kwargs):
        assert kwargs == {'allow_write': False}
        result = native_reply(name, args, success=True, canPlace=True)
        if name == 'home/get_cells_plus':
            for cell in result['cells']:
                if (cell['x'], cell['z']) in {(11, 9), (13, 9)}:
                    cell.update(walkable=False, passable=False)
        if name == 'home/place_building' and args['defName'] == 'CustomPartition':
            result.update(passability=passability, isDoor=is_door)
            result['rotations'][0]['occupiedCells'] = [dict(x=x, z=8) for x in (11, 12, 13)]
        return result
    return SimpleNamespace(invoke=AsyncMock(side_effect=invoke))


@pytest.mark.asyncio
async def test_custom_furniture_cannot_seal_completed_room_before_admission():
    current, spec = fixture()
    before = current.model_dump()
    with pytest.raises(ShellSiteRefusal, match='no observed route'):
        await preflight_construction(spec, current, game())
    assert current.model_dump() == before


@pytest.mark.asyncio
@pytest.mark.parametrize('passability,is_door', [('Standable', False), ('PassThroughOnly', False), ('Impassable', True)])
async def test_native_traversable_definitions_do_not_project_solid_walls(passability, is_door):
    current, spec = fixture()
    await preflight_construction(spec, current, game(passability, is_door))


@pytest.mark.asyncio
@pytest.mark.parametrize('passability,is_door', [(None, False), ('Unknown', False), ('Impassable', None)])
async def test_missing_completed_definition_metadata_refuses_admission(passability, is_door):
    current, spec = fixture()
    with pytest.raises(ConstructionRefusal):
        await preflight_construction(spec, current, game(passability, is_door))


@pytest.mark.asyncio
async def test_dispatch_refresh_checks_unchanged_plan_after_live_obstruction():
    current, spec = fixture()
    await preflight_construction(spec, current, game('Standable'))
    current.spec = spec
    with pytest.raises(ShellSiteRefusal):
        await preflight_construction(spec, current, game(), refresh=True)


def test_projection_requires_exact_native_footprint():
    _, spec = fixture()
    placement = spec.steps[-1].action.placements[0]
    with pytest.raises(ValueError, match='footprint'):
        projected_obstruction(dict(passability='Impassable', isDoor=False,
            rotations=[dict(rotation=placement.rotation, occupiedCells=[])]), placement)
