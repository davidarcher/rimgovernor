"""RIMAPI contracts plus explicit exclusion of editor/cheat capabilities."""
from .capabilities import attach_execution_policy
import copy
import json
from pathlib import Path
from jsonschema import Draft202012Validator

READ_POST = {'post_builder_check_zone', 'post_builder_copy', 'post_camera_screenshot'}
WRITE_CATEGORIES = {'Bill', 'ColonistsWork', 'Order', 'Research', 'PawnJob', 'Trade'}
WRITE_NAMES = {
    'post_work_settings',
    'post_builder_blueprint', 'post_things_set_forbidden',
    'post_map_zone_growing', 'post_map_zone_stockpile', 'post_map_zone_stockpile_update',
    'delete_map_zone_stockpile_delete', 'post_map_building_power',
    'post_colonist_work_priority', 'post_colonists_work_priority', 'post_colonist_time_assignment',
    'post_jobs_make_equip', 'post_pawn_edit_status', 'post_pawn_edit_apparel',
}
REQUIRED = {
    'get_map_things_at': ['map_id', 'position'],
    'post_builder_blueprint': ['map_id', 'position', 'blueprint'],
    'post_things_set_forbidden': ['map_id', 'thing_ids', 'forbidden'],
    'post_map_zone_growing': ['map_id', 'point_a', 'point_b', 'plant_def'],
    'post_map_zone_stockpile': ['map_id', 'point_a', 'point_b'],
    'post_colonist_work_priority': ['id', 'work', 'priority'],
    'post_pawn_edit_status': ['pawn_id'],
    'post_pawn_job': ['pawn_id', 'job_def'],
    'post_colonist_time_assignment':['pawn_id','hour','assignment'],
    'post_colonists_work_priority':['priorities'],
    'post_buildings_bills_add':['recipe_def_name'],
    'post_map_zone_stockpile_update':['zone_id'],
    'post_order_designate_area':['map_id','type','point_a','point_b'],
    'post_pawn_medical_bed_rest':['patient_pawn_id'],
    'post_pawn_medical_tend':['patient_pawn_id'],
}


