from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
import pytest
from rimbot.colony_plan import ColonyPlan, StepProgress
from rimbot.construction_preflight import preflight_construction
from rimbot.hands import Hands
from rimbot.shell_site import ShellSiteRefusal, validate_shell_zones, validate_shell_access
from rimbot.spatial import room_entrance
from test_construction_preflight import plan, native_reply, footprint


def census(*zones):
    return dict(success=True, zoneCount=len(zones), zoneCountOnMap=len(zones),
                zones=list(zones), totals={'gridSweepFailed': False})


def zone(kind='Zone_Growing', points=((11, 11),)):
    cells = [dict(x=x, z=z) for x, z in points]
    return dict(id=1, label='Existing zone', type=kind, cells=cells, gridCells=deepcopy(cells),
        listedCellCount=len(cells), gridCellCount=len(cells), cellsNotListed=0,
        gridCellsNotListed=0, consistent=True)


def test_shell_cannot_enclose_farm_but_can_enclose_stockpile():
    shell = plan().steps[0].action
    with pytest.raises(ShellSiteRefusal) as error:
        validate_shell_zones('room', shell, census(zone()))
    assert error.value.code == 'existing_zone'
    assert error.value.evidence['cell'] == dict(x=11, z=11)
    validate_shell_zones('room', shell, census(zone('Zone_Stockpile')))
    with pytest.raises(ShellSiteRefusal):
        validate_shell_zones('room', shell, census(zone('Zone_Stockpile', ((10, 11),))))
    validate_shell_zones('room', shell, census(zone(points=((30, 30),))))


@pytest.mark.parametrize('change', [
    lambda r: r.pop('zoneCountOnMap'),
    lambda r: r.update(zoneCountOnMap=2),
    lambda r: r['totals'].update(gridSweepFailed=True),
    lambda r: r['zones'][0].update(gridCellsNotListed=1),
    lambda r: r['zones'][0].update(cellsNotListed=1),
    lambda r: r['zones'][0].update(gridCellCount=2),
    lambda r: r['zones'][0].update(consistent=False),
    lambda r: r['zones'][0].update(gridCells=[dict(x=12, z=11)]),
    lambda r: r['zones'][0].update(gridCells=[dict(x=11, z=11), dict(x=11, z=11)]),
])
def test_unknown_truncated_or_disagreeing_zone_geometry_never_certifies_site(change):
    result = census(zone('Zone_Stockpile'))
    change(result)
    with pytest.raises(ShellSiteRefusal) as error:
        validate_shell_zones('room', plan().steps[0].action, result)
    assert error.value.code == 'incomplete_zone_geometry'


def test_native_growing_subclass_is_identified_by_growing_block():
    growing = zone('CustomGrowingZone')
    growing['plantDefExplicitlySet'] = False
    with pytest.raises(ShellSiteRefusal):
        validate_shell_zones('room', plan().steps[0].action, census(growing))


@pytest.mark.asyncio
@pytest.mark.parametrize('entrance', ['north', 'east', 'south', 'west'])
async def test_native_access_checks_three_exact_cells_and_both_approaches(entrance):
    shell = plan().steps[0].action
    shell.entrance = entrance
    read = AsyncMock(side_effect=lambda name, args: native_reply(name, args))
    await validate_shell_access('room', shell, read)
    args = read.await_args.args[1]
    assert args['width'] * args['height'] == 3 and args['sparse'] is False
    (x, z), (dx, dz) = room_entrance(shell)
    for point in ((x-dx, z-dz), (x+dx, z+dz)):
        async def blocked(name, args):
            result = native_reply(name, args)
            next(c for c in result['cells'] if (c['x'], c['z']) == point)['walkable'] = False
            return result
        with pytest.raises(ShellSiteRefusal) as error:
            await validate_shell_access('room', shell, blocked)
        assert error.value.code == 'entrance_unavailable'


