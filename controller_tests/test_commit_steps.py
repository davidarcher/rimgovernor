import pytest
from rimbot.colony_plan import ColonyPlan, PlanSpec, CommitSteps


def request():
    return CommitSteps(expected_revision=4,reason='Start storage while planning shelter',steps=[{
        'id':'stores','title':'Stores','completion_criteria':'Zone exists','action':{
            'kind':'create_zone','zone_type':'stockpile','label':'Stores','patches':[{'x':10,'z':10,'width':3,'height':3}]}}])


def test_append_preserves_strategy_and_does_not_mutate_current_plan():
    current=ColonyPlan(revision=4,spec=PlanSpec(long_term='Winter-ready',goals=['Food']))
    decision=request().decision(current)
    assert decision.plan.long_term=='Winter-ready' and decision.plan.goals==['Food']
    assert len(decision.plan.steps)==1 and not current.spec.steps
    assert decision.expected_revision==4


def test_append_cannot_overwrite_existing_work():
    current=ColonyPlan(spec=request().decision(ColonyPlan()).plan)
    with pytest.raises(ValueError,match='existing work'):request().decision(current)
