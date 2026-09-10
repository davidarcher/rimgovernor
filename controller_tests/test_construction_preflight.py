from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.colony_plan import ColonyPlan, PlanSpec
from rimbot.construction_preflight import preflight_construction, ConstructionRefusal


def footprint(args, **result):
    return dict(result, rotations=[{'rotation': args['rotation'],
        'occupiedCells': [{'x': args['x'], 'z': args['z']}]}])


def native_reply(name, args, **result):
    if name == 'home/list_zones':
        return dict(success=True, zoneCount=0, zoneCountOnMap=0,
            zones=[], totals={'gridSweepFailed': False})
    if name == 'home/get_cells_plus':
        cells = [dict(x=x, z=z, walkable=True, passable=True)
                 for x in range(args['x'], args['x']+args['width'])
                 for z in range(args['z'], args['z']+args['height'])]
        return dict(success=True, cells=cells, cellCount=len(cells), cellsOmitted=0,
                    fieldsApplied=args['fields'].split(','), mapSize={'x':250,'z':250})
    return footprint(args, **result) if name == 'home/place_building' else {'success': True}


def plan():
    return PlanSpec(steps=[dict(id='room',title='Room',completion_criteria='Built',action=dict(
        kind='build_room_shell',bounds=dict(x=10,z=10,width=4,height=4),wall_def='Wall',
        door_def='Door',materials=['WoodLog'],entrance='south'))])


@pytest.mark.asyncio
async def test_entire_shell_is_previewed_before_commit_and_shortage_is_not_rejection():
    game=SimpleNamespace(invoke=AsyncMock(side_effect=lambda name,args,**kw: native_reply(name,args, success=True,canPlace=True,
        materials={'canBuildNow':False})))
    current=ColonyPlan()
    await preflight_construction(plan(),current,game)
    assert game.invoke.await_count==16 and current.revision==0
    for call in game.invoke.await_args_list:
        assert call.args[0] in ('home/place_building','home/list_zones','home/get_cells_plus')
        if call.args[0]=='home/place_building': assert call.args[1]['dryRun'] is True
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
    game=SimpleNamespace(invoke=AsyncMock(side_effect=lambda name,args,**kw: native_reply(name,args, **replies.pop(0))))
    spec=plan()
    with pytest.raises(ConstructionRefusal):await preflight_construction(spec,ColonyPlan(),game)
    assert game.invoke.await_count==12
    game.invoke.reset_mock()
    await preflight_construction(spec,ColonyPlan(spec=spec),game)
    game.invoke.assert_not_awaited()


@pytest.mark.asyncio
async def test_material_alternative_and_dependent_clearance():
    spec=plan();spec.steps[0].action.materials=['WoodLog','BlocksGranite']
    async def preview(name,args,**kwargs):return native_reply(name,args, canPlace=args.get('stuff')=='BlocksGranite')
    game=SimpleNamespace(invoke=AsyncMock(side_effect=preview))
    await preflight_construction(spec,ColonyPlan(),game)
    assert game.invoke.await_count==28
    # A real dependency can clear the footprint before construction executes.
    data=spec.model_dump()
    data['steps'].insert(0,dict(id='clear',title='Clear',completion_criteria='Cleared',
        action=dict(kind='native_operation',tool='home/order',arguments={})))
    data['steps'][1]['after']=[{'step':'clear','when':'complete'}]
    game.invoke=AsyncMock(side_effect=lambda name,args,**kw: native_reply(name,args,canPlace=False,success=True))
    await preflight_construction(PlanSpec.model_validate(data),ColonyPlan(),game)
