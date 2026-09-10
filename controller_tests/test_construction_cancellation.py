from copy import deepcopy
from unittest.mock import AsyncMock
import pytest
from rimgovernor.colony_plan import PlanSpec, StepProgress, ColonyGoal
from rimgovernor.player_commands import apply_command
from test_strategic_architecture import runtime, batch


async def fixture(tmp_path):
    rt = runtime(tmp_path); await rt.sync_identity(); rt.batch = batch(); rt.mode = 'manual'
    plan = rt.current_plan
    plan.spec = PlanSpec(steps=[dict(id='player-room',title='Room',goal_id='intent-room',
        completion_criteria='Built',action=dict(kind='place_buildings',placements=[
            dict(def_name='Wall',x=20,z=20,materials=['WoodLog']),
            dict(def_name='Wall',x=21,z=20,materials=['WoodLog'])]))])
    plan.progress['player-room'] = StepProgress(state='waiting',issued={
        '0':{'confirmed':True,'stuff':'WoodLog'},'1':{'confirmed':True,'stuff':'WoodLog'}})
    plan.colony_goals['intent-room'] = ColonyGoal(source='PLAYER',priority_class=2,
        steps=['player-room'],target={'satisfies':'EnsureInitialShelter'})
    plan.control['player_intents'] = {'room':{'step':'player-room'}}
    rows = [dict(thingId='Blueprint_Wall1',buildDefName='Wall',stuff='WoodLog',isBlueprint=True,
        position={'x':20,'z':20}),dict(thingId='Wall2',defName='Wall',stuff='WoodLog',
        position={'x':21,'z':20},isBlueprint=False,isFrame=False)]
    async def query(name, **args):
        if name == 'home/list_buildings': return {'buildings':deepcopy(rows)}
        return {'colonyId':'test','mapId':1,'loadToken':'load'}
    rt.game.query = AsyncMock(side_effect=query)
    rt.game.invoke = AsyncMock(return_value={'success':True,'applied':False})
    rt.inspect_native = AsyncMock(return_value={'success':True,'applied':False})
    async def write(name, args, **kwargs):
        assert name == 'home/cancel_construction' and args['thing'] == 'Blueprint_Wall1'
        rows[:] = [b for b in rows if b['thingId'] != args['thing']]
        return {'receipt':{'success':True,'removed':True,'target':{'thingId':args['thing']}}}
    rt.native = AsyncMock(side_effect=write)
    return rt, rows


async def cancel(rt):
    return await apply_command(rt, {'kind':'CancelConstruction','intent_id':'room'},
        token=rt.context_token,revision=rt.chat_revision)


@pytest.mark.asyncio
async def test_semantic_cancellation_suppresses_source_then_hands_removes_only_pending(tmp_path):
    rt, rows = await fixture(tmp_path)
    result = await cancel(rt)
    assert result['targets'] == 1 and rt.native.await_count == 0
    assert rt.current_plan.progress['player-room'].state == 'cancelled'
    assert rt.current_plan.colony_goals['intent-room'].cancelled
    assert rt.current_plan.control['suppressed_goals']['EnsureInitialShelter'] == 'intent-room'
    await rt.execute_manual_requests()
    assert rt.current_plan.progress[result['step']].state == 'complete'
    assert [b['thingId'] for b in rows] == ['Wall2']
    assert rt.native.await_count == 1 and rt.mode == 'manual'
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('change', ['material','definition','uncertain','truncated','native_refusal'])
async def test_failed_validation_preserves_original_plan_and_orders(tmp_path,change):
    rt, rows = await fixture(tmp_path)
    if change == 'material': rows[0]['stuff'] = 'Steel'
    if change == 'definition': rows[0]['buildDefName'] = 'Door'
    if change == 'uncertain': rt.current_plan.progress['player-room'].issued['0']['confirmed'] = False
    if change == 'native_refusal': rt.game.invoke.return_value = {'success':False,'applied':False}
    if change == 'truncated':
        query = rt.game.query
        async def truncated(name,**args):
            result = await query(name,**args)
            if name == 'home/list_buildings': result['skipped'] = {'byMaxDetailed':1}
            return result
        rt.game.query = truncated
    before = deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError): await cancel(rt)
    assert rt.current_plan.model_dump() == before
    assert len(rows) == 2
    rt.native.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
async def test_unknown_cancellation_observes_absence_without_removing_replacement(tmp_path):
    rt, rows = await fixture(tmp_path)
    result = await cancel(rt)
    progress = rt.current_plan.progress[result['step']]
    progress.issued['0'] = {'confirmed':False,'thing_id':'Blueprint_Wall1'}
    rows[0]['thingId'] = 'Blueprint_WallReplacement'
    await rt.execute_manual_requests()
    assert progress.state == 'complete' and progress.issued['0']['observed_absent']
    assert rows[0]['thingId'] == 'Blueprint_WallReplacement'
    rt.native.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
