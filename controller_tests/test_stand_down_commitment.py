from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimgovernor.colony_plan import ColonyPlan, Decision, PlanSpec, StepProgress
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.config import ModelRole
from rimgovernor.native_contracts import validate_stand_down_steps
from rimgovernor.store import Store


def release(identity='release', after=(), pawn='Thing_A'):
    return {'id': identity, 'title': 'Release', 'completion_criteria': 'Undrafted',
            'action': {'kind': 'stand_down', 'pawn_ids': [pawn]},
            'after': [{'step': dependency} for dependency in after]}


def draft(pawn='Thing_A', action='draft'):
    return {'id': 'draft', 'title': 'Order', 'completion_criteria': 'Native order',
            'action': {'kind': 'native_operation', 'tool': 'home/order',
                       'arguments': {'action': action, 'pawn': pawn, 'dryRun': False}}}


def validate(steps, owners=None, current=None, drafted=False):
    validate_stand_down_steps(PlanSpec(steps=steps), current or ColonyPlan(), owners or {}, 'load',
        [SimpleNamespace(thing_id='Thing_A', drafted=drafted)])


@pytest.mark.parametrize('owners', [{}, {'Thing_A': 'old-load'}, {'Other': 'load'}])
def test_new_stand_down_rejects_missing_current_load_ownership(owners):
    with pytest.raises(ValueError, match='no current-load AI-owned target'):
        validate([release()], owners)


def test_owned_target_and_unchanged_completed_cleanup_remain_valid():
    validate([release()], {'Thing_A': 'load'})
    current = ColonyPlan(spec=PlanSpec(steps=[release()]), progress={
        'release': StepProgress(state='complete', issued={'0': {'confirmed': True}})})
    renamed = release()
    renamed['title'] = 'Updated presentation'
    validate([renamed], current=current)
    with pytest.raises(ValueError, match='no current-load'):
        validate([release('new')], current=current)


@pytest.mark.parametrize('action', ['draft', 'goto', 'attack', 'tend'])
def test_explicit_pending_draft_prerequisite_is_valid(action):
    validate([draft(action=action), release(after=['draft'])])
    with pytest.raises(ValueError, match='no current-load'):
        validate([draft(action=action), release()])


def test_transitive_prerequisite_is_recognized():
    intermediate = {'id': 'wait', 'title': 'Pause', 'completion_criteria': 'Paused',
        'action': {'kind': 'clock', 'speed': 'Paused'}, 'after': [{'step': 'draft'}]}
    validate([draft(), intermediate, release(after=['wait'])])


def test_prior_release_does_not_justify_another_new_cleanup():
    with pytest.raises(ValueError, match='no current-load'):
        validate([draft(), release('first', after=['draft']), release('second', after=['first'])])


@pytest.mark.parametrize('drafted', [True, None])
def test_future_order_cannot_claim_player_or_unknown_draft(drafted):
    with pytest.raises(ValueError, match='no current-load'):
        validate([draft(), release(after=['draft'])], drafted=drafted)


@pytest.mark.parametrize('prerequisite', [draft('Other'), draft(action='undraft'), draft(action='resolve')])
def test_unrelated_prerequisite_does_not_justify_cleanup(prerequisite):
    with pytest.raises(ValueError, match='no current-load'):
        validate([prerequisite, release(after=['draft'])])


@pytest.mark.parametrize('progress', [StepProgress(state='complete'), StepProgress(state='blocked'),
    StepProgress(state='executing', issued={'0': {'confirmed': True}})])
def test_already_issued_or_blocked_draft_does_not_promise_future_ownership(progress):
    current = ColonyPlan(spec=PlanSpec(steps=[draft()]), progress={'draft': progress})
    with pytest.raises(ValueError, match='no current-load'):
        validate([draft(), release(after=['draft'])], current=current)


@pytest.mark.asyncio
async def test_runtime_rejects_noop_commit_without_changing_plan(tmp_path):
    store = Store(tmp_path/'test.sqlite')
    rt = BridgeRuntime(store, tmp_path)
    rt.context_token = 'load'
    rt.sync_identity = AsyncMock(return_value=False)
    rt.batch = SimpleNamespace(summary=SimpleNamespace(pawns=[], end_tick=100))
    decision = Decision(expected_revision=0, disposition='revise', assessment='Cleanup',
        rationale='Cleanup', reply='Cleanup', plan=PlanSpec(steps=[release()]))
    before = rt.current_plan.model_dump()
    try:
        with pytest.raises(ValueError, match='stand_down has no current-load'):
            await rt.commit_strategy(decision, actor=ModelRole.STRATEGIST,
                                     expected_token='load', expected_revision=0)
        assert rt.current_plan.model_dump() == before
    finally:
        await rt.router.close()
        store.close()
