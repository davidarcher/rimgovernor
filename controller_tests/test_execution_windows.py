from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import PlanSpec, StepProgress
from rimbot.store import Store


def runtime(tmp_path, state='waiting', confirmed=True):
    store = Store(tmp_path/'windows.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.mode = 'automate'
    rt.sync_identity = AsyncMock(return_value=False)
    rt.resume_after_review = True
    rt.supervisor = SimpleNamespace(change=AsyncMock(), hold=None)
    rt.game = SimpleNamespace(query=AsyncMock(return_value={'time': {'ticksGame': 100}}))
    rt.hands = SimpleNamespace(advance=AsyncMock())
    rt.current_plan.spec = PlanSpec(steps=[dict(id='build', title='Build',
        completion_criteria='Built', action={'kind':'place_buildings',
        'placements':[{'def_name':'Wall','x':1,'z':1}]})])
    rt.current_plan.progress['build'] = StepProgress(state=state, issued={'0':{'confirmed':confirmed}})
    return rt, store


@pytest.mark.asyncio
@pytest.mark.parametrize('state,confirmed', [('pending',False),('waiting',False),('blocked',True),('complete',True)])
async def test_no_automatic_time_for_unissued_blocked_or_finished_work(tmp_path, state, confirmed):
    rt, store = runtime(tmp_path, state, confirmed)
    try:
        await rt.advance_execution()
        rt.supervisor.change.assert_not_awaited()
    finally:
        store.close()


@pytest.mark.asyncio
async def test_confirmed_work_gets_bounded_window_then_pauses_for_review(tmp_path):
    rt, store = runtime(tmp_path)
    try:
        await rt.advance_execution()
        rt.supervisor.change.assert_awaited_once_with('Normal')
        assert rt.execution_window_end == 700
        rt.game.query.return_value = {'time': {'ticksGame': 705}}
        await rt.advance_execution()
        assert rt.supervisor.change.await_args.args == ('Paused',)
        assert rt.wake.is_set() and rt.execution_window_end is None
    finally:
        store.close()


@pytest.mark.asyncio
async def test_completed_work_ends_window_early(tmp_path):
    rt, store = runtime(tmp_path)
    try:
        await rt.advance_execution()
        rt.current_plan.progress['build'].state = 'complete'
        await rt.advance_execution()
        assert rt.supervisor.change.await_args.args == ('Paused',)
        assert rt.wake.is_set()
    finally:
        store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('condition', ['deliberating','wake','hold','manual'])
async def test_execution_does_not_resume_during_review_or_hold(tmp_path, condition):
    rt, store = runtime(tmp_path)
    if condition == 'deliberating': rt.deliberating = True
    if condition == 'wake': rt.wake.set()
    if condition == 'hold': rt.supervisor.hold = 'external_pause'
    if condition == 'manual': rt.mode = 'manual'
    try:
        await rt.advance_execution()
        rt.supervisor.change.assert_not_awaited()
    finally:
        store.close()


@pytest.mark.asyncio
async def test_review_pauses_before_observation_and_never_unpauses_before_orders(tmp_path, monkeypatch):
    rt, store = runtime(tmp_path)
    rt.sync_identity = AsyncMock(return_value=False)
    rt.update_strategy_state = lambda: None
    rt.reconcile_plan = lambda: None
    rt.projects.reconcile = AsyncMock()
    async def observe(_):
        rt.supervisor.change.assert_awaited_once_with('Paused')
        assert rt.deliberating
        return None
    monkeypatch.setattr('rimbot.bridge_runtime.observe', observe)
    rt.planner = SimpleNamespace(play_bridge=AsyncMock())
    try:
        await rt.review()
        rt.planner.play_bridge.assert_awaited_once()
        rt.supervisor.change.assert_awaited_once_with('Paused')
        assert not rt.deliberating and rt.resume_after_review
    finally:
        store.close()


@pytest.mark.asyncio
async def test_clock_failure_stops_execution_instead_of_retrying_unattended(tmp_path):
    rt, store = runtime(tmp_path)
    rt.game.query.side_effect = RuntimeError('clock unavailable')
    rt.release_drafts = AsyncMock()
    try:
        await rt.advance_execution()
        assert rt.mode == 'manual'
        rt.supervisor.change.assert_awaited_once_with('Paused')
    finally:
        store.close()


@pytest.mark.asyncio
async def test_planned_clock_wait_is_bounded_even_without_construction(tmp_path):
    rt, store = runtime(tmp_path, 'complete')
    rt.sync_identity = AsyncMock(return_value=False)
    rt.supervisor.change.return_value = {'active': True}
    try:
        await rt.control_clock('Fast', expected_plan_revision=rt.current_plan.revision)
        assert rt.execution_window_end == 700 and rt.execution_wait_explicit
        await rt.advance_execution()
        assert rt.supervisor.change.await_count == 1
        rt.game.query.return_value = {'time': {'ticksGame': 705}}
        await rt.advance_execution()
        assert rt.supervisor.change.await_args.args == ('Paused',)
        assert rt.wake.is_set()
    finally:
        store.close()


@pytest.mark.asyncio
async def test_direction_during_clock_read_prevents_automatic_resume(tmp_path):
    rt, store = runtime(tmp_path)
    async def status(*args, **kwargs):
        await rt.steer('Stop and reconsider')
        return {'time': {'ticksGame': 100}}
    rt.game.query.side_effect = status
    try:
        await rt.advance_execution()
        rt.supervisor.change.assert_not_awaited()
    finally:
        store.close()
