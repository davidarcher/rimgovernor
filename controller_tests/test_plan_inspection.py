import pytest
from rimgovernor.colony_plan import ColonyPlan, Decision, PlanSpec, PlanStep, StepProgress, Zone, Rectangle
from rimgovernor.config import ModelRole
from rimgovernor.planner import inspect_plan


def test_inspection_exposes_revision_progress_and_unknown_ids():
    step = PlanStep(id='store', title='Stockpile', completion_criteria='Zone exists',
        action=Zone(zone_type='stockpile', label='Stores', patches=[Rectangle(x=5,z=5,width=2,height=2)]))
    plan = ColonyPlan(revision=7, spec=PlanSpec(steps=[step]),
        progress={'store': StepProgress(state='complete')})
    index = inspect_plan(plan, [])
    assert index == dict(revision=7, step_ids=['store'], steps=[], archived_steps={}, missing_ids=[])
    details = inspect_plan(plan, ['store', 'missing'])
    assert details['steps'][0]['progress']['state'] == 'complete'
    assert details['missing_ids'] == ['missing']


def test_revision_rejection_identifies_current_revision_without_mutating():
    plan = ColonyPlan(revision=7)
    decision = Decision(expected_revision=8, disposition='revise', plan=PlanSpec(goals=['Shelter']),
        assessment='Shelter', rationale='Needed', reply='Building shelter')
    before = plan.model_dump()
    with pytest.raises(ValueError, match='current plan revision is 7'):
        plan.commit(decision, actor=ModelRole.STRATEGIST, tick=1)
    assert plan.model_dump() == before
