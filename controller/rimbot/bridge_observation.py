"""Read-only migration gateway and compact typed projection of native evidence.

Raw receipts are retained separately. This is deliberately not a RIMAPI-shaped
response adapter; missing fields fail validation instead of becoming zeroes.
"""
from dataclasses import dataclass
from typing import Any
import time

from jsonschema import Draft202012Validator

from .bridge import BridgeClient, runtime_file_read
from .bridge_models import BridgeObservation

OBSERVATION_TOOLS = frozenset({
    'home/colony_facts',
    'home/colony_identity', 'home/status', 'home/list_pawns', 'home/list_things',
    'home/list_buildings', 'home/list_rooms', 'home/list_zones',
    'home/world',
    'home/get_cells_plus',
    'home/spatial_access',
})


class ObservationGateway:
    def __init__(self, bridge: BridgeClient):
        self.bridge = bridge
        self.schemas: dict[str, dict] = {}

    async def query(self, tool: str, **arguments) -> dict:
        if tool == 'home/world' and arguments.get('show'):
            raise ValueError('World inspection does not control the player view')
        if tool not in OBSERVATION_TOOLS:
            raise ValueError(f'Not an approved observation tool: {tool}')
        if tool not in self.schemas:
            detail = await runtime_file_read(self.bridge.detail, tool)
            schema = detail.structuredContent['inputSchema']
            Draft202012Validator.check_schema(schema)
            # The SDK historically ignored unknown keys. Enforce the discovered
            # vocabulary here even if an upstream schema allows extra properties.
            self.schemas[tool] = dict(schema, additionalProperties=False)
        Draft202012Validator(self.schemas[tool]).validate(arguments)
        result = await runtime_file_read(self.bridge.call, tool, **arguments)
        payload = result.structuredContent
        if not isinstance(payload, dict):
            raise ValueError(f'{tool} did not return structured native state')
        if payload.get('unknownArguments'):
            raise ValueError(f'{tool} ignored arguments: {payload["unknownArguments"]}')
        return payload


@dataclass
class ObservationBatch:
    summary: BridgeObservation
    native: dict[str, dict[str, Any]]
    started_at: float = 0


def project(native: dict[str, dict]) -> BridgeObservation:
    start, end = native['status_before'], native['status_after']
    if start['status'] != 'game_loaded' or end['status'] != 'game_loaded':
        raise ValueError('A playable colony is required')
    if start['time']['mapName'] != end['time']['mapName']:
        raise ValueError('Map changed during observation')
    pawns = []
    for p in native['pawns']['pawns']:
        needs, equipment, health = p['needs'], p['equipment'], p['health']
        pawns.append(dict(thing_id=p['thingId'], name=p['name'], position=p['position'],
            job=p['job'], drafted=p['drafted'], downed=p['downed'], dead=p['dead'],
            mood=needs['mood'] if needs else None, food=needs['food'] if needs else None,
            rest=needs['rest'] if needs else None, armed=equipment['armed'] if equipment else None,
            primary_weapon=equipment['primaryLabel'] if equipment else None,
            needs_tend=health['needsTend'] if health else None,
            bleeding=health['bleeding'] if health else None))
    supplies = [dict(def_name=s['defName'], label=s['label'], owned_units=s['ours'],
        owned_unforbidden_units=s['oursUnforbidden'], forbidden_units_all_owners=s['forbidden'],
        stockpiled_units_all_owners=s['inStockpile'], fogged_units_all_owners=s['fogged'],
        trader_units=s['traderStock']) for s in native['supplies']['things']]
    warnings = [f'{section}: {payload["unknownArgumentsWarning"]}' for section, payload in native.items()
                if payload.get('unknownArgumentsWarning')]
    for section, payload in native.items():
        if isinstance(payload.get('skipped'), list) and payload['skipped']:
            warnings.append(f'{section}: unavailable readings recorded in native diagnostics')
    if native['buildings'].get('skipped', {}).get('byMaxDetailed', 0):
        warnings.append('Building details were truncated; see native diagnostics')
    first, last = start['time']['ticksGame'], end['time']['ticksGame']
    if first != last:
        warnings.append('The game advanced during this batch; observations are not one atomic snapshot')
    return BridgeObservation.model_validate(dict(backend='rimbridge', start_tick=first, end_tick=last,
        same_tick=first == last, paused=end['time']['paused'], map_name=end['time']['mapName'],
        pawns=pawns, supplies=supplies,
        visible_rooms=sum(not r['fogged'] for r in native['rooms']['rooms']),
        fogged_rooms_omitted=sum(r['fogged'] for r in native['rooms']['rooms']),
        zone_count=native['zones']['zoneCount'], warnings=warnings,
        alert_labels=[a['label'] for a in end['alerts']],
        hostile_count=end['counts']['hostileCount'],
        hunting_predator_count=end['counts']['huntingPredatorCount']))


async def observe(gateway: ObservationGateway) -> ObservationBatch:
    started_at = time.time()
    native = {}
    for section, tool, arguments in [
        ('status_before', 'home/status', {}),
        ('pawns', 'home/list_pawns', {'colonistsOnly': True, 'health': True, 'needs': True, 'equipment': True}),
        ('supplies', 'home/list_things', {'ownership': 'ours', 'excludeChunks': True, 'maxPositionsPerDef': 0}),
        ('buildings', 'home/list_buildings', {'playerOnly': True}),
        ('rooms', 'home/list_rooms', {}), ('zones', 'home/list_zones', {}),
        ('status_after', 'home/status', {}),
    ]:
        native[section] = await gateway.query(tool, **arguments)
    return ObservationBatch(project(native), native, started_at)
