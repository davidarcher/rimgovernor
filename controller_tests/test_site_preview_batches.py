from copy import deepcopy
import json
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimgovernor.colony_plan import ColonyPlan, PlanSpec
from rimgovernor.development import placement
from rimgovernor.resource_accounting import validate_allocations


@pytest.mark.parametrize('first_safe', [0, 9, 31])
async def test_site_search_preserves_order_and_runtime_inspection_guard(first_safe):
    choices, counts = [], []
    for version in (0, 1):
        def result(p):
            return dict(success=True, canPlace=p['x'] >= first_safe, rotations=[dict(rotation='North', accepted=p['x'] >= first_safe,
                blockingThings=[], occupiedCells=[dict(x=p['x'], z=p['z'])])])
        async def inspect(tool, args):
            if tool == 'home/placement_previews':
                candidates = json.loads(args['placements'])
                assert len(candidates) <= 16
                return dict(success=True, version=1, results=[result(p) for p in candidates])
            assert args['dryRun'] is True
            return result(args)
        rt = SimpleNamespace(current_plan=ColonyPlan(), inspect_native=AsyncMock(side_effect=inspect),
            game=SimpleNamespace(bridge=SimpleNamespace(placement_preview_batch_version=version),
                invoke=AsyncMock(side_effect=AssertionError('Bypassed runtime inspection guard'))))
        facts = dict(definitions={'Bed': {'available': True}}, center={'x': 0, 'z': 2},
            cells=[dict(x=x, z=2, walkable=True) for x in range(40)])
        choices.append(await placement(rt, facts, 'Bed', radius=50))
        counts.append(rt.inspect_native.await_count)
    assert choices[0] == choices[1]
    assert choices[1]['placements'][0]['x'] == first_safe
    assert counts == ([1, 1] if first_safe == 0 else [first_safe+1, 1+(first_safe+15)//16])


async def test_shell_material_policy_and_reservations_match_individual_previews():
    step = dict(id='shell', title='Shell', completion_criteria='Native shelter', action=dict(
        kind='build_room_shell', bounds=dict(x=10,z=10,width=4,height=4),
        wall_def='Wall', door_def='Door', entrance='north', materials=['WoodLog','Steel']))
    costs, counts = [], []
    for version in (0, 1):
        def result(p):
            material = p['stuff']
            return dict(success=True, canPlace=True, costList=[dict(defName=material,count=5)],
                materials=dict(rows=[dict(defName=material,available=1000)]))
        async def invoke(tool, args, allow_write):
            assert not allow_write
            if tool == 'home/placement_previews':
                return dict(success=True, version=1, results=[result(p) for p in json.loads(args['placements'])])
            return result(args)
        game = SimpleNamespace(bridge=SimpleNamespace(placement_preview_batch_version=version),
                               invoke=AsyncMock(side_effect=invoke))
        spec = PlanSpec(steps=[deepcopy(step)])
        current = ColonyPlan(control={'resource_policy': {'WoodLog': {'spending': 'stop'}}})
        costs.append(await validate_allocations(spec, current, game))
        assert spec.steps[0].action.materials == ['Steel']
        counts.append(game.invoke.await_count)
    assert costs[0] == costs[1]
    assert counts == [36, 3]
