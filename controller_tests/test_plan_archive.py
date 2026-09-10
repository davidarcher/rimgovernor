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
    snapshot,records,methods,evidence=prepare_archive(plan)
    store.archive_and_set('colony','plan',snapshot,records,methods,evidence)
    finish_archive(plan,snapshot,records,methods,evidence)


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


def test_method_deduplication_and_exact_steps_survive_backup(tmp_path):
    store=Store(tmp_path/'state.sqlite');plan=retired_plan()
    goal=plan.colony_goals['EnsureWorkAssignments']
    goal.evidence['methods']={f'assign-{i}':[f'action-{i}'] for i in range(73)}
    persist(store,plan)
    assert goal.archived_methods==72 and goal.evidence['methods']=={'assign-72':['action-72']}
    assert all(goal.method_seen(f'assign-{i}') for i in range(73))
    with sqlite3.connect(tmp_path/'backup.sqlite') as backup:store.db.backup(backup)
    store.close();store=Store(tmp_path/'backup.sqlite')
    restored=ColonyPlan.model_validate(store.get('plan'))
    goal=restored.colony_goals['EnsureWorkAssignments']
    with pytest.raises(ValueError,match='method archive is unavailable'):goal.method_seen('assign-0')
    bind_archive(restored,store,'colony')
    assert goal.method_seen('assign-0') and not goal.method_seen('never-issued')
    assert store.retired_method('colony','EnsureWorkAssignments',0,'assign-0')=={'steps':['action-0']}
    goal.reopen_methods()
    assert goal.method_epoch==1 and goal.archived_methods==0 and not goal.method_seen('assign-0')
    assert store.retired_method('colony','EnsureWorkAssignments',0,'assign-0')=={'steps':['action-0']}
    goal.evidence['methods']['assign-0']=['action-1']
    persist(store,restored)
    assert goal.method_seen('assign-0')
    assert store.retired_method('colony','EnsureWorkAssignments',1,'assign-0')=={'steps':['action-1']}
    assert store.retired_method('colony','EnsureWorkAssignments',0,'assign-0')=={'steps':['action-0']}
    store.close()


def test_hunting_metadata_archives_atomically_including_legacy_completed_actions(tmp_path):
    store=Store(tmp_path/'state.sqlite');plan=retired_plan()
    goal=plan.colony_goals['EnsureWorkAssignments']
    metadata={'prey':'Animal_17','anchor':{'x':10,'z':20},'signature':'exact'}
    goal.evidence['hunting_targets']={'action-0':metadata,'action-72':metadata}
    plan.progress['action-0'].recovery_history=[{'tick':20,'failure':{'code':'interrupted'}}]
    before=plan.model_dump()
    store.db.execute("CREATE TRIGGER fail_state BEFORE INSERT ON state BEGIN SELECT RAISE(ABORT,'disk failure'); END")
    with pytest.raises(sqlite3.IntegrityError):persist(store,plan)
    assert plan.model_dump()==before
    assert store.retired_goal_evidence('colony','EnsureWorkAssignments','hunting_targets','action-0') is None
    store.db.execute('DROP TRIGGER fail_state');persist(store,plan)
    assert goal.evidence['hunting_targets']=={'action-72':metadata}
    assert store.retired_action('colony','action-0')['progress']['recovery_history']==before['progress']['action-0']['recovery_history']
    # A pre-migration snapshot can still hold metadata for already archived actions.
    goal.evidence['hunting_targets']['action-1']=metadata
    persist(store,plan)
    assert 'action-1' not in goal.evidence['hunting_targets']
    with sqlite3.connect(tmp_path/'backup.sqlite') as backup:store.db.backup(backup)
    store.close();store=Store(tmp_path/'backup.sqlite')
    for identity in ('action-0','action-1'):
        assert store.retired_goal_evidence('colony','EnsureWorkAssignments','hunting_targets',identity)==metadata
    with pytest.raises(ValueError,match='goal evidence changed'):
        store.archive_and_set('colony','plan',{}, {},evidence=[{'goal':'EnsureWorkAssignments',
            'field':'hunting_targets','identity':'action-0','record':{'prey':'replacement'}}])
    assert store.get('plan')==plan.model_dump()
    store.close()


def test_method_snapshot_failure_rolls_back_and_missing_archive_refuses(tmp_path):
    store=Store(tmp_path/'state.sqlite');plan=retired_plan()
    plan.colony_goals['EnsureWorkAssignments'].evidence['methods']={'assign':['action-0']}
    before=plan.model_dump()
    store.db.execute("CREATE TRIGGER fail_state BEFORE INSERT ON state BEGIN SELECT RAISE(ABORT,'disk failure'); END")
    with pytest.raises(sqlite3.IntegrityError):persist(store,plan)
    assert plan.model_dump()==before and store.retired_method('colony','EnsureWorkAssignments',0,'assign') is None
    store.db.execute('DROP TRIGGER fail_state');persist(store,plan)
    restored=ColonyPlan.model_validate(store.get('plan'))
    store.db.execute('DELETE FROM retired_methods');store.db.commit()
    with pytest.raises(ValueError,match='method archive is missing'):bind_archive(restored,store,'colony')
    store.close()


def test_mixed_pending_method_stays_live_and_old_action_methods_can_compact_later(tmp_path):
    store=Store(tmp_path/'state.sqlite');plan=retired_plan()
    plan.progress['action-72'].state='waiting'
    goal=plan.colony_goals['EnsureWorkAssignments']
    goal.evidence['methods']={'mixed':['action-0','action-72']}
    persist(store,plan)
    assert goal.archived_methods==0 and goal.evidence['methods']['mixed']==['action-0','action-72']
    goal.evidence['methods']['late-observed']=['action-0']
    persist(store,plan)
    assert goal.archived_methods==1 and goal.method_seen('late-observed')
    assert store.retired_method('colony','EnsureWorkAssignments',0,'late-observed')=={'steps':['action-0']}
    before=store.get('plan')
    with pytest.raises(ValueError,match='method record changed'):
        store.archive_and_set('colony','plan',{}, {},[{'goal':'EnsureWorkAssignments','epoch':0,
            'method':'late-observed','record':{'steps':['different']}}])
    assert store.get('plan')==before
    store.close()


