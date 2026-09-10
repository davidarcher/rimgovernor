from copy import deepcopy
import pytest
from jsonschema import Draft202012Validator, ValidationError
from rimgovernor.colony_plan import Decision
from rimgovernor.consultation import structured_tool
from rimgovernor.model import inference_tools


def test_wire_contract_requires_every_action_discriminator():
    original=Decision.model_json_schema()
    saved=deepcopy(original)
    tool=structured_tool('commit_plan','Commit',original)
    assert original==saved
    def check(node):
        if isinstance(node,dict):
            if 'discriminator' in node:
                tag=node['discriminator']['propertyName']
                for branch in node['oneOf']:
                    assert tag in branch['required']
            for value in node.values():check(value)
        elif isinstance(node,list):
            for value in node:check(value)
    check(tool)
    check(inference_tools([tool]))


def test_live_missing_kind_failure_is_rejected_before_pydantic():
    schema=structured_tool('commit_plan','Commit',Decision.model_json_schema())['function']['parameters']
    # Find the actual action union in the advertised contract.
    def union(node):
        if isinstance(node,dict):
            if node.get('discriminator',{}).get('propertyName')=='kind':return node
            for value in node.values():
                found=union(value)
                if found:return found
        elif isinstance(node,list):
            for value in node:
                found=union(value)
                if found:return found
    validator=Draft202012Validator(union(schema))
    action={'zone_type':'stockpile','label':'Supplies','patches':[{'x':10,'z':10,'width':2,'height':2}]}
    with pytest.raises(ValidationError):validator.validate(action)
    validator.validate(dict(action,kind='create_zone'))
