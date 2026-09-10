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
    rt.identity = {'colonyId':'test','mapId':1,'loadToken':'load'}
    rt.sync_identity = AsyncMock(return_value=False)
    rt.resume_after_review = True
    async def change(speed, **kwargs):
        return {'active': speed != 'Paused', 'startTick': 100,
                'tickDeadline': 100 + kwargs.get('max_ticks', 0)}
    rt.supervisor = SimpleNamespace(change=AsyncMock(side_effect=change), hold=None)
    rt.game = SimpleNamespace(query=AsyncMock(return_value={'time': {'ticksGame': 100}}),
        invoke=AsyncMock(return_value={'success':True,'floors':{},'commitments':{},'stopped':[]}))
    rt.hands = SimpleNamespace(advance=AsyncMock())
    rt.current_plan.spec = PlanSpec(steps=[dict(id='build', title='Build',
        completion_criteria='Built', action={'kind':'place_buildings',
        'placements':[{'def_name':'Wall','x':1,'z':1}]})])
    rt.current_plan.progress['build'] = StepProgress(state=state, issued={'0':{'confirmed':confirmed}})
    return rt, store


@pytest.mark.asyncio
async def test_autosave_refusal_reobserves_without_replaying_or_releasing_hold(tmp_path):
    rt,store=runtime(tmp_path)
    rt._advance_execution=AsyncMock(side_effect=ValueError('A long event (autosave, map generation) is running or queued'))
    rt.supervisor.hold='external_pause'
    try:
        await rt.advance_execution()
        assert rt.mode=='automate' and rt.wake.is_set()
        assert rt.supervisor.hold=='external_pause'
        rt.supervisor.change.assert_not_awaited()
        assert rt._advance_execution.await_count==1
        rt._long_event_deadline=0
        assert not rt.defer_long_event(ValueError('A long event (autosave, map generation) is running or queued'))
    finally:
        store.close()


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
        rt.supervisor.change.assert_awaited_once_with('Normal',mode='colony',ignored_hostiles='',max_ticks=600)
        assert rt.execution_window_end == 700
        rt.game.query.return_value = {'time': {'ticksGame': 705}}
        await rt.advance_execution()
        assert rt.supervisor.change.await_args.args == ('Paused',)
        assert rt.wake.is_set() and rt.execution_window_end is None
    finally:
        store.close()


@pytest.mark.asyncio
async def test_native_fire_watch_uses_normal_speed_and_sixty_tick_review(tmp_path):
    from rimbot.colony_plan import ColonyGoal
    rt, store = runtime(tmp_path, 'complete')
    from dataclasses import replace
    rt.controller.policy = replace(rt.controller.policy, execution_speed='Superfast')
    rt.current_plan.control['simulation_needed'] = True
    rt.current_plan.colony_goals['MaintainFireSafety'] = ColonyGoal(
        priority_class=1, status='active', evidence={'waiting_for_native_fire': True})
    try:
        await rt.advance_execution()
        rt.supervisor.change.assert_awaited_once_with('Normal', mode='colony', ignored_hostiles='', max_ticks=60)
        assert rt.execution_window_end == 160
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
    rt.controller.cycle = AsyncMock()
    try:
        await rt.review()
        rt.planner.play_bridge.assert_not_awaited()
        rt.controller.cycle.assert_awaited_once()
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


@pytest.mark.asyncio
async def test_blocked_emergency_prevents_time_even_with_waiting_construction(tmp_path):
    rt,store=runtime(tmp_path)
    try:
        rt.current_plan.control['execution_hold']='Threat exceeds available method'
        await rt.advance_execution()
        rt.supervisor.change.assert_not_awaited()
    finally: store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('state,profile',[('complete','combat'),('blocked','colony'),('pending','colony')])
async def test_combat_clock_acknowledges_only_a_dispatched_active_defense(tmp_path,state,profile):
    from rimbot.colony_plan import ColonyGoal
    rt,store=runtime(tmp_path)
    try:
        rt.current_plan.control['combat']={'target':'Thing_Hare1','steps':['attack']}
        rt.current_plan.progress['attack']=StepProgress(state=state)
        rt.current_plan.colony_goals['ActiveCombat']=ColonyGoal(priority_class=0)
        rt.game.query.return_value = {'time': {'ticksGame': 100}, 'pawns': [
            {'thingId': 'colonist', 'dead': False, 'downed': False, 'health': {'summaryPct': 1}}]}
        await rt.advance_execution()
        rt.supervisor.change.assert_awaited_once_with('Normal',mode=profile,
            ignored_hostiles='Thing_Hare1' if profile=='combat' else '',max_ticks=600)
    finally: store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('health', [0.4, 0.5, None, float('nan')])
async def test_combat_rearm_holds_existing_injury_without_starting_clock(tmp_path, health):
    from rimbot.colony_plan import ColonyGoal
    rt, store = runtime(tmp_path)
    try:
        rt.current_plan.control['combat'] = {'target': 'Thing_Hare1', 'steps': ['attack']}
        rt.current_plan.progress['attack'] = StepProgress(state='complete')
        rt.current_plan.colony_goals['ActiveCombat'] = ColonyGoal(priority_class=0)
        rt.game.query.return_value = {'time': {'ticksGame': 100}, 'pawns': [
            {'thingId': 'colonist', 'dead': False, 'downed': False, 'health': {'summaryPct': health}}]}
        await rt.advance_execution()
        assert rt.current_plan.control['execution_hold']
        assert rt.current_plan.colony_goals['ActiveCombat'].status == 'blocked'
        rt.resume_after_review = True
        await rt.advance_execution()
        rt.supervisor.change.assert_not_awaited()
    finally:
        store.close()


@pytest.mark.asyncio
async def test_native_production_wait_has_bounded_window_and_retains_normal_guard(tmp_path):
    rt,store=runtime(tmp_path,state='complete')
    try:
        rt.current_plan.control['simulation_needed']=True
        await rt.advance_execution()
        assert rt.execution_window_end==3100
        rt.supervisor.change.assert_awaited_once_with('Normal',mode='colony',ignored_hostiles='',max_ticks=3000)
    finally: store.close()
