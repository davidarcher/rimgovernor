from copy import deepcopy

import pytest

from rimbot.player_commands import AdoptRoom, apply_command
from rimbot.room_geometry import articulation_cells, irregular_aisle
from rimbot.shelter_handoff import player_shelter, verified_room
from rimbot.colony_skills import SkillBlocked
from test_room_adoption import fixture


def request(cells):
    return dict(kind='AdoptRoom',intent_id='ruin',bounds=dict(x=10,z=10,width=9,height=9),
        entrance='south',entrance_cell=dict(x=14,z=10),interior_cells=cells)


@pytest.mark.asyncio
async def test_exact_nonrectangular_native_room_is_reused_without_construction(tmp_path):
    rt,room=await fixture(tmp_path)
    room['cells']=[p for p in room['cells'] if p['x']<15 or p['z']<15]
    result=await apply_command(rt,request(room['cells']),token=rt.context_token,revision=rt.chat_revision)
    assert result['native_room']==room['id'] and not rt.current_plan.spec.steps
    _,_,shell=player_shelter(rt.current_plan)
    observed,interior=await verified_room(rt,shell)
    assert observed==room and len(interior)==40
    rt.game.invoke.assert_not_awaited()
    # A native rebuild cannot silently replace the accepted exact footprint.
    room['cells'].pop()
    with pytest.raises(SkillBlocked):await verified_room(rt,shell)
    rt.store.close()


@pytest.mark.asyncio
async def test_wrong_nonrectangular_native_shape_preserves_plan(tmp_path):
    rt,room=await fixture(tmp_path)
    before=deepcopy(rt.current_plan.model_dump())
    cells=room['cells'][:-1]
    with pytest.raises(ValueError,match='no longer matches'):
        await apply_command(rt,request(cells),token=rt.context_token,revision=rt.chat_revision)
    assert rt.current_plan.model_dump()==before
    rt.store.close()


@pytest.mark.parametrize('change',[
    lambda r:r.update(entrance_cell=None),
    lambda r:r['interior_cells'].append(r['interior_cells'][0]),
    lambda r:r['interior_cells'].append(dict(x=10,z=10)),
    lambda r:r.update(entrance_cell=dict(x=14,z=14)),
    lambda r:r.update(interior_cells=[dict(x=14,z=11),dict(x=16,z=15)]),
])
def test_invalid_shape_or_entrance_is_not_admitted(change):
    value=request([dict(x=x,z=z) for x in range(11,18) for z in range(11,18)])
    change(value)
    with pytest.raises(ValueError):AdoptRoom.model_validate(value)


def test_aisle_connects_entrance_and_protects_every_narrow_connector():
    cells={(x,z) for x in range(1,4) for z in range(1,4)}
    cells|={(x,z) for x in range(6,9) for z in range(1,4)}
    cells|={(4,2),(5,2)}
    shell=dict(entrance='west',entrance_cell=dict(x=0,z=2))
    aisle=irregular_aisle(shell,cells)
    assert {(4,2),(5,2)}<=aisle<=cells
    from rimbot.shell_site import connected_cells
    assert connected_cells((1,2),aisle)==aisle
    assert articulation_cells(cells,(1,2))<=aisle


def test_large_winding_geometry_does_not_depend_on_python_recursion_limit():
    cells={(x,0) for x in range(3844)}
    assert len(articulation_cells(cells,(0,0)))==3842
