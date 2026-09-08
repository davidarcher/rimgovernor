from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock

from jsonschema import Draft202012Validator
import pytest

from rimbot.colony_plan import CommitSteps, Decision, RoomShell, Zone
from rimbot.consultation import structured_tool
from rimbot.execution_contracts import ExecutionContracts
from rimbot.hands import Hands
from rimbot.model import inference_tools


def action():
    return {'kind': 'create_zone', 'zone_type': 'growing', 'label': 'Food',
            'patches': [{'x': 1, 'z': 1, 'width': 3, 'height': 3}]}


@pytest.mark.parametrize('model,name', [(CommitSteps, 'commit_steps'), (Decision, 'commit_plan')])
def test_advertised_zone_crop_matches_runtime_requirement(model, name):
    tools = [structured_tool(name, 'Commit', model.model_json_schema())]
    contracts = ExecutionContracts(tools)
    # Native discovery refresh must preserve the semantic zone constraint.
    contracts.expose('home/order', {'type': 'object', 'properties': {}})
    schema = tools[0]['function']['parameters']
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    zone = action()
    step = {'id': 'food', 'title': 'Food', 'completion_criteria': 'Native zone', 'action': zone}
    request = ({'expected_revision': 0, 'reason': 'Food', 'steps': [step]} if model is CommitSteps else
        {'expected_revision': 0, 'disposition': 'revise', 'assessment': 'Food',
         'rationale': 'Food', 'reply': 'Food', 'plan': {'steps': [step]}})
    for missing in (True, False):
        if not missing:
            zone['crop'] = ''
        assert not validator.is_valid(request)
        with pytest.raises(ValueError, match='sowable crop'):
            model.model_validate(request)
    zone['crop'] = 'ModdedObservedSowable'
    original = deepcopy(request)
    validator.validate(request)
    assert model.model_validate(request)
    assert request == original
    # No invented table: a nonempty value is still subject to native legality.
    zone['crop'] = 'UnknownToNative'
    validator.validate(request)
    assert model.model_validate(request)
    zone['zone_type'] = 'stockpile'
    for crop in ('', None):
        if crop is None:
            zone.pop('crop', None)
        else:
            zone['crop'] = crop
        validator.validate(request)
        assert model.model_validate(request)


def test_standalone_zone_schema_never_injects_crop():
    zone = action()
    validator = Draft202012Validator(Zone.model_json_schema())
    assert not validator.is_valid(zone)
    assert 'crop' not in zone
    zone['crop'] = None
    assert not validator.is_valid(zone)
    with pytest.raises(ValueError):
        Zone.model_validate(zone)


@pytest.mark.parametrize('model,name', [(CommitSteps, 'commit_steps'), (Decision, 'commit_plan')])
def test_zone_inference_alternatives_are_complete_after_contract_refresh(model, name):
    tools = [structured_tool(name, 'Commit', model.model_json_schema())]
    contracts = ExecutionContracts(tools)
    contracts.expose('home/order', {'type': 'object', 'properties': {}})
    original = deepcopy(tools)
    wire = inference_tools(tools)
    assert tools == original
    zones = []
    def visit(value):
        if isinstance(value, dict):
            if value.get('properties', {}).get('kind', {}).get('const') == 'create_zone' and 'anyOf' in value:
                zones.append(value)
            for child in value.values():
                visit(child)
        elif isinstance(value, list):
            for child in value:
                visit(child)
    visit(wire)
    assert zones
    for zone_schema in zones:
        # llama.cpp visits anyOf before properties. Validate only the union to
        # prove it retains every constraint needed to emit executable actions.
        validator = Draft202012Validator({'anyOf': zone_schema['anyOf']})
        for zone_type in ('stockpile', 'growing'):
            zone = dict(action(), zone_type=zone_type)
            if zone_type == 'growing':
                zone['crop'] = 'Plant_Rice'
            validator.validate(zone)
            for required in ('kind', 'zone_type', 'label', 'patches'):
                missing = dict(zone)
                missing.pop(required)
                assert not validator.is_valid(missing)
            assert not validator.is_valid(dict(zone, kind='place_buildings'))
            assert not validator.is_valid(dict(zone, unexpected=True))
            assert not validator.is_valid(dict(zone, patches=[]))
            assert not validator.is_valid(dict(zone, patches=[{'x': 1, 'z': 1}]))
        assert not validator.is_valid(dict(action(), zone_type='growing'))


@pytest.mark.parametrize('model,name', [(CommitSteps, 'commit_steps'), (Decision, 'commit_plan')])
def test_room_bounds_advertise_interior_minimum_without_restricting_zone_patches(model, name):
    schema = structured_tool(name, 'Commit', model.model_json_schema())['function']['parameters']
    validator = Draft202012Validator(schema)
    room = {'kind': 'build_room_shell', 'bounds': {'x': 1, 'z': 1, 'width': 8, 'height': 3},
        'wall_def': 'ObservedWall', 'door_def': 'ObservedDoor', 'materials': ['ObservedStuff'], 'entrance': 'north'}
    step = {'id': 'room', 'title': 'Room', 'completion_criteria': 'Built', 'action': room}
    request = ({'expected_revision': 0, 'reason': 'Shelter', 'steps': [step]} if model is CommitSteps else
        {'expected_revision': 0, 'disposition': 'revise', 'assessment': 'Shelter',
         'rationale': 'Shelter', 'reply': 'Shelter', 'plan': {'steps': [step]}})
    assert not validator.is_valid(request)
    with pytest.raises(ValueError):
        RoomShell.model_validate(room)
    room['bounds'].update(width=3, height=8)
    assert not validator.is_valid(request)
    room['bounds'].update(width=4, height=4)
    validator.validate(request)
    assert model.model_validate(request)
    step['action'] = {'kind': 'create_zone', 'zone_type': 'stockpile', 'label': 'Supplies',
                      'patches': [{'x': 1, 'z': 1, 'width': 1, 'height': 1}]}
    validator.validate(request)
    assert model.model_validate(request)


@pytest.mark.asyncio
async def test_unknown_native_crop_is_refused_without_substitution():
    zone = Zone.model_validate(dict(action(), crop='UnknownToNative'))
    rt = SimpleNamespace(mode='automate', context_token='load', chat_revision=0, handled_revision=0,
        current_plan=SimpleNamespace(revision=0),
        game=SimpleNamespace(query=AsyncMock(return_value={'zones': [{'label': 'Food',
            'gridCells': [{'x': x, 'z': z} for x in range(1, 4) for z in range(1, 4)]}]})),
        native=AsyncMock(side_effect=ValueError('No sowable plant matches UnknownToNative')))
    with pytest.raises(ValueError, match='No sowable plant matches'):
        await Hands().zone(rt, zone, SimpleNamespace(), '0', 0, 'load', 0)
    rt.native.assert_awaited_once()
    assert rt.native.await_args.args[1]['plant'] == 'UnknownToNative'
