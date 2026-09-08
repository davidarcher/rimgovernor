"""Reviewed gameplay surface over native bridge contracts; no HTTP emulation."""
from jsonschema import Draft202012Validator
from .bridge_observation import OBSERVATION_TOOLS, ObservationGateway
from .native_contracts import validate_arguments

READS = OBSERVATION_TOOLS | frozenset({
    'rimworld/get_cells_info', 'rimworld/get_cell_info',
    'rimworld/list_architect_categories', 'rimworld/list_architect_designators',
    'rimworld/list_selected_gizmos', 'rimworld/get_selection_semantics',
    'rimworld/list_letters', 'rimworld/get_ui_state', 'rimworld/get_screen_targets',
    'rimworld/get_ui_layout', 'rimworld/list_main_tabs', 'rimworld/list_inspect_tabs',
    'rimworld/list_messages', 'rimworld/list_alerts', 'rimworld/get_map_target_info',
})
WRITES = frozenset({'home/zone_cells', 'home/place_building', 'home/pawn_config',
    'home/building_config', 'home/bills', 'home/order', 'home/trade', 'home/research', 'home/dialog_text', 'home/install',
    'rimworld/set_time_speed', 'rimworld/apply_architect_designator',
    'rimworld/open_letter', 'rimworld/dismiss_letter', 'rimworld/click_screen_target',
    'rimworld/click_ui_target', 'rimworld/scroll_ui_target',
    'rimworld/open_main_tab', 'rimworld/close_main_tab'})


def is_write(tool, arguments):
    if tool == 'home/research':
        return bool(arguments.get('set')) and arguments.get('dryRun') is not True
    if (tool.startswith('home/') or tool == 'rimworld/apply_architect_designator') and arguments.get('dryRun') is True:
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
        validate_arguments(tool, schema, arguments)
        arguments = dict(arguments)
        if tool == 'rimworld/click_screen_target':
            from .dialog_control import dismissal_target
            targets = await self.invoke('rimworld/get_screen_targets', {})
            dismissal_target(arguments.get('targetId'), targets)
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
        if tool == 'home/install' and payload.get('accepted') is not True:
            raise ValueError(payload.get('reason') or 'Native installation was not accepted')
        if tool == 'home/research' and arguments.get('set'):
            write = payload.get('write') or {}
            if write.get('refused') is not False:
                raise ValueError(write.get('reason') or 'Native research selection was not accepted')
        if tool == 'home/dialog_text' and arguments.get('dryRun') is False:
            if payload.get('applied') is not True or payload.get('error'):
                raise ValueError(payload.get('error') or 'Dialog text was not applied')
            if payload.get('set', {}).get('after') != arguments.get('text'):
                raise ValueError('Dialog field readback does not match the requested text')
        return payload


def for_model(payload, tool=None, catalog_offset=0):
    """Omit explanatory boilerplate, never silently cut entity rows or facts."""
    import json
    result = {k: v for k, v in payload.items() if k not in ('operation', 'notes', 'watch')}
    if tool == 'rimworld/get_ui_layout' and isinstance(payload.get('surfaces'), list):
        from .vendor.companion_ui import slim_surface
        result = {k:v for k,v in result.items() if k != 'surfaces'}
        result['surfaces'] = [slim_surface(surface) for surface in payload['surfaces']]
        for compact, surface in zip(result['surfaces'], payload['surfaces']):
            compact['controls'] = [{k: element.get(k) for k in
                ('targetId', 'label', 'kind', 'disabled', 'isChecked', 'screenRect')}
                for element in surface.get('elements', [])
                if element.get('actionable') or element.get('kind') == 'scroll_view']
        result['meaning'] = 'Companion compact UI report. Text is presentation, not proof of action completion. Query a surfaceId to narrow the next capture.'
    if tool == 'home/get_cells_plus' and result.get('cells'):
        cells = result['cells']
        if all(isinstance(c,dict) and 'x' in c and 'z' in c for c in cells):
            profiles=[]; index={}; locations=[]
            for cell in cells:
                profile={k:v for k,v in cell.items() if k not in ('x','z')}
                key=json.dumps(profile,sort_keys=True,separators=(',',':'))
                if key not in index:
                    index[key]=len(profiles); profiles.append(profile)
                locations.append([cell['x'],cell['z'],index[key]])
            packed={k:v for k,v in result.items() if k!='cells'}
            packed.update(cell_profiles=profiles,cells_x_z_profile=locations,
                cell_encoding='Each [x,z,profile_index] is one observed cell; merge with cell_profiles[profile_index]. Omitted coordinates and fields remain unknown.')
            if len(json.dumps(packed)) < len(json.dumps(result)):
                result=packed
    if tool in ('rimworld/list_architect_categories', 'rimworld/list_architect_designators'):
        # The registry entries are authoritative discovery data. The accompanying
        # UI snapshot and duplicate selection-state payload are not definitions.
        result = {k: v for k, v in result.items() if k not in ('state', 'designatorState')}
    if tool == 'rimworld/list_architect_designators' and isinstance(result.get('designators'), list):
        rows = result['designators']
        # A page is not the whole construction vocabulary. Keep a small index so
        # ordinary walls are not hidden behind bridges/doors on the first page.
        result['buildable_index'] = [{'defName': row['buildableDefName'],
            'label': row.get('buildableLabel') or row.get('label')}
            for row in rows if row.get('buildableDefName')]
        result['designator_index'] = [{k:row[k] for k in ('id','label','applicationKind',
            'supportsCellApplication','supportsRectangleApplication') if k in row}
            for row in rows if row.get('id')]
        if catalog_offset < 0 or catalog_offset > len(rows):
            raise ValueError(f'catalog_offset must be between 0 and {len(rows)}')
        end = min(catalog_offset+8, len(rows))
        result = dict(result, designators=rows[catalog_offset:end])
        while end > catalog_offset+1 and len(json.dumps(result)) > 23000:
            end -= 1
            result['designators'] = rows[catalog_offset:end]
        result['catalog_page'] = {'offset':catalog_offset, 'total':len(rows),
            'next_offset':end if end < len(rows) else None,
            'instruction':'Repeat the same inspect call with catalog_offset=next_offset for more entries. Each page is a fresh native read.'}
    if len(json.dumps(result)) > 24000:
        return {'requires_narrower_query': True,
                'reason': 'Result exceeds this turn\'s detail budget. Use native filters or fewer optional detail blocks.',
                'fields': {k: len(v) if isinstance(v, (list, dict)) else v for k, v in result.items()}}
    return result


def inspection_result(payload, name, arguments, catalog_offset=0, callable_name=None):
    """Keep controller pagination separate from the unmodified native contract."""
    from copy import deepcopy
    result = for_model(payload, tool=name, catalog_offset=catalog_offset)
    page = result.get('catalog_page')
    if page and page['next_offset'] is not None:
        page['next_call'] = {'name':'inspect', 'arguments':{
            'name':name, 'arguments':deepcopy(arguments), 'catalog_offset':page['next_offset']}}
        page['instruction'] = 'Call next_call exactly. catalog_offset is beside arguments, not inside native arguments.'
        if callable_name:
            page['next_call']={'name':callable_name,'arguments':dict(deepcopy(arguments),catalog_offset=page['next_offset'])}
            page['instruction']='Call next_call exactly to read the next page.'
    return result
