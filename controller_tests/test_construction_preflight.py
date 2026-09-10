from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.colony_plan import ColonyPlan, PlanSpec
from rimbot.construction_preflight import preflight_construction, ConstructionRefusal


def footprint(args, **result):
    return dict(result, rotations=[{'rotation': args['rotation'],
        'occupiedCells': [{'x': args['x'], 'z': args['z']}]}])


def plan():
    return PlanSpec(steps=[dict(id='room',title='Room',completion_criteria='Built',action=dict(
        kind='build_room_shell',bounds=dict(x=10,z=10,width=4,height=4),wall_def='Wall',
        door_def='Door',materials=['WoodLog'],entrance='south'))])


@pytest.mark.asyncio
async def test_entire_shell_is_previewed_before_commit_and_shortage_is_not_rejection():
    game=SimpleNamespace(invoke=AsyncMock(side_effect=lambda name,args,**kw: footprint(args, success=True,canPlace=True,
        materials={'canBuildNow':False})))
    current=ColonyPlan()
    await preflight_construction(plan(),current,game)
    assert game.invoke.await_count==12 and current.revision==0
    for call in game.invoke.await_args_list:
        assert call.args[0]=='home/place_building' and call.args[1]['dryRun'] is True
        assert call.kwargs=={'allow_write':False}


@pytest.mark.asyncio
async def test_invented_definition_is_returned_with_step_and_native_evidence():
    spec=plan();spec.steps[0].action.door_def='Door_Wood'
    game=SimpleNamespace(invoke=AsyncMock(side_effect=ValueError('No definition matches Door_Wood')))
    with pytest.raises(ConstructionRefusal) as failure:
        await preflight_construction(spec,ColonyPlan(),game)
    assert failure.value.evidence['step_id']=='room'
    assert failure.value.evidence['definition']=='Door_Wood'
    assert 'No definition matches' in failure.value.evidence['native']['error']


@pytest.mark.asyncio
async def test_late_invalid_wall_rejects_and_unchanged_work_is_not_revalidated():
    replies=[{'canPlace':True}]*11+[{'canPlace':False}]
    game=SimpleNamespace(invoke=AsyncMock(side_effect=lambda name,args,**kw: footprint(args, **replies.pop(0))))
    spec=plan()
    with pytest.raises(ConstructionRefusal):await preflight_construction(spec,ColonyPlan(),game)
    assert game.invoke.await_count==12
    game.invoke.reset_mock()
    await preflight_construction(spec,ColonyPlan(spec=spec),game)
    game.invoke.assert_not_awaited()


@pytest.mark.asyncio
async def test_material_alternative_and_dependent_clearance():
    spec=plan();spec.steps[0].action.materials=['WoodLog','BlocksGranite']
    async def preview(name,args,**kwargs):return footprint(args, canPlace=args['stuff']=='BlocksGranite')
    game=SimpleNamespace(invoke=AsyncMock(side_effect=preview))
    await preflight_construction(spec,ColonyPlan(),game)
    assert game.invoke.await_count==24
    # A real dependency can clear the footprint before construction executes.
    data=spec.model_dump()
    data['steps'].insert(0,dict(id='clear',title='Clear',completion_criteria='Cleared',
        action=dict(kind='native_operation',tool='home/order',arguments={})))
    data['steps'][1]['after']=[{'step':'clear','when':'complete'}]
    game.invoke=AsyncMock(side_effect=lambda name,args,**kw: footprint(args,canPlace=False,success=True))
    await preflight_construction(PlanSpec.model_validate(data),ColonyPlan(),game)
