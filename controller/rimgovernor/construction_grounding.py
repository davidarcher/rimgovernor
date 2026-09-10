"""Bind construction commitments to definitions actually shown during this review."""
def ground_construction(tools, definitions):
    if not definitions:return
    choices=sorted(definitions)
    def visit(node):
        if isinstance(node,list):
            for child in node:visit(child)
        elif isinstance(node,dict):
            for name,prop in node.get('properties',{}).items():
                if name in ('def_name','wall_def','door_def') and prop.get('type')=='string':
                    prop['enum']=choices
            for child in node.values():visit(child)
    for tool in tools:
        if tool['function']['name'] in ('commit_plan','commit_steps'):
            visit(tool['function']['parameters'])
