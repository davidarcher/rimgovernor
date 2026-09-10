from copy import deepcopy
from unittest.mock import AsyncMock
import pytest
from rimbot.colony_plan import ColonyPlan
from rimbot.hands import Hands
from rimbot.construction_preflight import preflight_construction
from rimbot.shell_site import ShellSiteRefusal, validate_shell_connectivity
from rimbot.spatial import room_entrance
from test_construction_preflight import plan, native_reply
from test_shell_site import runtime


def reader(blocked=(), unknown=(), size=(250, 250)):
    async def read(name, args):
        assert name == 'home/get_cells_plus'
        assert args['width']*args['height'] <= 1024
        assert 0 <= args['x'] < args['x']+args['width'] <= size[0]
        assert 0 <= args['z'] < args['z']+args['height'] <= size[1]
        result = native_reply(name, args)
        result['mapSize'] = dict(x=size[0], z=size[1])
        for c in result['cells']:
            point = (c['x'], c['z'])
            if point in blocked:
                c.update(walkable=False, passable=False)
            if point in unknown:
                c['walkable'] = None
        return result
    return AsyncMock(side_effect=read)


@pytest.mark.asyncio
@pytest.mark.parametrize('entrance', ['south', 'west', 'east', 'north'])
async def test_open_site_connects_but_walkable_entrance_in_sealed_pocket_refuses(entrance):
    spec = plan()
    shell = spec.steps[0].action
    shell.entrance = entrance
    good = await validate_shell_connectivity('room', shell, reader(), spec)
    assert good['interior_cells'] == 4 and good['margin'] == 3
    (x, z), (dx, dz) = room_entrance(shell)
    outside = (x+dx, z+dz)
    pocket = {(outside[0]+dx, outside[1]+dz),
              (outside[0]+dz, outside[1]+dx), (outside[0]-dz, outside[1]-dx)}
    with pytest.raises(ShellSiteRefusal) as error:
        await validate_shell_connectivity('room', shell, reader(blocked=pocket), spec)
    assert error.value.code == 'disconnected_entrance'


@pytest.mark.asyncio
async def test_disconnected_usable_interior_is_not_furnished_as_one_accessible_room():
    spec = plan()
    shell = spec.steps[0].action
    shell.bounds.width = shell.bounds.height = 6
    divider = {(12, z) for z in range(11, 15)}
    with pytest.raises(ShellSiteRefusal) as error:
        await validate_shell_connectivity('room', shell, reader(blocked=divider), spec)
    assert error.value.code == 'disconnected_interior'
    # A gap around the divider restores four-connected access.
    await validate_shell_connectivity('room', shell, reader(blocked=divider-{(12, 14)}), spec)


@pytest.mark.asyncio
async def test_pending_neighbor_shell_cannot_close_the_only_local_exit():
    spec = plan()
    shell = spec.steps[0].action
    read = reader(blocked={(11, 9), (13, 9)})
    await validate_shell_connectivity('room', shell, read, spec)
    neighbor = spec.steps[0].model_copy(deep=True)
    neighbor.id = 'neighbor'
    neighbor.action.bounds.x, neighbor.action.bounds.z = 8, 3
    neighbor.action.bounds.width, neighbor.action.bounds.height = 9, 6
    neighbor.action.entrance = 'west'
    spec.steps.append(neighbor)
    with pytest.raises(ShellSiteRefusal) as error:
        await validate_shell_connectivity('room', shell, read, spec)
    assert error.value.code == 'disconnected_entrance'


