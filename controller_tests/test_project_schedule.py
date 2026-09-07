from rimbot.project_schedule import execution_due, record_attempt, REVIEW_TICKS, RETRY_TICKS
from rimbot.semantic import retain_project
from rimbot.semantic_models import WorkObjective


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
