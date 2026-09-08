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
