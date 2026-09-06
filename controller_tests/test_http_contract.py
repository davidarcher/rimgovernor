import copy
import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import httpx
import pytest
from jsonschema import ValidationError
from openapi_spec_validator import validate

from rimbot.http_contract import DOCUMENT, OPERATIONS, HttpContractClient, HttpContractError
from rimbot.http_models import get_v1_map_rooms_Query, MapRoomsDto, Input_ThingsAtCellRequestDto, Input_PositionDto
from rimbot.server import create_app

ROOT = Path(__file__).parents[1]


def envelope(data):
    return {'success': True, 'data': data, 'errors': [], 'warnings': [], 'timestamp': '2026-09-05T18:00:00Z'}


def test_complete_openapi_and_generated_outputs_are_current():
    validate(DOCUMENT)
    subprocess.run([sys.executable, str(ROOT/'scripts/generate_http_contracts.py'), '--check'], cwd=ROOT, check=True, capture_output=True)
    assert len(OPERATIONS) == 203
    assert {'get_v1_map_rooms', 'get_v1_pawns_details', 'post_v1_map_zone_stockpile', 'get_v1_events'} <= OPERATIONS.keys()
    assert 'text/event-stream' in DOCUMENT['paths']['/api/v1/events']['get']['responses']['200']['content']
    assert '/api/v1/docs/extensions/{extensionId}' not in DOCUMENT['paths']


async def test_typed_native_response_and_missing_query_rejection():
    calls = []
    def respond(request):
        calls.append(request)
        return httpx.Response(200, json=envelope({'rooms': []}))
    async with httpx.AsyncClient(base_url='http://game', transport=httpx.MockTransport(respond)) as http:
        client = HttpContractClient(http)
        with pytest.raises(ValidationError):
            await client.get_v1_map_rooms()
        assert not calls
        result = await client.get_v1_map_rooms(query=get_v1_map_rooms_Query(map_id=4))
        assert isinstance(result.data, MapRoomsDto)
        assert result.data.rooms == []
        assert calls[0].url.params['map_id'] == '4'
        assert calls[0].content == b''


async def test_bad_responses_rejected_and_uncertain_writes_not_retried():
    async with httpx.AsyncClient(base_url='http://game', transport=httpx.MockTransport(lambda r: httpx.Response(200, json=envelope({'rooms': [], 'invented': True})))) as http:
        with pytest.raises(ValidationError):
            await HttpContractClient(http).get_v1_map_rooms(query=get_v1_map_rooms_Query(map_id=0))
    calls = []
    def fail(request):
        calls.append(request)
        raise httpx.ReadTimeout('Write outcome unknown')
    async with httpx.AsyncClient(base_url='http://game', transport=httpx.MockTransport(fail)) as http:
        with pytest.raises(httpx.ReadTimeout):
            await HttpContractClient(http).post_v1_research_stop()
        assert len(calls) == 1


async def test_get_body_and_query_fallback_transports():
    calls = []
    def respond(request):
        calls.append(request)
        return httpx.Response(200, json=envelope([]))
    async with httpx.AsyncClient(base_url='http://game', transport=httpx.MockTransport(respond)) as http:
        client = HttpContractClient(http)
        await client.get_v1_map_things_at(body=Input_ThingsAtCellRequestDto(map_id=0, position=Input_PositionDto(x=10, y=0, z=12)))
        assert calls[0].method == 'GET'
        assert json.loads(calls[0].content)['position']['x'] == 10
        with pytest.raises(HttpContractError, match='body or query'):
            await client.post_v1_colonist_time_assignment()
        assert len(calls) == 1


async def test_http_error_remains_an_error_and_streaming_is_not_buffered():
    error = {'success': False, 'errors': ['Missing pawn'], 'warnings': [], 'timestamp': '2026-09-05T18:00:00Z'}
    calls = []
    def respond(request):
        calls.append(request)
        return httpx.Response(404, json=error)
    async with httpx.AsyncClient(base_url='http://game', transport=httpx.MockTransport(respond)) as http:
        client = HttpContractClient(http)
        with pytest.raises(HttpContractError, match='Missing pawn'):
            await client.get_v1_maps()
        with pytest.raises(HttpContractError, match='streaming'):
            await client._call('get_v1_events')
        assert len(calls) == 1


async def test_dashboard_contract_is_available_without_a_loaded_game():
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app()), base_url='http://testserver') as client:
        response = await client.get('/api/rimapi/openapi.json')
        assert response.status_code == 200
        assert response.json() == DOCUMENT


def test_source_guard_detects_omitted_routes_and_changed_handlers():
    spec = importlib.util.spec_from_file_location('check_http_api', ROOT/'scripts/check_http_api.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    route = {'method': 'GET', 'path': '/api/example', 'registered': True, 'body': '{}', 'calls': []}
    doc = {'paths': {'/api/example': {'get': {'x-native-source-sha256': module.digest({'body': '{}', 'calls': []})}}, '/api/v1/events': {'get': {}}}, 'components': {'schemas': {}}, 'x-unregistered-routes': []}
    audit = {'routes': [route], 'types': {}}
    module.check(doc, audit)
    changed = copy.deepcopy(audit)
    changed['routes'][0]['body'] = '{new request parser}'
    with pytest.raises(ValueError, match='handler changed'):
        module.check(doc, changed)
    with pytest.raises(ValueError, match='route drift'):
        module.check(doc, {'routes': [], 'types': {}})