@pytest.mark.asyncio
@pytest.mark.parametrize('change', [
    lambda r: r.update(cells=r['cells'][1:]),
    lambda r: r.update(fieldsApplied=['walkable', 'passable']),
    lambda r: r.update(cellsOmitted=1),
    lambda r: r['cells'][0].update(walkable=None),
    lambda r: r['cells'][0].update(passable=None),
    lambda r: r['cells'][0].update(fogged=True),
    lambda r: r['cells'][0].update(fogged=None),
])
async def test_missing_selected_fields_fog_or_unknown_access_refuses(change):
    async def read(name, args):
        result = native_reply(name, args)
        change(result)
        return result
    with pytest.raises(ShellSiteRefusal):
        await validate_shell_access('room', plan().steps[0].action, read)


@pytest.mark.asyncio
@pytest.mark.parametrize('source', ['PLAYER', 'AUTOPILOT'])
async def test_existing_native_farm_refuses_admission_without_changing_plan(source):
    spec = plan()
    spec.steps[0].source = source
    current = ColonyPlan()
    before = current.model_dump()
    async def read(name, args, **kw):
        assert kw == {'allow_write': False}
        if name == 'home/list_zones':
            assert 'radius' not in args and args['includeContents'] is False
            return census(zone())
        return native_reply(name, args, canPlace=True)
    with pytest.raises(ShellSiteRefusal):
        await preflight_construction(spec, current, SimpleNamespace(invoke=AsyncMock(side_effect=read)))
    assert current.model_dump() == before


def runtime():
    spec = plan()
    current = ColonyPlan(spec=spec, progress={'room': StepProgress()})
    state = dict(zones=census(), built=[])
    async def query(name, **args):
        if name == 'home/list_buildings':
            return {'buildings': [b for b in state['built'] if b['position'] == dict(x=args['x'], z=args['z'])]}
        if name == 'home/list_zones':
            return state['zones']
        return native_reply(name, args)
    async def native(name, args, **kw):
        state['built'].append(dict(thingId='Thing_Door', defName=args['defName'], stuff='WoodLog',
            status='blueprint', position=dict(x=args['x'], z=args['z'])))
        return {'receipt': {'outcome': 'placed'}}
    rt = SimpleNamespace(current_plan=current, mode='automate', context_token='load',
        chat_revision=0, handled_revision=0, persist=Mock(), note=Mock(), signal=Mock(),
        game=SimpleNamespace(query=AsyncMock(side_effect=query)),
        inspect_native=AsyncMock(side_effect=lambda name, args: footprint(args, canPlace=True)),
        native=AsyncMock(side_effect=native))
    return rt, state


@pytest.mark.asyncio
async def test_new_farm_between_dispatch_batches_blocks_remaining_shell_and_preserves_receipt():
    rt, state = runtime()
    await Hands().advance(rt, max_operations=1)
    assert rt.native.await_count == 1
    receipt = deepcopy(rt.current_plan.progress['room'].issued)
    assert receipt['0']['confirmed'] is True
    # The player designates an interior crop patch after the first door order.
    state['zones'] = census(zone())
    rt.current_plan = ColonyPlan.model_validate_json(rt.current_plan.model_dump_json())
    await Hands().advance(rt)
    assert rt.native.await_count == 1
    progress = rt.current_plan.progress['room']
    assert progress.issued == receipt and progress.state == 'blocked'
    assert progress.failure.code == 'existing_zone'


@pytest.mark.asyncio
async def test_direction_change_during_access_read_prevents_write():
    rt, _ = runtime()
    original = rt.game.query.side_effect
    async def changed(name, **args):
        result = await original(name, **args)
        if name == 'home/get_cells_plus':
            rt.chat_revision += 1
        return result
    rt.game.query.side_effect = changed
    await Hands().advance(rt)
    rt.native.assert_not_awaited()
    assert not rt.current_plan.progress['room'].issued
