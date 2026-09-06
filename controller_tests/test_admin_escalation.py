import pytest
from rimbot.semantic import arbitrate_objectives
from rimbot.semantic_models import ObjectiveDecision
from rimbot.model import ModelError

async def test_admin_failure_does_not_call_another_model(colony):
    rt,_=colony;calls=[]
    async def ask(*args):calls.append(args[0]);raise ModelError('Invalid decision')
    rt.planner.ask=ask
    with pytest.raises(ModelError):await arbitrate_objectives(rt,{'projects':[],'proposals':{}})
    assert len(calls)==1

def test_administrator_has_no_escalation_field():
    assert 'escalation_reason' not in ObjectiveDecision.model_json_schema()['properties']
    with pytest.raises(ValueError):ObjectiveDecision(response='Wait',escalation_reason='Need a bigger model')