@pytest.mark.asyncio
async def test_admitting_neighbor_revalidates_already_completed_room_exit():
    from types import SimpleNamespace
    from rimbot.colony_plan import StepProgress
    current = ColonyPlan(spec=plan(), progress={'room': StepProgress(state='complete')})
    spec = current.spec.model_copy(deep=True)
    neighbor = spec.steps[0].model_copy(deep=True)
    neighbor.id = 'neighbor'
    neighbor.action.bounds.x, neighbor.action.bounds.z = 8, 3
    neighbor.action.bounds.width, neighbor.action.bounds.height = 9, 6
    neighbor.action.entrance = 'west'
    spec.steps.append(neighbor)
    cells = reader(blocked={(11, 9), (13, 9)})
    async def invoke(name, args, **kw):
        assert kw == {'allow_write': False}
        if name == 'home/get_cells_plus':
            return await cells(name, args)
        return native_reply(name, args, canPlace=True)
    before = current.model_dump()
    with pytest.raises(ShellSiteRefusal) as error:
        await preflight_construction(spec, current, SimpleNamespace(invoke=AsyncMock(side_effect=invoke)))
    assert error.value.code == 'disconnected_entrance'
    assert error.value.evidence['step_id'] == 'room'
    assert current.model_dump() == before


@pytest.mark.asyncio
async def test_unknown_interior_refuses_but_unknown_exterior_can_be_routed_around():
    spec = plan()
    with pytest.raises(ShellSiteRefusal) as error:
        await validate_shell_connectivity('room', spec.steps[0].action, reader(unknown={(11, 11)}), spec)
    assert error.value.code == 'incomplete_connectivity'
    await validate_shell_connectivity('room', spec.steps[0].action, reader(unknown={(9, 9)}), spec)


@pytest.mark.asyncio
async def test_large_room_uses_bounded_native_rectangles():
    spec = plan()
    shell = spec.steps[0].action
    shell.bounds.width = shell.bounds.height = 64
    read = reader()
    result = await validate_shell_connectivity('room', shell, read, spec)
    assert read.await_count == 10 and result['observation_cells'] == 70*70
    assert result['interior_cells'] == 62*62


@pytest.mark.asyncio
async def test_scan_clips_to_observed_map_without_treating_map_edge_as_exit():
    spec = plan()
    shell = spec.steps[0].action
    shell.bounds.x = shell.bounds.z = 1
    await validate_shell_connectivity('room', shell, reader(size=(12, 12)), spec)
    # The outside door cell at z=0 cannot leave the map, and both side exits close.
    with pytest.raises(ShellSiteRefusal) as error:
        await validate_shell_connectivity('room', shell, reader(blocked={(2, 0), (4, 0)}, size=(12, 12)), spec)
    assert error.value.code == 'disconnected_entrance'


@pytest.mark.asyncio
@pytest.mark.parametrize('change', [
    lambda r: r['cells'].pop(),
    lambda r: r.update(cellsOmitted=1),
    lambda r: r.update(fieldsApplied=['walkable']),
    lambda r: r['mapSize'].update(x=249),
])
async def test_incomplete_or_changed_census_never_certifies_connectivity(change):
    spec = plan()
    good = reader()
    async def read(name, args):
        result = await good(name, args)
        if args['width']*args['height'] > 1:
            change(result)
        return result
    with pytest.raises(ShellSiteRefusal) as error:
        await validate_shell_connectivity('room', spec.steps[0].action, read, spec)
    assert error.value.code == 'incomplete_connectivity'


@pytest.mark.asyncio
async def test_new_outer_obstruction_after_partial_dispatch_preserves_issued_work():
    rt, _ = runtime()
    await Hands().advance(rt, max_operations=1)
    receipt = deepcopy(rt.current_plan.progress['room'].issued)
    rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    original = rt.game.query.side_effect
    async def changed(name, **args):
        result = await original(name, **args)
        if name == 'home/get_cells_plus':
            for c in result['cells']:
                if (c['x'], c['z']) in {(11, 9), (13, 9), (12, 8)}:
                    c.update(walkable=False, passable=False)
        return result
    rt.game.query.side_effect = changed
    await Hands().advance(rt)
    progress = rt.current_plan.progress['room']
    assert rt.native.await_count == 1 and progress.issued == receipt
    assert progress.failure.code == 'disconnected_entrance'
