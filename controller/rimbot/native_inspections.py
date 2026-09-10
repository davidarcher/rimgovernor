"""On-demand, schema-backed inspection tools; no independent game writer."""
from copy import deepcopy
from .bridge_game import READS, WRITES
from .consultation import structured_tool

DESCRIBABLE=READS | frozenset({'home/medical_operations','home/caravan_gift', 'home/fulfill_quest','home/caravan','home/accept_quest','home/place_building','home/install','home/zone_cells','home/research'})


class NativeInspections:
    def __init__(self):
        self.names = {}

    def expose(self, name, schema, tools):
        if name not in READS | WRITES:
            raise ValueError('Tool is outside the reviewed native surface')
        parameters = deepcopy(schema)
        preview = parameters.get('properties', {}).get('dryRun')
        can_preview = name.startswith('home/') and isinstance(preview,dict) and preview.get('type')=='boolean'
        if name not in READS and not can_preview and name not in ('home/research','home/order','home/trade'):
            return {'native_tool':name, 'input_schema':schema,
                    'execution_only':True, 'instruction':'Use the appropriate semantic command; this tool has no reviewed inspection mode.'}
        alias='native_'+name.replace('/','__')
        if alias not in self.names:
            if name=='rimworld/list_architect_designators':
                parameters.setdefault('properties',{})['catalog_offset']={
                    'type':'integer','minimum':0,'default':0,
                    'description':'Controller pagination offset. Follow catalog_page.next_call; this field is not sent to the game.'}
            if can_preview:
                preview.update(const=True, default=True)
                preview['description']='Inspection only: must be true. Execution belongs in the committed plan.'
            tools.append(structured_tool(alias,
                f'Inspect {name} using its native arguments directly. Read-only or dry-run only; never executes orders. '
                'Request game changes with the appropriate semantic command tool.',parameters))
            self.names[alias]=name
        return {'native_tool':name,'callable_tool':alias,
                'instruction':'Call this tool directly with its advertised native parameters; do not wrap them in name/arguments. '
                              'It stays available for the rest of this review. No game action was executed.'}