class Catalog:
    def install_contracts(self, manifest):
        if manifest.get('version') != 1:
            raise ValueError('Unsupported native construction contract version.')
        expected=json.loads((Path(__file__).parent/'data/construction_contracts.json').read_text())
        if manifest != expected:
            raise ValueError('Native construction contract changed. Regenerate the Python domain types; do not guess at schema drift.')
        for entry in manifest['endpoints']:
            Draft202012Validator.check_schema(entry['request_schema'])
            Draft202012Validator.check_schema(entry['response_schema'])
            name=entry['name']
            if not ((name.startswith('construction_') and entry['path'].startswith('/api/v2/construction/')) or (name.startswith('planning_') and entry['path'].startswith('/api/v2/planning/')) or (name=='zone_growing_cells' and entry['path']=='/api/v2/zones/growing-cells') or (name in ('orders_unforbid_all','orders_forbidden_overview') and entry['path'].startswith('/api/v2/orders/'))):
                raise ValueError('Unexpected native construction contract route.')
            self.entries[name]={**entry,'schema':entry['request_schema'],'native_contract':True,
                'transport':'json','query_keys':[],'exposed':True,'category':'Order' if name.startswith('orders_') else 'Construction'}
            attach_execution_policy(self.entries[name])
            self.available.add(name)
        # Construction now has one authoritative tool path.
        self.entries['post_builder_blueprint']['exposed']=False

    def __init__(self):
        data = json.loads((Path(__file__).parent / 'data/catalog.json').read_text())
        self.revision = data['revision']
        self.entries = {}
        self.available = set()
        self.discovered = False
        for e in data['endpoints']:
            e = copy.deepcopy(e)
            def describe_positions(schema):
                if isinstance(schema,list):
                    for part in schema:describe_positions(part)
                elif isinstance(schema,dict):
                    p=schema.get('properties',{})
                    if {'x','y','z'} <= set(p):
                        schema['description']='RimWorld map position: x/z are the horizontal map plane. y is vertical height, normally 0. Copy observed x and z; do not use y as the north/south coordinate.'
                        schema['required']=sorted(set(schema.get('required',[]))|{'x','z'})
                    for part in schema.values():describe_positions(part)
            describe_positions(e['schema'])
            e['write'] = e['method'] != 'GET' and e['name'] not in READ_POST
            e['exposed'] = (not e['write'] or e['category'] in WRITE_CATEGORIES or e['name'] in WRITE_NAMES)
            if any(s in e['path'] for s in ['/dev/', '/learning/', '/image', '/portrait', '/mods/', '/incidents/top', '/incident/chance','/docs','/cache/','/openapi']):
                e['exposed'] = False
            props = e['schema'].get('properties', {})
            if e['name'] == 'post_order_designate_area':
                props['type']={'type':'string','enum':['mine','deconstruct','harvest','hunt','remove-all']}
                e['description'] += ' Only these five designation types are supported. This does not plan rooms, roofs or shelters. Use construction_place for buildings; the architect owns planning marks.'
            if e['name'] == 'post_pawn_edit_status':
                props.pop('kill', None)
                props.pop('resurrect', None)
                e['schema']['anyOf']=[{'required':['is_drafted']},{'required':['hostility_response']}]
                e['description'] += ' hostility_response sets the undrafted player response: Ignore, Attack (Fight), or Flee. It is immediate, does not draft the pawn or order an attack. Use Attack for capable early defenders; preserve nonviolent pawns.'
            if e['name'] == 'post_research_target' and 'force' in props:
                props['force'] = {'const': False}
            if e['name'] == 'post_builder_blueprint':
                props['clear_obstacles'] = {'const': False}
                e['description'] += ' Construct ordinary building blueprints from ThingDefs, not premade structure templates. Discover ThingDefs using get_def_all with query.path=things_defs and query.where={category: Building}. Put furniture and walls in blueprint.buildings; floors are TerrainDefs only. position is the anchor; rel_x/rel_z are offsets. Use observed definitions and terrain, not guessed names or layouts.'
                props['blueprint']['description']='Payload assembled by the manager using the buildings and floors schemas below; no separate blueprint-definition registry is required.'
                props['blueprint']['properties']['buildings']['description']='Building ThingDefs (furniture, walls, doors). def_name identifies what to construct; stuff_def_name selects its material when applicable.'
            if e['name']=='get_def_all':
                e['description'] += ' Returns definition groups such as things_defs and terrain_defs. filters selects GROUPS, not individual names. Omit filters to use the complete session snapshot. Use query.path=things_defs and query.where={def_name: observed name} or {category: Building} to filter individual definitions. No separate structure-blueprint definitions are needed for construction.'
                props['filters']['description']='Optional definition group names, e.g. ThingsDefs or TerrainDefs. Not building names. Prefer omitting this and selecting the group with query.path.'
            if e['name']=='get_resources_stored':
                e['description'] += ' Grouped inventory: read without category first, then select the returned list with query.path (for example resources_raw). Output group names are not necessarily native category filter names. Never infer no supplies from an unverified category filter.'
                props['category']['description']='Optional native ThingCategoryDef filter, not the name of a JSON response group. Prefer omitting this and using query.path with an observed group.'
            if e['name']=='get_map_things':
                e['description'] += ' Haulable items only; not a complete map entity list. Does not expose construction blueprints. Use get_map_things_at for exact-cell blueprint readback.'
            if e['name']=='post_things_set_forbidden':
                e['description'] += ' Allow / unforbid selected items immediately with forbidden=false, or forbid them with forbidden=true. thing_ids are observed thing_id values from map item queries. No hauling, stockpile, pawn job or labor assignment is needed to change this flag. Verify the selected items is_forbidden field; storage counts do not verify this action.'
            if e['name']=='get_map_things_radius':
                e['description'] += ' Includes items, buildings and plants; excludes blueprints. Use get_map_things_at to inspect construction at a cell.'
            if e['name']=='get_map_things_at':
                e['description'] += ' All things at the exact cell, including blueprints and construction frames. Use this for immediate blueprint-placement verification. Returned rows include thing_id, def_name, label and position.'
            if e['name']=='get_map_rooms':
                e['description'] += ' Native regions are not necessarily usable shelter. open_roof_count counts unroofed cells. visible_cells gives up to 256 exact explored cells; cells_truncated says whether geometry is incomplete. visible_cell is a standable sample; pawns_reaching_visible_cell tests native reachability to that sample only. No visible_cell means no explored standable sample. Do not choose a site from a room ID or cell count alone.'
            e['schema']['required'] = sorted(set(e['schema'].get('required', []) + REQUIRED.get(e['name'], [])))
            if e['name'] in ('get_colonist_detailed','get_colonists_detailed'):
                e['description'] += ' Work priorities include only enabled work (priority > 0) and are sorted by priority; disabling work removes its row. Filter by work_type, never rely on array position. Use query.path=colonist_work_info.work_priorities on a single pawn and where={work_type: observed name}; total=0 means that work is not enabled.'
            if e['name']=='get_colonists_detailed':
                e['description'] += ' Bulk pawn data is cached upstream for 1800 game ticks. Use get_colonist_detailed with id for uncached command verification.'
            attach_execution_policy(e)
            self.entries[e['name']] = e

    def discover(self, docs):
        found = set()
        def walk(value):
            if isinstance(value, dict):
                low = {k.lower(): v for k, v in value.items()}
                if isinstance(low.get('path'), str) and isinstance(low.get('method'), str):
                    found.add((low['method'].upper(), low['path']))
                for v in value.values():
                    walk(v)
            elif isinstance(value, list):
                for v in value:
                    walk(v)
        walk(docs)
        if not found:
            raise ValueError('RIMAPI did not return a usable endpoint catalog.')
        self.available = {n for n,e in self.entries.items() if (e['method'], e['path']) in found}
        self.discovered = True

    def get(self, name, write=None):
        e = self.entries.get(name)
        if not e or not e['exposed']:
            raise ValueError(f'Unavailable player capability: {name}')
        if not self.discovered or name not in self.available:
            raise ValueError(f'Connected RIMAPI does not advertise {name}')
        if write is not None and e['write'] != write:
            if e['write']:
                raise ValueError(f'{name} changes the game. Call the native tool named {name} to draft it, with title and arguments. Then submit your report. The controller executes approved actions afterward.')
            raise ValueError(f'{name} only reads state; use query, not an action.')
        return e

    def validate(self, name, args, write=None):
        e = self.get(name, write)
        errors = sorted(Draft202012Validator(e['schema']).iter_errors(args), key=lambda x: str(x.path))
        if errors:
            raise ValueError('; '.join(f'{".".join(map(str,x.path)) or name}: {x.message}' for x in errors[:3]))
        return e

    def listing(self, search='', write=None):
        words = search.lower().split()
        return [dict(name=n, description=e['description'], category=e['category'], write=e['write'])
                for n,e in self.entries.items() if e['exposed'] and n in self.available
                and (write is None or e['write'] == write)
                and all(w in (n+' '+e['description']+' '+e['category']).lower() for w in words)]
