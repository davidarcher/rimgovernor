import sqlite3
import pytest
from rimbot.colony_plan import ColonyPlan,CommitSteps,PlanStep,StepProgress,ColonyGoal
from rimbot.plan_archive import bind_archive,prepare_archive,finish_archive
from rimbot.store import Store
from rimbot.planner import inspect_plan


def action(identity):
    return PlanStep(id=identity,title=identity,source='AUTOPILOT',goal_id='EnsureWorkAssignments',
        completion_criteria='Native receipt',action={'kind':'native_operation','tool':'home/pawn_config',
        'arguments':{'pawn':'Thing_Human'+identity,'work':'Growing=1','dryRun':False}})


def retired_plan():
    plan=ColonyPlan()
    for index in range(73):
        step=action('action-'+str(index))
        plan.commit(CommitSteps(expected_revision=plan.revision,reason='work',steps=[step]).decision(plan),actor='strategist',tick=index)
        plan.progress[step.id]=StepProgress(state='complete',issued={'0':{'confirmed':True,'receipt':{'id':step.id}}})
    plan.colony_goals['EnsureWorkAssignments']=ColonyGoal(priority_class=2,steps=list(plan.progress))
    return plan


def persist(store,plan):
    bind_archive(plan,store,'colony')
    snapshot,records=prepare_archive(plan)
    store.archive_and_set('colony','plan',snapshot,records)
    finish_archive(plan,snapshot,records)


def test_archive_preserves_receipts_across_sqlite_backup_and_rejects_identity_reuse(tmp_path):
    store=Store(tmp_path/'state.sqlite');plan=retired_plan()
    old=plan.progress['action-0'].model_dump()
    persist(store,plan)
    assert len(plan.progress)==1 and plan.colony_goals['EnsureWorkAssignments'].steps==['action-72']
    assert store.retired_action('colony','action-0')['progress']==old
    assert 'action-0' in inspect_plan(plan,['action-0'])['archived_steps']
    with sqlite3.connect(tmp_path/'backup.sqlite') as backup:store.db.backup(backup)
    store.close()
    store=Store(tmp_path/'backup.sqlite')
    restored=ColonyPlan.model_validate(store.get('plan'));bind_archive(restored,store,'colony')
    with pytest.raises(ValueError,match='Retired action identity'):
        restored.commit(CommitSteps(expected_revision=restored.revision,reason='replay',steps=[action('action-0')]).decision(restored),actor='strategist',tick=100)
    assert store.retired_action('colony','action-0')['progress']==old
    assert store.retired_action('different-colony','action-0') is None
    store.close()


def test_snapshot_write_failure_rolls_back_archive_without_compacting_live_state(tmp_path):
    store=Store(tmp_path/'state.sqlite');plan=retired_plan();before=plan.model_dump()
    store.db.execute("CREATE TRIGGER fail_state BEFORE INSERT ON state BEGIN SELECT RAISE(ABORT,'disk failure'); END")
    with pytest.raises(sqlite3.IntegrityError,match='disk failure'):persist(store,plan)
    assert plan.model_dump()==before
    assert store.retired_action('colony','action-0') is None
    assert store.get('plan') is None
    store.db.execute('DROP TRIGGER fail_state')
    persist(store,plan)
    assert store.get('plan')==plan.model_dump()
    store.close()


def test_missing_archive_blocks_replay_and_combat_references_stay_live(tmp_path):
    store=Store(tmp_path/'state.sqlite');plan=retired_plan()
    plan.control['combat']={'steps':['action-0']}
    persist(store,plan)
    assert 'action-0' in plan.progress and 'action-0' in plan.control['retired_steps']
    assert store.retired_action('colony','action-0') is None
    restored=ColonyPlan.model_validate(plan.model_dump())
    with pytest.raises(ValueError,match='archive is unavailable'):
        restored.commit(CommitSteps(expected_revision=restored.revision,reason='new',steps=[action('new')]).decision(restored),actor='strategist',tick=100)
    store.db.execute('DELETE FROM retired_actions');store.db.commit()
    with pytest.raises(ValueError,match='archive is missing'):bind_archive(restored,store,'colony')
    store.close()


def test_archival_preserves_pending_receipts_and_refuses_changed_history(tmp_path):
    store=Store(tmp_path/'state.sqlite');plan=retired_plan()
    plan.progress['action-72'].state='waiting'
    pending=plan.progress['action-72'].model_dump()
    persist(store,plan)
    assert plan.progress['action-72'].model_dump()==pending
    original=store.get('plan')
    altered=store.retired_action('colony','action-0')
    altered['progress']['issued']['0']['receipt']={'id':'different'}
    with pytest.raises(ValueError,match='record changed'):
        store.archive_and_set('colony','plan',{'invalid':'replacement'},{'action-0':altered})
    assert store.get('plan')==original
    assert store.retired_action('colony','action-0')['progress']['issued']['0']['receipt']=={'id':'action-0'}
    store.close()
