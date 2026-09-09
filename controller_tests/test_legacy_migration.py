"""Legacy migration refuses inconsistent player state before taking game ownership."""
from copy import deepcopy
import importlib.util
from pathlib import Path
import sqlite3
import pytest
from rimbot.colony_plan import ColonyPlan
from rimbot.store import Store

spec=importlib.util.spec_from_file_location('legacy_migration',Path(__file__).parents[1]/'scripts/migrate_legacy_session.py')
migration=importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)


def snapshot():
    plan=ColonyPlan(control={'interpreted_player_revision':3})
    chat=[{'kind':'human','revision':3,'text':'Keep this player request'}]
    saved={'current_plan':plan.model_dump(),'chat':chat,'handled_revision':3}
    state={'currentPlan':{'revision':0,'colonyGoals':{},'controller':deepcopy(plan.control)},'feed':deepcopy(chat)}
    return saved,state


@pytest.mark.parametrize('change',['revision','policy','goals','chat','drafts','pending','uninterpreted'])
def test_inconsistent_or_pending_player_state_blocks_migration(change):
    saved,state=snapshot()
    if change=='revision': state['currentPlan']['revision']=1
    elif change=='policy': state['currentPlan']['controller']['food_target_days']=20
    elif change=='goals': state['currentPlan']['colonyGoals']['food']={}
    elif change=='chat': state['feed'][0]['text']='A newer request'
    elif change=='drafts': saved['draft_owners']={'pawn':'load'}
    elif change=='pending': saved['handled_revision']=2
    elif change=='uninterpreted':
        saved['current_plan']['control']['interpreted_player_revision']=2
        state['currentPlan']['controller']['interpreted_player_revision']=2
    with pytest.raises(ValueError): migration.validate_saved_state(saved,state)


def test_database_copy_is_independent_and_preserves_player_state(tmp_path):
    source=Store(tmp_path/'old.sqlite');saved,state=snapshot()
    source.set('bridge:colony:0',saved)
    migration.copy_database(tmp_path/'old.sqlite',tmp_path/'copy.sqlite')
    copied=Store(tmp_path/'copy.sqlite')
    migration.validate_saved_state(copied.get('bridge:colony:0'),state)
    copied.set('bridge:colony:0',{})
    assert source.get('bridge:colony:0')==saved
    copied.close();source.close()


def test_missing_database_is_not_created(tmp_path):
    source=tmp_path/'missing.sqlite'
    with pytest.raises(sqlite3.OperationalError): migration.copy_database(source,tmp_path/'copy.sqlite')
    assert not source.exists()
