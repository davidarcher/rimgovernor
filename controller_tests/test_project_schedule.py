from rimbot.project_schedule import execution_due, record_attempt, REVIEW_TICKS, RETRY_TICKS
from rimbot.semantic import retain_project
from rimbot.semantic_models import WorkObjective
from rimbot.project_schedule import order_states, acknowledge_dispatch


def setup():
    memory={'work': [{'id':'w','status':'issued'}]}
    project=retain_project(memory,'Infrastructure',WorkObjective(kind='construction',outcome='Shelter',success_signals=['Roofed']))
    project.update(status='awaiting_work',work_ids=['w'],progress={'observed_tick':100,'sites':[{'state':'targeted','work_done':0}]})
    record_attempt(project,memory,100)
    return project,memory


def test_game_progress_does_not_replan_underway_work_or_use_wall_time():
    project,memory=setup()
    project['progress']['observed_tick']=500
    project['progress']['sites'][0]['work_done']=99
    assert not execution_due(project,memory,500)[0]
    assert not execution_due(project,memory,100)[0]
    assert execution_due(project,memory,100+REVIEW_TICKS)[0]


def test_new_receipts_are_acknowledged_but_later_completion_reopens():
    project,memory=setup()
    before=order_states(project,memory)
    project['work_ids'].append('new')
    memory['work'].append({'id':'new','status':'issued'})
    acknowledge_dispatch(project,memory,101,before)
    project['progress']['orders']=[{'title':'Old display copy','status':'issued'}]
    assert not execution_due(project,memory,102)[0]
    memory['work'][-1]['status']='complete'
    assert execution_due(project,memory,102)[0]


def test_existing_work_completion_during_model_turn_is_not_swallowed():
    project,memory=setup()
    before=order_states(project,memory)
    memory['work'][0]['status']='complete'
    project['work_ids'].append('new')
    memory['work'].append({'id':'new','status':'issued'})
    acknowledge_dispatch(project,memory,101,before)
    assert execution_due(project,memory,102)[0]


async def test_verified_instant_batch_does_not_invoke_executor_again(colony):
    from unittest.mock import AsyncMock
    from rimbot.semantic import execute_projects
    from rimbot.contracts import Proposal, Action
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    project=retain_project(rt.memory,'Survival',WorkObjective(
        kind='supply_access',outcome='Allow nearby timber',success_signals=['Timber allowed']))
    rt.planner.ask=AsyncMock(return_value=Proposal(summary='Allow timber',actions=[Action(
        title='Allow timber',endpoint='post_things_set_forbidden',
        arguments={'map_id':7,'thing_ids':[101],'forbidden':False})]))
    await execute_projects(rt,{},[project])
    assert project['status']=='orders_verified'
    assert not game.forbidden
    await execute_projects(rt,{},[project])
    rt.planner.ask.assert_awaited_once()


def test_completion_blocker_and_changed_intent_reopen_immediately():
    project,memory=setup()
    memory['work'][0]['status']='complete'
    assert execution_due(project,memory,100)[0]
    project,memory=setup()
    project['progress']['sites'][0]['state']='worker_blocked'
    assert execution_due(project,memory,100)[0]
    project,memory=setup()
    project['quantity']=8
    assert execution_due(project,memory,100)[0]


def test_failed_attempt_retries_on_game_time_and_player_can_interrupt():
    project,memory=setup()
    project['status']='needs_review'
    assert not execution_due(project,memory,100)[0]
    assert execution_due(project,memory,100+RETRY_TICKS)[0]
    assert execution_due(project,memory,100,force=True)[0]
    project['status']='retired'
    assert not execution_due(project,memory,100,force=True)[0]


def test_unchanged_approval_preserves_lifecycle_and_commitment():
    project,memory=setup()
    original=project['execution_review'].copy()
    retain_project(memory,'Infrastructure',WorkObjective(project_id=project['project_id'],kind='construction',outcome='Shelter',success_signals=['Roofed']))
    assert project['status']=='awaiting_work'
    assert project['execution_review']==original
    assert not execution_due(project,memory,100)[0]


def test_growing_changes_are_scoped_to_crop_and_not_individual_plant_jobs():
    project,memory=setup()
    project.update(kind='growing',crop_def='TestCrop',target_cells=20,
                   progress={'matching_cells':20,'zones':[
                       {'zone_id':1,'crop':'TestCrop','cells':20,'plants_present':1,'growth_progress':0.1,'sowing_allowed':True},
                       {'zone_id':2,'crop':'OtherCrop','cells':10,'plants_present':0}],
                       'zone_count':2,'designated_cells':30})
    record_attempt(project,memory,100)
    project['progress']['zones'][0].update(plants_present=19,growth_progress=0.7)
    project['progress']['zones'][1]['cells']=50
    project['progress'].update(zone_count=3,designated_cells=80)
    assert not execution_due(project,memory,500)[0]
    assert execution_due(project,memory,100+REVIEW_TICKS)[0]
    project['progress']['zones'][0]['sowing_allowed']=False
    assert execution_due(project,memory,500)[0]
    record_attempt(project,memory,500)
    project['progress']['zones']=[]
    assert execution_due(project,memory,501)[0]


async def test_no_orders_needed_waits_without_claiming_goal_complete(colony):
    from unittest.mock import AsyncMock
    from rimbot.semantic import execute_projects
    from rimbot.contracts import Proposal
    rt,_=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    project=retain_project(rt.memory,'Infrastructure',WorkObjective(
        kind='research',outcome='Develop technology',success_signals=['Technology available']))
    rt.planner.ask=AsyncMock(return_value=Proposal(summary='Research already selected; observe progress.'))
    await execute_projects(rt,{},[project])
    assert project['status']=='awaiting_work'
    await execute_projects(rt,{},[project])
    rt.planner.ask.assert_awaited_once()
    assert not execution_due(project,rt.memory,(rt.last_tick or 0)+RETRY_TICKS)[0]
    assert execution_due(project,rt.memory,(rt.last_tick or 0)+REVIEW_TICKS)[0]
    rt.planner.ask=AsyncMock(return_value=Proposal(summary='Blocked',blockers=['No eligible research project observed']))
    await execute_projects(rt,{'administration_required':True},[project])
    assert project['status']=='needs_review'
    assert execution_due(project,rt.memory,(rt.last_tick or 0)+RETRY_TICKS)[0]
