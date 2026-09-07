from types import SimpleNamespace
from unittest.mock import Mock
import pytest
from rimbot.project_controls import validate_controls,apply_controls
from rimbot.semantic_models import ObjectiveDecision
from rimbot.world_model import sync_interrupts

def runtime():
    return SimpleNamespace(memory={'projects':[{'project_id':'p','kind':'construction','status':'awaiting_work','work_ids':['wall']}],
                                  'risk_state':{}},note=Mock())

def test_hold_persists_and_resume_preserves_work_and_reopens_review():
    rt=runtime();p=rt.memory['projects'][0]
    hold=ObjectiveDecision(response='Wait',suspend_projects={'p':'Save labor for harvest'})
    validate_controls(hold,[p]);apply_controls(rt,hold);sync_interrupts(rt)
    apply_controls(rt,ObjectiveDecision(response='Keep',keep_projects=['p']))
    assert p['status']=='suspended' and p['work_ids']==['wall']
    p['execution_review']={'tick':1}
    resume=ObjectiveDecision(response='Continue',resume_projects=['p'])
    validate_controls(resume,[p]);apply_controls(rt,resume);sync_interrupts(rt)
    assert p['status']=='awaiting_work' and p['work_ids']==['wall']
    assert 'admin_hold' not in p and 'execution_review' not in p

def test_medical_recovery_cannot_release_administrator_hold():
    rt=runtime();p=rt.memory['projects'][0]
    rt.memory['risk_state']={'medical_emergency':{'active':True}};sync_interrupts(rt)
    apply_controls(rt,ObjectiveDecision(response='Wait',suspend_projects={'p':'Resources needed elsewhere'}))
    rt.memory['risk_state']['medical_emergency']['active']=False;sync_interrupts(rt)
    assert p['status']=='suspended' and p['admin_hold']
    apply_controls(rt,ObjectiveDecision(response='Continue',resume_projects=['p']));sync_interrupts(rt)
    assert p['status']=='awaiting_work' and 'interruption' not in p

def test_administrator_release_does_not_bypass_active_medical_interruption():
    rt=runtime();p=rt.memory['projects'][0]
    apply_controls(rt,ObjectiveDecision(response='Wait',suspend_projects={'p':'Hold'}))
    rt.memory['risk_state']={'medical_emergency':{'active':True}}
    apply_controls(rt,ObjectiveDecision(response='Continue',resume_projects=['p']));sync_interrupts(rt)
    assert p['status']=='suspended' and p['interruption']['resume_status']=='awaiting_work'

@pytest.mark.parametrize('fields',[
    {'suspend_projects':{'missing':'Hold'}}, {'suspend_projects':{'p':' '}},
    {'suspend_projects':{'p':'Hold'},'retire_projects':{'p':'Obsolete'}},
    {'suspend_projects':{'p':'Hold'},'resume_projects':['p']}, {'resume_projects':['p']},
])
def test_invalid_controls_rejected_before_mutation(fields):
    rt=runtime();p=rt.memory['projects'][0]
    with pytest.raises(ValueError):validate_controls(ObjectiveDecision(response='Review',**fields),[p])
    assert p['status']=='awaiting_work'

async def test_held_project_does_not_invoke_executor(colony):
    from unittest.mock import AsyncMock
    from rimbot.semantic import retain_project,execute_projects
    from rimbot.semantic_models import WorkObjective
    rt,_=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    p=retain_project(rt.memory,'Development',WorkObjective(kind='research',outcome='Develop technology',success_signals=['Technology unlocked']))
    apply_controls(rt,ObjectiveDecision(response='Wait',suspend_projects={p['project_id']:'Focus on food'}))
    rt.planner.ask=AsyncMock(side_effect=AssertionError('Held project must not invoke executor'))
    await execute_projects(rt,{},[p])
    rt.planner.ask.assert_not_awaited()
    assert p['status']=='suspended'