async def test_unknown_cancellation_still_present_is_not_replayed(tmp_path):
    rt, rows = await fixture(tmp_path)
    result = await cancel(rt)
    progress = rt.current_plan.progress[result['step']]
    progress.issued['0'] = {'confirmed':False}
    await rt.execute_manual_requests()
    assert progress.state == 'blocked' and progress.failure.code == 'uncertain_write'
    rt.native.assert_not_awaited()
    assert len(rows) == 2
    rt.store.close()


@pytest.mark.asyncio
async def test_loaded_cancellation_cannot_retarget_after_load_change(tmp_path):
    rt, rows = await fixture(tmp_path)
    result = await cancel(rt)
    action = rt.current_plan.spec.steps[-1].action
    action.loadToken = 'prior-load'
    await rt.execute_manual_requests()
    assert rt.current_plan.progress[result['step']].failure.code == 'cancellation_context_changed'
    rt.native.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('instruction', [
    'Stop the room goal. Keep the blueprints already in the game.',
    'Cancel future work; leave existing construction orders in place.',
    'Do not remove its frames.',
    "Don't delete any pending blueprints."])
async def test_preservation_instruction_overrules_model_removal(tmp_path,instruction):
    rt, rows = await fixture(tmp_path)
    rt.chat.append({'kind':'human','revision':rt.chat_revision,'text':instruction})
    before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError,match='preserving existing construction'):
        await cancel(rt)
    assert rt.current_plan.model_dump()==before and len(rows)==2
    rt.native.assert_not_awaited()
    rt.store.close()


@pytest.mark.parametrize('instruction', [
    'Remove the blueprints but keep completed buildings.',
    'Cancel its frames; leave the finished walls alone.',
    'Do not keep constructing this bedroom. Remove its pending blueprints.'])
def test_completed_building_preservation_does_not_refuse_pending_removal(instruction):
    from rimgovernor.construction_cancellation import preserves_pending_orders
    assert not preserves_pending_orders(instruction)


@pytest.mark.asyncio
async def test_raw_plan_cannot_bypass_player_preservation_instruction(tmp_path):
    from rimgovernor.colony_plan import CommitSteps
    rt, rows = await fixture(tmp_path)
    result=await cancel(rt)
    action=next(s for s in rt.current_plan.spec.steps if s.id==result['step']).model_copy(deep=True)
    action.id='bypass';rt.current_plan.spec.steps.pop()
    rt.current_plan.progress.pop(result['step'])
    rt.chat.append({'kind':'human','revision':rt.chat_revision,'text':'Keep the blueprints.'})
    before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError,match='preserving existing construction'):
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,reason='Remove',steps=[action]).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
    assert rt.current_plan.model_dump()==before
    rt.store.close()


@pytest.mark.asyncio
async def test_pending_removal_rechecks_preservation_before_execution(tmp_path):
    rt, rows=await fixture(tmp_path)
    result=await cancel(rt)
    rt.chat.append({'kind':'human','revision':rt.chat_revision,'text':'Leave the blueprints in place.'})
    await rt.execute_manual_requests()
    progress=rt.current_plan.progress[result['step']]
    assert progress.state=='blocked' and 'preserving existing construction' in progress.failure.detail
    rt.native.assert_not_awaited()
    assert len(rows)==2
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('instruction', [
    'Cancel the bedroom.',
    'Stop work on that room.',
    'Do not cancel construction of the room.',
    "Don't remove the current construction.",
    "Remove the bedroom construction orders. Keep workshop's blueprints in place.",
    'Remove the room orders but keep its pending blueprints.'])
async def test_ambiguous_or_conflicting_human_request_cannot_remove_native_orders(tmp_path,instruction):
    rt,rows=await fixture(tmp_path)
    rt.chat.append({'kind':'human','revision':rt.chat_revision,'text':instruction})
    before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError):await cancel(rt)
    assert rt.current_plan.model_dump()==before and len(rows)==2
    rt.native.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
async def test_move_request_cannot_be_reduced_to_standalone_removal(tmp_path):
    from rimgovernor.colony_plan import CommitSteps
    rt,rows=await fixture(tmp_path)
    result=await cancel(rt)
    removal=next(s for s in rt.current_plan.spec.steps if s.id==result['step']).model_copy(deep=True)
    removal.id='bypass';rt.current_plan.spec.steps.pop();rt.current_plan.progress.pop(result['step'])
    rt.chat.append({'kind':'human','revision':rt.chat_revision,
        'text':'Move the pending walls to x 30, z 30. Remove their old blueprints as part of that move.'})
    before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError,match='RelocateConstruction'):await cancel(rt)
    with pytest.raises(ValueError,match='RelocateConstruction'):
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,reason='Move',steps=[removal]).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
    assert rt.current_plan.model_dump()==before and len(rows)==2
    rt.native.assert_not_awaited()
    rt.store.close()
