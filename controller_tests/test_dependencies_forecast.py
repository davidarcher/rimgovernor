from unittest.mock import AsyncMock
import pytest
from rimbot.project_dependencies import validate_graph,check_dependencies
from rimbot.food_forecast import forecast
from rimbot.contracts import Action


def test_dependency_graph_rejects_cycles_missing_and_duplicate_references():
    projects=[{'project_id':'a'},{'project_id':'b','after_projects':['a']}]
    validate_graph(projects,{'c':['b']})
    for change in ({'a':['b']},{'c':['missing']},{'c':['a','a']},{'a':['a']}):
        with pytest.raises(ValueError):validate_graph(projects,change)


async def test_dependency_rechecks_native_outcome_and_preserves_project(colony):
    rt,_=colony
    parent={'project_id':'a','outcome':'Workshop','work_ids':['w'],'status':'orders_verified'}
    child={'project_id':'b','outcome':'Production','after_projects':['a']}
    rt.memory['projects']=[parent,child]
    action=Action(title='Build',endpoint='post_work_settings',arguments={'use_work_priorities':True})
    rt.memory['work']=[{'id':'w','status':'complete','action':action.model_dump()}]
    rt.action_complete=AsyncMock(return_value=False)
    assert not await check_dependencies(rt,child)
    rt.action_complete.return_value=True
    assert await check_dependencies(rt,child)
    parent['cancelled_by_player']=True
    assert not await check_dependencies(rt,child)
    assert len(rt.memory['projects'])==2 and parent['work_ids']==['w']


async def test_transitive_prerequisite_failure_and_hold_block_descendants(colony):
    rt,_=colony
    action=Action(title='Setting',endpoint='post_work_settings',arguments={'use_work_priorities':True})
    a={'project_id':'a','outcome':'Workshop','work_ids':['a-work'],'status':'orders_verified'}
    b={'project_id':'b','outcome':'Production','work_ids':['b-work'],'status':'orders_verified','after_projects':['a']}
    c={'project_id':'c','outcome':'Export','after_projects':['b']}
    rt.memory['projects']=[a,b,c]
    rt.memory['work']=[{'id':identity,'status':'complete','action':action.model_dump()} for identity in ('a-work','b-work')]
    rt.action_complete=AsyncMock(return_value=False)
    assert not await check_dependencies(rt,c)
    assert 'Workshop' in c['dependency_blockers'][0]
    assert rt.action_complete.await_count==1
    a['admin_hold']={'reason':'Hold'}
    rt.action_complete.reset_mock()
    assert not await check_dependencies(rt,c)
    rt.action_complete.assert_not_awaited()
    a.pop('admin_hold');rt.action_complete.return_value=True
    assert await check_dependencies(rt,c)
    assert all(row['verified'] for row in c['dependency_checks']['projects'])


async def test_shared_ancestor_checked_once_per_pass_and_again_next_pass(colony):
    rt,_=colony
    action=Action(title='Setting',endpoint='post_work_settings',arguments={'use_work_priorities':True})
    parents=[{'project_id':key,'outcome':key,'work_ids':[key],'status':'orders_verified',
              'after_projects':[] if key=='a' else ['a']} for key in ('a','b','c')]
    root={'project_id':'d','outcome':'Final','after_projects':['b','c']}
    rt.memory['projects']=parents+[root]
    rt.memory['work']=[{'id':p['project_id'],'status':'complete','action':action.model_dump()} for p in parents]
    rt.action_complete=AsyncMock(return_value=True)
    assert await check_dependencies(rt,root)
    assert rt.action_complete.await_count==3
    rt.action_complete.return_value=False
    assert not await check_dependencies(rt,root)
    assert rt.action_complete.await_count==4


async def test_persisted_cycle_is_blocked_without_native_writes_or_recursion(colony):
    rt,_=colony
    a={'project_id':'a','outcome':'A','after_projects':['b']}
    b={'project_id':'b','outcome':'B','after_projects':['a']}
    rt.memory['projects']=[a,b];rt.action_complete=AsyncMock()
    assert not await check_dependencies(rt,a)
    assert 'cycle' in a['dependency_blockers'][0]
    rt.action_complete.assert_not_awaited()


def test_prerequisites_are_scheduled_before_dependants_in_same_pass():
    from rimbot.project_dependencies import order_projects
    child={'project_id':'b','after_projects':['a']};parent={'project_id':'a'}
    assert order_projects([child,parent])==[parent,child]


async def test_deadline_requests_review_without_cancelling(colony):
    rt,_=colony
    project={'project_id':'a','outcome':'Winter food','deadline_tick':rt.last_tick-1,'status':'approved'}
    assert await check_dependencies(rt,project)
    assert project['deadline_overdue'] and project['status']=='approved'
    assert any('Winter food' in reason for reason in rt.memory['admin_requested'].values())


def food(tick,nutrition=100,population=8):
    return {'game':{'game_tick':tick,'colonist_count':population},'resources':{'critical_resources':{
        'food_summary':{'total_nutrition':nutrition,'unforbidden_nutrition':nutrition,'forbidden_nutrition':0}}}}


def test_food_trend_requires_game_time_and_is_not_metabolic_runway():
    memory={}
    for _ in range(20):assert forecast(memory,food(0))['stock_depletion_days'] is None
    assert len(memory['food_samples'])==1
    for tick in range(2500,15001,2500):result=forecast(memory,food(tick,100-tick/1500))
    assert result['net_loss_per_day']==40 and result['stock_depletion_days']==2.25
    assert result['food_runway_days'] is None
    assert forecast(memory,food(17500,90,population=9))['stock_depletion_days'] is None
    assert len(memory['food_samples'])==1


def test_food_missing_growing_stock_and_rewind_do_not_invent_deadlines():
    memory={}
    for tick in range(0,15001,2500):result=forecast(memory,food(tick,100+tick/1500))
    assert result['stock_depletion_days'] is None and result['net_loss_per_day']<0
    assert forecast(memory,{'game':{'game_tick':16000}})['stock_depletion_days'] is None
    assert forecast(memory,food(0))['stock_depletion_days'] is None
    assert len(memory['food_samples'])==1