@pytest.mark.asyncio
async def test_skill_compiler_uses_archived_method_membership(tmp_path):
    from types import SimpleNamespace
    from rimbot.colony_skills import ColonySkills
    store=Store(tmp_path/'state.sqlite');plan=retired_plan()
    goal=ColonyGoal(priority_class=0,evidence={'methods':{'names-7':['action-0']}})
    plan.colony_goals['ConfirmColonyNames']=goal
    persist(store,plan)
    assert goal.evidence['methods']=={} and goal.archived_methods==1
    skills=ColonySkills(SimpleNamespace(current_plan=plan))
    facts={'colonyNaming':{'windowId':7}}
    assert await skills.compile('ConfirmColonyNames',facts,[]) is None
    goal.reopen_methods()
    method,actions=await skills.compile('ConfirmColonyNames',facts,[])
    assert method=='names-7' and len(actions)==1
    store.close()


def test_player_capacity_archives_completed_receipts_but_pins_maintained_construction(tmp_path):
    from rimbot.colony_plan import PlanSpec
    store=Store(tmp_path/'player.sqlite')
    rows=[action('player-'+str(i)).model_copy(update={'source':'PLAYER'}) for i in range(80)]
    plan=ColonyPlan(spec=PlanSpec(steps=rows))
    plan.progress={s.id:StepProgress(state='complete',issued={'0':{'confirmed':True,'receipt':{'id':s.id}}}) for s in rows}
    plan.progress[rows[-1].id].state='waiting'
    plan.colony_goals['intent-room']=ColonyGoal(priority_class=2,source='PLAYER',steps=[rows[0].id])
    plan.control['player_intents']={'original':{'step':rows[1].id,'request':{'kind':'SetWorkPriority'}}}
    original=plan.progress[rows[1].id].model_dump()
    bind_archive(plan,store,'colony')
    decision=CommitSteps(expected_revision=0,reason='next player order',steps=[action('next')]).decision(plan)
    plan.commit(decision,actor='strategist',tick=1)
    persist(store,plan)
    assert {s.id for s in plan.spec.steps}=={rows[0].id,rows[-1].id,'next'}
    assert store.retired_action('colony',rows[1].id)['progress']==original
    assert plan.control['player_intents']['original']['step']==rows[1].id
    assert plan.colony_goals['intent-room'].steps==[rows[0].id]
    store.close()


def test_full_unfinished_player_plan_refuses_without_dropping_work():
    from rimbot.colony_plan import PlanSpec
    rows=[action('pending-'+str(i)).model_copy(update={'source':'PLAYER'}) for i in range(80)]
    plan=ColonyPlan(spec=PlanSpec(steps=rows),progress={s.id:StepProgress() for s in rows})
    before=plan.model_dump()
    with pytest.raises(ValueError):
        CommitSteps(expected_revision=0,reason='new',steps=[action('next')]).decision(plan)
    assert plan.model_dump()==before


@pytest.mark.parametrize('setting', ['work', 'draft', 'undraft', 'goto'])
def test_completed_player_setting_can_be_renewed_without_erasing_previous_receipt(tmp_path, setting):
    from rimbot.colony_plan import PlanSpec
    old=action('old').model_copy(update={'source':'PLAYER'})
    if setting != 'work':
        old.action = old.action.model_copy(update={'tool': 'home/order', 'arguments': {'action': setting, 'pawn': 'Thing_Human1', 'dryRun': False}})
        if setting == 'goto':
            old.action = old.action.model_copy(update={'completion': 'pawn_at_position',
                'arguments': dict(old.action.arguments, x=10, z=20)})
    new=old.model_copy(update={'id':'new'})
    plan=ColonyPlan(spec=PlanSpec(steps=[old]),progress={'old':StepProgress(state='complete',issued={'0':{'confirmed':True}})})
    store=Store(tmp_path/'renew.sqlite')
    plan.commit(CommitSteps(expected_revision=0,reason='Explicitly restore prior setting',steps=[new]).decision(plan),actor='strategist',tick=2)
    persist(store,plan)
    assert [s.id for s in plan.spec.steps]==['new'] and plan.progress['new'].state=='pending'
    assert store.retired_action('colony','old')['progress']['issued']=={'0':{'confirmed':True}}
    store.close()


@pytest.mark.parametrize('setting', ['work', 'draft', 'undraft', 'goto'])
def test_pending_player_setting_cannot_be_duplicated(setting):
    from rimbot.colony_plan import PlanSpec
    old=action('old').model_copy(update={'source':'PLAYER'})
    if setting != 'work':
        old.action = old.action.model_copy(update={'tool': 'home/order', 'arguments': {'action': setting, 'pawn': 'Thing_Human1', 'dryRun': False}})
        if setting == 'goto':
            old.action = old.action.model_copy(update={'completion': 'pawn_at_position',
                'arguments': dict(old.action.arguments, x=10, z=20)})
    plan=ColonyPlan(spec=PlanSpec(steps=[old]),progress={'old':StepProgress(state='waiting')})
    with pytest.raises(ValueError,match='existing step ID'):
        plan.commit(CommitSteps(expected_revision=0,reason='duplicate',steps=[old.model_copy(update={'id':'new'})]).decision(plan),actor='strategist',tick=2)
