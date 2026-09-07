from types import SimpleNamespace
from unittest.mock import Mock,AsyncMock
from rimbot.world_model import derive,transition,update,sync_interrupts


def observation(tick=100,urgent=False):
    return {'game':{'game_tick':tick,'colonist_count':1},'pawns':[{'colonist':{'id':11},
        'colonist_medical_info':{'is_dead':False,'is_downed':False,'hediffs':[
            {'is_permanent':True,'can_ever_kill':True,'is_currently_life_threatening':urgent,'tendable_now':False}]}}]}


def test_native_current_risk_is_not_inferred_from_permanent_injury_or_missing_data():
    facts=derive(observation())
    assert facts['medical']['urgent_pawn_ids']==[]
    assert facts['medical']['missing_pawns']==0
    assert 'unavailable' in facts['power']
    assert derive(observation(urgent=True))['medical']['urgent_pawn_ids']==[11]
    assert derive({'game':{'colonist_count':3},'pawns':[]})['medical']['missing_pawns']==3


def test_hysteresis_uses_game_time_unknown_is_not_recovery():
    state={}
    assert transition(state,'medical',True,100,['Survival'],True)['urgent']
    assert transition(state,'medical',True,110,['Survival'],True) is None
    assert transition(state,'medical',False,120,['Survival'],True) is None
    assert transition(state,'medical',False,120,['Survival'],True) is None
    assert transition(state,'medical',None,400,['Survival'],True) is None
    assert state['medical']['active']
    assert transition(state,'medical',False,500,['Survival'],True) is None
    event=transition(state,'medical',False,750,['Survival'],True)
    assert not event['data']['active'] and not event['urgent']


def test_interrupt_preserves_work_and_resumes_without_recreating_projects():
    project={'project_id':'base','kind':'construction','status':'awaiting_work','work_ids':['wall']}
    care={'project_id':'care','kind':'care','status':'approved','work_ids':[]}
    rt=SimpleNamespace(memory={'projects':[project,care]},observation=observation(urgent=True),note=Mock())
    events=update(rt)
    assert events[0]['roles']==['Survival'] and project['status']=='suspended'
    assert care['status']=='approved' and project['work_ids']==['wall']
    # Cancellation/failure of an in-flight LLM review must not undo the suspension.
    project['status']='needs_review';sync_interrupts(rt)
    assert project['status']=='suspended' and project['interruption']['resume_status']=='needs_review'
    rt.observation=observation(200);update(rt)
    rt.observation=observation(450);update(rt)
    assert project['status']=='needs_review' and project['work_ids']==['wall']
    assert 'interruption' not in project and len(rt.memory['projects'])==2


def test_distant_hostiles_do_not_suspend_work():
    project={'project_id':'base','kind':'construction','status':'approved'}
    obs=observation();obs['hostiles']=[{'type':'insect','distance':150}]
    rt=SimpleNamespace(memory={'projects':[project]},observation=obs,note=Mock())
    assert update(rt)==[] and project['status']=='approved'


async def test_targeted_trigger_does_not_wake_every_manager(colony):
    rt,_=colony
    rt.store.set('full_review:'+rt.colony,rt.last_review)
    event={'type':'derived_risk','roles':['Infrastructure'],'urgent':False}
    assert rt.review_roles([event])==['Infrastructure']
    event.update(roles=['Survival'],urgent=True)
    assert rt.review_roles([event])==['Survival']
    assert len(rt.review_roles([event],steering=True))>1


async def test_urgent_review_bypasses_seasonal_and_daily_planning(colony,monkeypatch):
    rt,_=colony
    rt.events_pending=[{'type':'derived_risk','roles':['Survival'],'urgent':True}]
    rt.planner.ask=AsyncMock(side_effect=AssertionError('Urgent care must not wait for long-term planning'))
    review=AsyncMock()
    monkeypatch.setattr('rimbot.runtime.semantic_review',review)
    await rt.review()
    review.assert_awaited_once()
    assert review.call_args.args[2]==['Survival']
    assert review.call_args.args[1]['administration_required']
