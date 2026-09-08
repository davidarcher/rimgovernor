import json
import pytest
from rimbot.bridge_game import for_model
from rimbot.colony_plan import RoomShell
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


def test_catalog_index_includes_definitions_beyond_first_detail_page():
    rows=[{'id':f'custom:{i}','buildableDefName':f'Custom{i}','label':f'Building {i}'} for i in range(12)]
    result=for_model({'designators':rows},tool='rimworld/list_architect_designators')
    assert len(result['designators'])==8
    assert [r['defName'] for r in result['buildable_index']]==[r['buildableDefName'] for r in rows]
    assert [r['id'] for r in result['designator_index']]==[r['id'] for r in rows]
    with pytest.raises(ValueError,match='distinct wall'):
        RoomShell(bounds={'x':1,'z':1,'width':5,'height':5},wall_def='ModDoor',door_def='ModDoor',materials=['Wood'],entrance='south')
