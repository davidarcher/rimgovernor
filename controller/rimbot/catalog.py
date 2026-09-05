"""RIMAPI contracts plus explicit exclusion of editor/cheat capabilities."""
import copy
import json
from pathlib import Path
from jsonschema import Draft202012Validator

READ_POST = {'post_builder_check_zone', 'post_builder_copy', 'post_camera_screenshot'}
WRITE_CATEGORIES = {'Bill', 'ColonistsWork', 'Order', 'Research', 'PawnJob', 'Trade'}
WRITE_NAMES = {
    'post_builder_blueprint', 'post_things_set_forbidden',
    'post_map_zone_growing', 'post_map_zone_stockpile', 'post_map_zone_stockpile_update',
    'delete_map_zone_stockpile_delete', 'post_map_building_power',
    'post_colonist_work_priority', 'post_colonists_work_priority', 'post_colonist_time_assignment',
    'post_jobs_make_equip', 'post_pawn_edit_status', 'post_pawn_edit_apparel',
}
REQUIRED = {
    'post_builder_blueprint': ['map_id', 'position', 'blueprint'],
    'post_things_set_forbidden': ['map_id', 'thing_ids', 'forbidden'],
    'post_map_zone_growing': ['map_id', 'point_a', 'point_b', 'plant_def'],
    'post_map_zone_stockpile': ['map_id', 'point_a', 'point_b'],
    'post_colonist_work_priority': ['id', 'work', 'priority'],
    'post_pawn_edit_status': ['pawn_id', 'is_drafted'],
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
    def __init__(self):
        data = json.loads((Path(__file__).parent / 'data/catalog.json').read_text())
        self.revision = data['revision']
        self.entries = {}
        self.available = set()
        self.discovered = False
        for e in data['endpoints']:
            e = copy.deepcopy(e)
            e['write'] = e['method'] != 'GET' and e['name'] not in READ_POST
            e['exposed'] = (not e['write'] or e['category'] in WRITE_CATEGORIES or e['name'] in WRITE_NAMES)
            if any(s in e['path'] for s in ['/dev/', '/learning/', '/image', '/portrait', '/mods/', '/incidents/top', '/incident/chance']):
                e['exposed'] = False
            props = e['schema'].get('properties', {})
            if e['name'] == 'post_pawn_edit_status':
                props.pop('kill', None)
                props.pop('resurrect', None)
            if e['name'] == 'post_research_target' and 'force' in props:
                props['force'] = {'const': False}
            if e['name'] == 'post_builder_blueprint':
                props['clear_obstacles'] = {'const': False}
            e['schema']['required'] = sorted(set(e['schema'].get('required', []) + REQUIRED.get(e['name'], [])))
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
            raise ValueError('Specialists may query; only the administrator executes approved actions.')
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
