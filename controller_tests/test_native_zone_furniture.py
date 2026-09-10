from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
import pytest
from rimgovernor.colony_plan import ColonyPlan, PlanSpec, Placement, StepProgress, Zone
from rimgovernor.hands import Hands, Blocked


def runtime(legal=True):
    async def query(tool, **args):
        if tool == 'home/list_buildings': return {'buildings': []}
        return {'zones': [{'label': 'Food', 'gridCells': [{'x': 10, 'z': 10}], 'gridCellCount': 1}]}
    return SimpleNamespace(current_plan=ColonyPlan(), context_token='load', chat_revision=0,
        handled_revision=0, mode='automate', persist=Mock(), game=SimpleNamespace(query=AsyncMock(side_effect=query)),
        inspect_native=AsyncMock(return_value={'canPlace': legal, 'madeFromStuff': False,
            'rotations': [{'rotation':'north','occupiedCells': [{'x': 10, 'z': 10}]}]}),
        native=AsyncMock(return_value={'receipt': {'outcome': 'placed'}}))


@pytest.mark.asyncio
async def test_native_legal_furniture_can_share_stockpile_cells():
    rt=runtime()
    result=await Hands().place(rt, Placement(x=10,z=10,def_name='SleepingSpot'), StepProgress(), '0', 0, 'load', 0)
    assert result['outcome']=='placed'
    rt.native.assert_awaited_once()


@pytest.mark.asyncio
async def test_native_refused_furniture_never_issues():
    rt=runtime(False)
    rt.current_plan=ColonyPlan(spec=PlanSpec(steps=[dict(id='bed',title='Bed',
        completion_criteria='Built',action=dict(kind='place_buildings',placements=[
            dict(x=10,z=10,def_name='SleepingSpot')]))]),progress={'bed':StepProgress()})
    rt.batch=SimpleNamespace(summary=SimpleNamespace(end_tick=100))
    with pytest.raises(Blocked) as error:
        await Hands().place(rt, Placement(x=10,z=10,def_name='SleepingSpot'), rt.current_plan.progress['bed'], '0', 0, 'load', 0)
    assert error.value.failure.code=='construction_unavailable'
    rt.native.assert_not_awaited()


@pytest.mark.asyncio
async def test_zone_replacement_still_requires_exact_existing_geometry():
    rt=runtime()
    action=Zone(zone_type='stockpile',label='Food',patches=[dict(x=10,z=10,width=2,height=1)])
    with pytest.raises(Blocked) as error:
        await Hands().zone(rt, action, StepProgress(), '0', 0, 'load', 0)
    assert error.value.failure.code=='zone_conflict'
    rt.native.assert_not_awaited()
