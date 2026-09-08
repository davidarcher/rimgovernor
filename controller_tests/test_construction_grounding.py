import json
from rimbot.colony_plan import CommitSteps
from rimbot.consultation import structured_tool
from rimbot.construction_grounding import ground_construction


def test_discovered_mod_definitions_update_commitment_choices_without_touching_native_tools():
    tools=[structured_tool('commit_steps','Append',CommitSteps.model_json_schema()),
           structured_tool('native_tool','Native',{'type':'object','properties':{'def_name':{'type':'string'}}})]
    ground_construction(tools,{'ModdedDoor','ModdedWall'})
    def enums(node):
        if isinstance(node,dict):
            for name,prop in node.get('properties',{}).items():
                if name in ('def_name','wall_def','door_def'):yield prop['enum']
            for value in node.values():yield from enums(value)
        elif isinstance(node,list):
            for value in node:yield from enums(value)
    assert list(enums(tools[0])) and all(x==['ModdedDoor','ModdedWall'] for x in enums(tools[0]))
    assert 'enum' not in tools[1]['function']['parameters']['properties']['def_name']
    ground_construction(tools,{'ModdedDoor','ModdedWall','NewBed'})
    assert all('NewBed' in x for x in enums(tools[0]))
