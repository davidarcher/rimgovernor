from copy import deepcopy
import pytest
from jsonschema import Draft202012Validator, ValidationError
from rimbot.native_inspections import NativeInspections


def test_research_discovery_exposes_reads_and_previews_without_write_authority():
    from rimbot.native_inspections import DESCRIBABLE
    assert 'home/research' in DESCRIBABLE and 'rimworld/set_time_speed' not in DESCRIBABLE
    schema={'type':'object','properties':{'set':{'type':'string'},'locked':{'type':'boolean'},
        'dryRun':{'type':'boolean','default':True}},'additionalProperties':False}
    tools=[];meta=NativeInspections().expose('home/research',schema,tools)
    assert meta['callable_tool']=='native_home__research'
    validator=Draft202012Validator(tools[0]['function']['parameters'])
    validator.validate({'locked':True})
    validator.validate({'set':'GeothermalPower','dryRun':True})
    with pytest.raises(ValidationError):validator.validate({'set':'GeothermalPower','dryRun':False})


def test_discovery_advertises_real_contract_once_without_mutating_it():
    registry=NativeInspections(); tools=[]
    schema={'type':'object','properties':{'category':{'type':'string','enum':['food','weapons']}},
            'required':['category'],'additionalProperties':False}
    original=deepcopy(schema)
    result=registry.expose('home/list_things',schema,tools)
    assert result['callable_tool']=='native_home__list_things'
    assert tools[0]['function']['parameters']==original
    registry.expose('home/list_things',schema,tools)
    assert len(tools)==1 and schema==original
    with pytest.raises(ValidationError):
        Draft202012Validator(tools[0]['function']['parameters']).validate({'category':'invented'})


def test_preview_grammar_refuses_writes_and_keeps_native_schema():
    schema={'type':'object','properties':{'dryRun':{'type':'boolean','default':False},'pawn':{'type':'string'}}}
    tools=[]; NativeInspections().expose('home/pawn_config',schema,tools)
    validator=Draft202012Validator(tools[0]['function']['parameters'])
    validator.validate({'dryRun':True,'pawn':'Thing_1'})
    with pytest.raises(ValidationError):validator.validate({'dryRun':False})
    assert schema['properties']['dryRun']=={'type':'boolean','default':False}


def test_execution_only_contract_is_not_exposed_as_callable():
    tools=[]
    result=NativeInspections().expose('rimworld/dismiss_letter',{'type':'object'},tools)
    assert result['execution_only'] and not tools
    with pytest.raises(ValueError):NativeInspections().expose('arbitrary/cheat',{},tools)


def test_catalog_continuation_matches_the_advertised_tool_schema():
    from rimbot.bridge_game import inspection_result
    tools=[]; registry=NativeInspections()
    schema={'type':'object','properties':{'categoryId':{'type':'string'}},
            'required':['categoryId'],'additionalProperties':False}
    meta=registry.expose('rimworld/list_architect_designators',schema,tools)
    page=inspection_result({'designators':[{'id':str(i)} for i in range(12)]},
        'rimworld/list_architect_designators',{'categoryId':'observed'},callable_name=meta['callable_tool'])
    call=page['catalog_page']['next_call']
    assert call['name']==tools[0]['function']['name']
    Draft202012Validator(tools[0]['function']['parameters']).validate(call['arguments'])
    assert call['arguments']=={'categoryId':'observed','catalog_offset':8}
    assert 'catalog_offset' not in schema['properties']
