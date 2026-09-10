from copy import deepcopy
from unittest.mock import AsyncMock

import pytest
pytest.importorskip('mcp')
from mcp.types import CallToolResult
from jsonschema import ValidationError
from rimgovernor.bridge_observation import ObservationGateway, project, observe


async def test_native_batch_preserves_projection_and_rejects_partial_sections():
    bridge = AsyncMock()
    bridge.observation_batch_version = 1
    gateway = ObservationGateway(bridge)
    gateway.query = AsyncMock(return_value={'success': True, 'version': 1, 'sections': native()})
    batch = await observe(gateway)
    assert batch.summary == project(native())
    gateway.query.assert_awaited_once_with('home/observation_batch')
    gateway.query.return_value['sections']['pawns']['success'] = False
    with pytest.raises(ValueError, match='pawns'):
        await observe(gateway)


async def test_legacy_observation_remains_available_without_capability():
    bridge = AsyncMock()
    bridge.observation_batch_version = 0
    gateway = ObservationGateway(bridge)
    data = native()
    gateway.query = AsyncMock(side_effect=[data[k] for k in
        ('status_before', 'pawns', 'supplies', 'buildings', 'rooms', 'zones', 'status_after')])
    assert (await observe(gateway)).summary == project(data)
    assert gateway.query.await_count == 7


async def test_observation_gateway_blocks_writes_and_unknown_arguments_before_dispatch():
    bridge = AsyncMock()
    bridge.detail.return_value = CallToolResult(content=[], structuredContent={
        'inputSchema': {'type': 'object', 'properties': {'colonistsOnly': {'type': 'boolean'}}}})
    gateway = ObservationGateway(bridge)
    with pytest.raises(ValueError):
        await gateway.query('home/order', action='equip')
    bridge.detail.assert_not_awaited()
    with pytest.raises(ValidationError):
        await gateway.query('home/list_pawns', skills=True)
    bridge.call.assert_not_awaited()
    bridge.call.return_value = CallToolResult(content=[], structuredContent={'pawns': [], 'unknownArguments': []})
    await gateway.query('home/list_pawns', colonistsOnly=True)
    await gateway.query('home/list_pawns', colonistsOnly=False)
    assert bridge.detail.await_count == 1


def native():
    status = {'status': 'game_loaded', 'time': {'mapName': 'Test', 'ticksGame': 100, 'paused': True},
              'alerts': [{'label': 'Low food'}], 'counts': {'hostileCount': 2, 'huntingPredatorCount': 0}}
    return {'status_before': status, 'status_after': deepcopy(status), 'pawns': {'pawns': [{
        'thingId': 'Thing_Human1', 'name': 'A', 'position': {'x': 10, 'z': 20},
        'job': None, 'drafted': False, 'downed': False, 'dead': False,
        'needs': None, 'equipment': None, 'health': None}]}, 'supplies': {'things': [{
        'defName': 'WoodLog', 'label': 'Wood', 'ours': 500, 'oursUnforbidden': 0,
        'forbidden': 500, 'inStockpile': 0, 'fogged': 50, 'traderStock': 100}]},
        'buildings': {}, 'rooms': {'rooms': [{'fogged': True}, {'fogged': False}]},
        'zones': {'zoneCount': 0}}


def test_projection_keeps_owned_forbidden_stored_and_unknown_distinct():
    result = project(native())
    assert result.supplies[0].owned_units == 500
    assert result.supplies[0].owned_unforbidden_units == 0
    assert result.supplies[0].stockpiled_units_all_owners == 0
    assert result.pawns[0].armed is None
    assert result.visible_rooms == 1 and result.fogged_rooms_omitted == 1
    assert result.same_tick
    assert result.alert_labels == ['Low food'] and result.hostile_count == 2


def test_missing_state_is_not_silently_defaulted_and_tick_change_is_reported():
    data = native()
    del data['supplies']['things'][0]['oursUnforbidden']
    with pytest.raises(KeyError):
        project(data)
    data = native()
    data['status_after']['time']['ticksGame'] += 1
    result = project(data)
    assert not result.same_tick and result.warnings
