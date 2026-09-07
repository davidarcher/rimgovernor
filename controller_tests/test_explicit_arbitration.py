from types import SimpleNamespace
import pytest
from rimbot.planner import Planner
from rimbot.semantic_models import ObjectiveDecision


def test_explanation_cannot_silently_replace_missing_decisions():
    planner=Planner(SimpleNamespace())
    decision=ObjectiveDecision(response='Accepted Survival:0. Defer construction.',deferred={'Infrastructure:0':'Wait'})
    context={'proposals':{'Survival:0':{},'Infrastructure:0':{}},'projects':[]}
    with pytest.raises(ValueError,match='Missing candidate IDs: Survival:0'):
        planner.validate_submission('Administrator',decision,context)
    assert 'Survival:0' not in decision.deferred


async def test_fully_explicit_deferral_remains_a_valid_decision(colony):
    planner=colony[0].planner
    decision=ObjectiveDecision(response='Wait for a safe route',deferred={'Survival:0':'Observed access blocked'})
    assert planner.validate_submission('Administrator',decision,{'proposals':{'Survival:0':{}},'projects':[]}) is decision
