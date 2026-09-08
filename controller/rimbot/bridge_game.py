"""Reviewed gameplay surface over native bridge contracts; no HTTP emulation."""
from jsonschema import Draft202012Validator
from .bridge_observation import OBSERVATION_TOOLS, ObservationGateway

READS = OBSERVATION_TOOLS | frozenset({
    'rimworld/get_cells_info', 'rimworld/get_cell_info',
    'rimworld/list_architect_categories', 'rimworld/list_architect_designators',
    'rimworld/list_selected_gizmos', 'rimworld/get_selection_semantics',
})
WRITES = frozenset({'home/zone_cells', 'home/place_building', 'home/pawn_config',
    'home/building_config', 'home/bills', 'home/order', 'home/trade', 'home/research',
    'rimworld/set_time_speed', 'rimworld/apply_architect_designator'})


def is_write(tool, arguments):
    if tool == 'home/research':
        return bool(arguments.get('set')) and arguments.get('dryRun') is not True
    if tool.startswith('home/') and arguments.get('dryRun') is True:
        return False
    if tool == 'home/order' and arguments.get('action', 'resolve') == 'resolve':
        return False
    if tool == 'home/trade':
        return arguments.get('action', 'list_traders') not in ('list_traders', 'sheet', 'preview', 'status')
    return tool in WRITES


class BridgeGame(ObservationGateway):
    async def describe(self, tool):
        if tool not in READS | WRITES:
            raise ValueError('Tool is outside the gameplay surface')
        if tool not in self.schemas:
            detail = await self.bridge.detail(tool)
            schema = detail.structuredContent['inputSchema']
            Draft202012Validator.check_schema(schema)
            self.schemas[tool] = dict(schema, additionalProperties=False)
        return self.schemas[tool]

    async def invoke(self, tool, arguments, *, allow_write=False):
        if tool == 'home/world' and arguments.get('show'):
            raise ValueError('World inspection does not control the player view')
        if tool not in READS | WRITES:
            raise ValueError('Unknown gameplay tool')
        if is_write(tool, arguments) and not allow_write:
            raise ValueError('Automation is off; no game action was sent')
        schema = await self.describe(tool)
        Draft202012Validator(schema).validate(arguments)
        arguments = dict(arguments)
        if tool == 'home/pawn_config' and arguments.get('drop'):
            raise ValueError('Instant gear dropping bypasses normal pawn work; use a native pawn order')
        if arguments.get('ultraSpeedBoost'):
            raise ValueError('Use normal game speeds')
        if tool == 'home/trade' and arguments.get('action') == 'open':
            arguments['requireAdjacent'] = True
        if arguments.get('godMode'):
            raise ValueError('Use normal gameplay placement')
        if tool in WRITES:
            if 'watch' in schema.get('properties', {}):
                arguments['watch'] = False
            if (tool.startswith('home/') and 'dryRun' in schema.get('properties', {}) and 'dryRun' not in arguments
                    and (tool != 'home/research' or arguments.get('set'))):
                raise ValueError('State dryRun explicitly: true to preview, false to act')
        result = await self.bridge.call(tool, **arguments)
        payload = result.structuredContent
        if not isinstance(payload, dict):
            raise ValueError('Native structured receipt is missing')
        if payload.get('unknownArguments'):
            raise ValueError('Native tool reported ignored arguments')
        if tool == 'home/research' and arguments.get('set'):
            write = payload.get('write') or {}
            if write.get('refused') is not False:
                raise ValueError(write.get('reason') or 'Native research selection was not accepted')
        return payload


def for_model(payload):
    """Omit explanatory boilerplate, never silently cut entity rows or facts."""
    import json
    result = {k: v for k, v in payload.items() if k not in ('operation', 'notes', 'watch')}
    if len(json.dumps(result)) > 24000:
        return {'requires_narrower_query': True,
                'reason': 'Result exceeds this turn\'s detail budget. Use native filters or fewer optional detail blocks.',
                'fields': {k: len(v) if isinstance(v, (list, dict)) else v for k, v in result.items()}}
    return result
