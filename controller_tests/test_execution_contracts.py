from copy import deepcopy
import pytest
from jsonschema import Draft202012Validator
from rimbot.colony_plan import CommitSteps
from rimbot.consultation import structured_tool
from rimbot.execution_contracts import ExecutionContracts


def test_native_argument_schema_is_discovered_not_an_open_dictionary():
    tools=[structured_tool('commit_steps','Append',CommitSteps.model_json_schema())]
    bindings=ExecutionContracts(tools)
    schema=tools[0]['function']['parameters']
    request={'expected_revision':0,'reason':'Work','steps':[{'id':'work','title':'Work',
        'completion_criteria':'Observed','action':{'kind':'native_operation',
        'tool':'home/pawn_config','arguments':{'pawn':'Thing_1','work':{'Construction':1}}}}]}
    assert not Draft202012Validator(schema).is_valid(request)
    native={'type':'object','properties':{'pawn':{'type':'string'},'work':{'type':'object',
        'additionalProperties':{'type':'integer','minimum':0,'maximum':4}}},
        'required':['pawn'],'additionalProperties':False}
    original=deepcopy(native)
    bindings.expose('home/pawn_config',native)
    Draft202012Validator.check_schema(schema)
    assert Draft202012Validator(schema).is_valid(request)
    request['steps'][0]['action']['arguments']={'jobs':[]}
    assert not Draft202012Validator(schema).is_valid(request)
    bindings.expose('home/pawn_config',native)
    assert native==original
    request['steps'][0]['action']['arguments']={'pawn':'Thing_1'}
    assert Draft202012Validator(schema).is_valid(request) # no duplicate oneOf branch
    request['steps'][0]['action']['tool']='home/bills'
    assert not Draft202012Validator(schema).is_valid(request)


def test_semantic_construction_remains_available_before_native_discovery():
    tools=[structured_tool('commit_steps','Append',CommitSteps.model_json_schema())]
    ExecutionContracts(tools)
    request={'expected_revision':0,'reason':'Sleep','steps':[{'id':'sleep','title':'Sleep',
        'completion_criteria':'Built','action':{'kind':'place_buildings',
        'placements':[{'def_name':'SleepingSpot','x':1,'z':1}]}}]}
    Draft202012Validator(tools[0]['function']['parameters']).validate(request)


def test_execution_schema_preserves_gateway_policy_and_tool_specific_fields():
    tools=[structured_tool('commit_steps','Append',CommitSteps.model_json_schema())]
    bindings=ExecutionContracts(tools)
    bindings.expose('home/pawn_config',{'type':'object','additionalProperties':False,
        'properties':{'pawn':{'type':'string'},'watch':{'type':'boolean'},'drop':{'type':'string'}}})
    bindings.expose('home/bills',{'type':'object','additionalProperties':False,
        'properties':{'recipe':{'type':'string'}},'required':['recipe']})
    request={'expected_revision':0,'reason':'Configure','steps':[{'id':'x','title':'Configure',
        'completion_criteria':'Observed','action':{'kind':'native_operation','tool':'home/pawn_config',
        'arguments':{'pawn':'Thing_1','watch':False}}}]}
    valid=lambda:Draft202012Validator(tools[0]['function']['parameters']).is_valid(request)
    assert valid()
    request['steps'][0]['action']['arguments']['watch']=True
    assert not valid()
    request['steps'][0]['action']['arguments']={'drop':'shirt'}
    assert not valid()
    request['steps'][0]['action'].update(tool='home/bills',arguments={'recipe':'ObservedRecipe'})
    assert valid()
    request['steps'][0]['action']['arguments']={'pawn':'Thing_1'}
    assert not valid()


def test_bill_commitment_requires_explicit_mutation_without_changing_inspection_schema():
    tools=[structured_tool('commit_steps','Append',CommitSteps.model_json_schema())]
    bindings=ExecutionContracts(tools)
    native={'type':'object','properties':{'action':{'type':'string','default':'list'},
        'bench':{'type':'string'}},'additionalProperties':False}
    original=deepcopy(native)
    bindings.expose('home/bills',native)
    args={'bench':'ButcherSpot1'}
    request={'expected_revision':0,'reason':'Bill','steps':[{'id':'bill','title':'Bill',
        'completion_criteria':'Observed','action':{'kind':'native_operation',
        'tool':'home/bills','arguments':args}}]}
    validator=Draft202012Validator(tools[0]['function']['parameters'])
    assert not validator.is_valid(request)
    for action in ('list','recipes'):
        args['action']=action
        assert not validator.is_valid(request)
    for action in ('add','set','delete','move'):
        args['action']=action
        validator.validate(request)
    assert native==original
