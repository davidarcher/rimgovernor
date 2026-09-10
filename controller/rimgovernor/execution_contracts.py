"""Bind plan-native arguments to the contracts discovered in this review."""
from copy import deepcopy
from typing import get_args
from .colony_plan import NativeOperation
from .consultation import structured_tool
from .native_contracts import BILL_WRITE_ACTIONS


class ExecutionContracts:
    def __init__(self, tools):
        self.contracts = {}
        self.branches = []
        def visit(node):
            if isinstance(node, dict):
                for value in node.values(): visit(value)
            elif isinstance(node, list):
                for value in list(node):
                    if isinstance(value,dict) and value.get('properties', {}).get('kind', {}).get('const') == 'native_operation':
                        self.branches.append((node,deepcopy(value),[]))
                        node.remove(value)
                    else:
                        visit(value)
        for tool in tools:
            if tool['function']['name'] in ('commit_plan', 'commit_steps'):
                visit(tool['function']['parameters'])
        self.refresh()

    def expose(self, name, schema):
        if name not in get_args(NativeOperation.model_fields['tool'].annotation):
            return
        contract = structured_tool(name, '', schema)['function']['parameters']
        if 'dryRun' in contract.get('properties', {}):
            # The gameplay gateway refuses implicit preview/write defaults.
            # Advertise that same requirement to the model before commitment.
            contract['required'] = list(dict.fromkeys([*contract.get('required', []), 'dryRun']))
            contract['properties']['dryRun'].pop('default', None)
        for key in ('watch','godMode','ultraSpeedBoost'):
            if key in contract.get('properties',{}):
                contract['properties'][key].update(const=False,default=False)
        if name=='home/pawn_config':
            contract.get('properties',{}).pop('drop',None)
        if name == 'home/bills' and 'action' in contract.get('properties', {}):
            action = contract['properties']['action']
            action['enum'] = list(BILL_WRITE_ACTIONS)
            action.pop('default', None)
            contract['required'] = list(dict.fromkeys([*contract.get('required', []), 'action']))
        self.contracts[name] = contract
        self.refresh()

    def refresh(self):
        for target, original, installed in self.branches:
            target[:] = [branch for branch in target if not any(branch is old for old in installed)]
            installed.clear()
            alternatives = []
            for name, schema in sorted(self.contracts.items()):
                branch = deepcopy(original)
                branch['properties']['tool'] = {'type':'string', 'const':name}
                branch['properties']['arguments'] = deepcopy(schema)
                alternatives.append(branch)
            target.extend(alternatives)
            installed.extend(alternatives)
