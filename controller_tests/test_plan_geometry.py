import pytest
from rimbot.colony_plan import PlanSpec
from rimbot.hands import GeometryConflict, validate_geometry


def layout(zone, reverse=False):
    steps = [dict(id='shelter', title='Shelter', completion_criteria='Built',
        action=dict(kind='build_room_shell', bounds=dict(x=138,z=117,width=12,height=12),
                    wall_def='Wall',door_def='Door',materials=['WoodLog'],entrance='south')),
        dict(id='stores',title='Stores',completion_criteria='Zone exists',
             action=dict(kind='create_zone',zone_type='stockpile',label='Stores',patches=[zone]))]
    return PlanSpec(steps=list(reversed(steps)) if reverse else steps)


@pytest.mark.parametrize('reverse', [False, True])
def test_indoor_stockpile_and_adjacent_zone_are_valid(reverse):
    validate_geometry(layout(dict(x=139,z=118,width=10,height=10), reverse))
    validate_geometry(layout(dict(x=150,z=117,width=8,height=8), reverse))


@pytest.mark.parametrize('reverse', [False, True])
def test_live_rejected_layout_identifies_conflicting_steps_and_cell(reverse):
    with pytest.raises(GeometryConflict) as raised:
        validate_geometry(layout(dict(x=148,z=125,width=8,height=8), reverse))
    evidence = raised.value.evidence
    assert {evidence['step_id'], evidence['conflicts_with']} == {'shelter','stores'}
    x, z = evidence['cell']['x'], evidence['cell']['z']
    assert 148 <= x <= 149 and 125 <= z <= 128
    assert x == 149 or z == 128


def test_walkway_conflict_is_preserved_with_coordinates():
    plan = layout(dict(x=150,z=117,width=8,height=8))
    plan = PlanSpec.model_validate(dict(plan.model_dump(),
        reserved_walkways=[dict(x=150,z=117,width=1,height=1)]))
    with pytest.raises(GeometryConflict) as raised:
        validate_geometry(plan)
    assert raised.value.evidence == dict(step_id='stores', conflicts_with='reserved walkway',
                                        cell=dict(x=150,z=117))


def furnishing_after_zone(state='complete'):
    from rimbot.colony_plan import ColonyPlan, StepProgress
    zone=dict(id='stores',title='Stores',completion_criteria='Zone exists',action=dict(
        kind='create_zone',zone_type='stockpile',label='Stores',patches=[dict(x=10,z=10,width=3,height=3)]))
    bed=dict(id='bed',title='Player furniture edit',completion_criteria='Placed',action=dict(
        kind='place_buildings',placements=[dict(def_name='SleepingSpot',x=11,z=11)]))
    current=ColonyPlan(spec=PlanSpec(steps=[zone]),progress={'stores':StepProgress(state=state)})
    return current,PlanSpec(steps=[zone,bed])


def test_completed_zone_does_not_own_later_native_furniture_cells():
    current,spec=furnishing_after_zone()
    before=current.model_dump()
    validate_geometry(spec,current=current)
    assert current.model_dump()==before


@pytest.mark.parametrize('state',['pending','executing','waiting','blocked'])
def test_unfinished_footprints_still_conflict(state):
    current,spec=furnishing_after_zone(state)
    with pytest.raises(GeometryConflict):validate_geometry(spec,current=current)


def test_changed_completed_action_is_not_exempt_from_geometry_validation():
    current,spec=furnishing_after_zone()
    spec.steps[0].action.patches[0].width=4
    with pytest.raises(GeometryConflict):validate_geometry(spec,current=current)


def test_completed_history_does_not_remove_explicit_walkway_constraints():
    current,spec=furnishing_after_zone()
    spec=PlanSpec.model_validate(dict(spec.model_dump(),reserved_walkways=[dict(x=11,z=11,width=1,height=1)]))
    with pytest.raises(GeometryConflict,match='reserved walkway'):validate_geometry(spec,current=current)


@pytest.mark.asyncio
async def test_native_placement_still_refuses_an_occupied_completed_footprint():
    from types import SimpleNamespace
    from unittest.mock import AsyncMock
    from rimbot.construction_preflight import preflight_construction,ConstructionRefusal
    current,spec=furnishing_after_zone()
    validate_geometry(spec,current=current)
    game=SimpleNamespace(invoke=AsyncMock(return_value={'canPlace':False,'success':True,
        'rotations':[{'reason':'Occupied native building'}]}))
    with pytest.raises(ConstructionRefusal):await preflight_construction(spec,current,game)
    game.invoke.assert_awaited_once()
    assert game.invoke.await_args.args[1]['dryRun'] is True
