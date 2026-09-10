from copy import deepcopy
import pytest
from rimgovernor.colony_plan import ColonyPlan,CommitSteps,Decision,PlanSpec,PlanStep


def cancelled_plan():
    plan=ColonyPlan()
    step=PlanStep(id='room',title='Player room',source='PLAYER',
        action={'kind':'build_room_shell','bounds':{'x':10,'z':10,'width':5,'height':5},
                'wall_def':'Wall','door_def':'Door','materials':['WoodLog'],'entrance':'south'},
        completion_criteria='Shell built')
    plan.commit(CommitSteps(expected_revision=0,reason='Room',steps=[step]).decision(plan),actor='strategist',tick=100)
    plan.progress['room'].state='waiting'
    plan.progress['room'].issued={'0':{'confirmed':True,'native_outcome':'blueprint'}}
    plan.cancel('room')
    return ColonyPlan.model_validate_json(plan.model_dump_json())


def test_cancelled_partial_work_does_not_block_unrelated_commit_after_restore():
    plan=cancelled_plan();before=deepcopy(plan.progress['room'])
    research=PlanStep(id='research',title='Research',source='PLAYER',
        action={'kind':'native_operation','tool':'home/research','arguments':{'set':'ColoredLights','dryRun':False}},
        completion_criteria='Research selected')
    plan.commit(CommitSteps(expected_revision=plan.revision,reason='Research',steps=[research]).decision(plan),
        actor='strategist',tick=101)
    assert plan.progress['room']==before
    assert [s.id for s in plan.ready()]==['research']
    assert plan.cancelled_ids==['room']


@pytest.mark.parametrize('change',['id','action','source','removed'])
def test_cancelled_work_cannot_be_reintroduced_or_changed(change):
    plan=cancelled_plan();room=plan.spec.steps[0].model_copy(deep=True)
    if change=='id':room.id='new-room'
    elif change=='action':room.action.bounds.x+=1
    elif change=='source':room.source='AUTOPILOT'
    elif change=='removed':
        plan.commit(Decision(expected_revision=plan.revision,disposition='revise',assessment='Remove from active plan',
            rationale='Preserve cancellation',reply='Retained history',plan=PlanSpec()),actor='strategist',tick=101)
    before=plan.model_dump()
    proposal=Decision(expected_revision=plan.revision,disposition='revise',assessment='Attempt revision',
        rationale='Attempt revision',reply='Attempt revision',plan=PlanSpec(steps=[room]))
    with pytest.raises(ValueError,match='cancelled'):plan.commit(proposal,actor='strategist',tick=102)
    assert plan.model_dump()==before
