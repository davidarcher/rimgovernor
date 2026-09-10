from unittest.mock import AsyncMock

import pytest
from rimbot.colony_plan import ColonyGoal
from test_colony_controller import Replay


@pytest.mark.asyncio
async def test_butcher_uses_native_alternative_outside_occupied_kitchen():
    rt = Replay()
    rt.current_plan.colony_goals['EnsureFoodSupply'] = ColonyGoal(priority_class=2)
    rt.facts.update(armed=1, foodRunwayDays=0, farms=[{'edible': True, 'usableCells': 100}])
    rt.facts['definitions']['ButcherSpot'] = {'available': True, 'costs': {}}
    rt.facts['cells'] = [dict(x=x, z=10, walkable=True, occupied=False, zone=False, indoors=False)
                         for x in (10, 11)] + [dict(x=10, z=11, walkable=True, occupied=True, indoors=True)]
    rt.controller.skills.layout = AsyncMock(return_value={'room': {'x': 10, 'z': 10}})
    async def inspect(name, args):
        assert name == 'home/place_building'
        assert (args['x'], args['z']) != (10, 11)
        return {'canPlace': args['x'] == 11, 'rotations': [{'accepted': args['x'] == 11, 'blockingThings': [],
            'occupiedCells': [{'x': args['x'], 'z': args['z']}]}]}
    rt.inspect_native = AsyncMock(side_effect=inspect)
    # No batch capability: the same bounded search must retain its legacy fallback.
    rt.game.invoke = AsyncMock(side_effect=lambda name, args, **kw: inspect(name, args))
    method, actions = await rt.controller.skills.compile('EnsureFoodSupply', rt.facts, rt.people)
    assert method == 'butcher-spot'
    assert actions[0]['placements'] == [{'def_name': 'ButcherSpot', 'x': 11, 'z': 10}]
