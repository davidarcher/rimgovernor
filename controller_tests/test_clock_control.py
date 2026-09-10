from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.clock_control import PlayClock
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store


@pytest.mark.asyncio
async def test_acceleration_is_explicit_bounded_and_capability_checked():
    bridge = NativeClock()
    clock = PlayClock(bridge, test_acceleration=True)
    with pytest.raises(ValueError, match='requires a native tick budget'):
        await clock.change('Superfast')
    assert not bridge.calls
    with pytest.raises(ValueError, match='lacks supervised test acceleration'):
        await clock.change('Superfast', max_ticks=600)
    assert not any(args.get('op') == 'start' for _, args in bridge.calls)
    bridge.state['nativeTestAcceleration'] = True
    await clock.change('Superfast', max_ticks=600)
    start = next(args for _, args in bridge.calls if args.get('op') == 'start')
    assert start['speed'] == 'Ultrafast' and start['testAcceleration'] is True
    assert start['maxTicks'] == 600 and start['injuryStopCooldownMs'] == 0
    await clock.change('Paused')
    assert not bridge.state['active']


@pytest.mark.asyncio
async def test_production_clock_does_not_request_boost():
    bridge = NativeClock()
    await PlayClock(bridge).change('Superfast', max_ticks=600)
    start = next(args for _, args in bridge.calls if args.get('op') == 'start')
    assert start['speed'] == 'Superfast' and 'testAcceleration' not in start


@pytest.mark.asyncio
async def test_acceleration_preserves_tracked_surgical_recovery_profile():
    bridge = NativeClock()
    bridge.state['nativeTestAcceleration'] = True
    await PlayClock(bridge, test_acceleration=True).change('Superfast', max_ticks=600,
                                                        surgical_recovery='Thing_Human1')
    start = next(args for _, args in bridge.calls if args.get('op') == 'start')
    assert start['surgicalRecoveryIds'] == 'Thing_Human1' and start['testAcceleration']


@pytest.mark.asyncio
@pytest.mark.parametrize('operation', ['status', 'events'])
async def test_clock_reads_recover_published_launch_claim(operation):
    from rimbot.bridge import BridgeClient, BridgeError
    bridge = BridgeClient(None, 'rimbot-trial')
    failure = BridgeError('games_call_tool', SimpleNamespace(structuredContent={'error':
        "Failed to claim runtime ownership: a launch claim for 'rimbot-trial' was published while preparing this operation; re-check games_status and retry"}, content=[]))
    bridge.core = AsyncMock(side_effect=[failure, SimpleNamespace(structuredContent={}),
                                        SimpleNamespace(structuredContent={'success': True})])
    assert await PlayClock(bridge).call(op=operation) == {'success': True}
    assert bridge.core.call_count == 3
    assert bridge.core.call_args_list[1].args[0] == 'games_status'


@pytest.mark.asyncio
@pytest.mark.parametrize('operation', ['start', 'pause', 'heartbeat'])
async def test_clock_mutations_never_retry_launch_claim_errors(operation):
    from rimbot.bridge import BridgeClient, BridgeError
    bridge = BridgeClient(None, 'rimbot-trial')
    failure = BridgeError('games_call_tool', SimpleNamespace(structuredContent={'error':
        "Failed to claim runtime ownership: a launch claim for 'rimbot-trial' was published while preparing this operation; re-check games_status and retry"}, content=[]))
    bridge.core = AsyncMock(side_effect=failure)
    with pytest.raises(BridgeError): await PlayClock(bridge).call(op=operation)
    assert bridge.core.call_count == 1


class NativeClock:
    def __init__(self):
        self.state = {'success': True, 'active': False, 'epoch': 0, 'newestCursor': 0,
                      'nativeTickBoundary': True}
        self.calls = []
        self.events = []
        self.race = False

    async def call(self, name, **args):
        self.calls.append((name, args))
        op = args.get('op')
        if name == 'home/status':
            return SimpleNamespace(structuredContent={'time': {'paused': True}})
        if op == 'start':
            self.state = dict(success=True, active=True, owner=args['owner'], epoch=self.state['epoch']+1,
                              newestCursor=len(self.events), nativeTickBoundary=True, startTick=100,
                              tickDeadline=100+args['maxTicks'] if args.get('maxTicks') else None)
        elif op == 'pause':
            self.state.update(active=False, stopReason='requested_pause', paused=True, pauseVerified=True)
        elif op == 'heartbeat' and self.race:
            self.state.update(active=False, stopReason='hostile', paused=True, pauseVerified=True)
            raise ValueError('Owner/epoch mismatch or no active supervisor')
        elif op == 'events':
            rows = [r for r in self.events if r['cursor'] > args['afterCursor']]
            self.state['newestCursor'] = len(self.events)
            return SimpleNamespace(structuredContent=dict(success=True, events=rows, nextCursor=len(self.events), gap=False))
        return SimpleNamespace(structuredContent=dict(self.state))


@pytest.mark.asyncio
async def test_restored_journal_shorter_than_checkpoint_reports_gap_before_read(tmp_path):
    store = Store(tmp_path/'state.sqlite')
    try:
        store.set('clock-source:colony:map', dict(cursor=10, epoch=4, context='colony:map:old'))
        store.set('clock-inbox:colony:map:old', [dict(kind='letter_pause')])
        bridge = NativeClock()
        bridge.state.update(epoch=4, newestCursor=8)
        clock = PlayClock(bridge, store, 'colony:map:new')
        rows = await clock.poll()
        assert rows == [dict(kind='event_gap', detail='Restored native journal precedes the saved controller cursor.',
                             saved_cursor=10, restored_cursor=8)]
        assert clock.cursor == 0
        assert [r['kind'] for r in store.get('clock-inbox:colony:map:new')] == ['letter_pause', 'event_gap']
        assert not any(c[1]['op'] in ('events', 'start') for c in bridge.calls)
        await clock.poll()
        assert bridge.calls[-1][1] == dict(op='events', afterCursor=0, limit=128)
    finally:
        store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('active', [False, True])
async def test_same_load_journal_regression_refuses_without_invalid_native_read(active):
    bridge = NativeClock()
    clock = PlayClock(bridge)
    clock.epoch, clock.cursor = 4, 10
    bridge.state.update(epoch=4, newestCursor=8, active=active)
    with pytest.raises(ValueError, match='regressed'):
        await clock.poll()
    assert clock.hold == 'event_journal_error'
    assert not any(c[1]['op'] in ('events', 'start') for c in bridge.calls)


@pytest.mark.asyncio
async def test_dialog_stops_owned_lease_without_resuming():
    bridge = NativeClock(); clock = PlayClock(bridge)
    await clock.change('Normal')
    await clock.pause_for_dialog()
    assert bridge.state['stopReason'] == 'requested_pause'
    assert bridge.state['active'] is False and clock.hold is None
    assert len([c for c in bridge.calls if c[1].get('op') == 'start']) == 1


@pytest.mark.asyncio
async def test_dialog_preserves_external_hold():
    bridge = NativeClock(); clock = PlayClock(bridge)
    await clock.change('Normal')
    bridge.state.update(active=False, stopReason='external_pause')
    with pytest.raises(ValueError, match='Clock held'):
        await clock.pause_for_dialog()
    assert clock.hold == 'external_pause'
    assert not any(c[1].get('op') == 'pause' for c in bridge.calls)


@pytest.mark.asyncio
async def test_dialog_does_not_stop_another_owner():
    bridge = NativeClock(); owner = PlayClock(bridge); other = PlayClock(bridge)
    await owner.change('Normal')
    with pytest.raises(ValueError, match='Another controller'):
        await other.pause_for_dialog()
    assert bridge.state['active'] is True


@pytest.mark.asyncio
async def test_external_pause_latches_until_explicit_player_resume():
    bridge = NativeClock(); clock = PlayClock(bridge)
    await clock.change('Normal')
    bridge.state.update(active=False, stopReason='external_pause', paused=True)
    await clock.poll()
    with pytest.raises(ValueError, match='player must enable'):
        await clock.change('Fast')
    assert len([c for c in bridge.calls if c[1].get('op') == 'start']) == 1
    await clock.change('Paused')
    clock.allow_resume()
    await clock.change('Normal')
    assert bridge.state['active']


@pytest.mark.asyncio
async def test_unsafe_hunting_route_requires_explicit_resume():
    bridge = NativeClock(); clock = PlayClock(bridge)
    await clock.change('Normal')
    bridge.state.update(active=False, stopReason='hunting_route_unsafe', paused=True)
    await clock.poll()
    with pytest.raises(ValueError, match='player must enable'):
        await clock.change('Fast')
    assert clock.hold == 'hunting_route_unsafe'
    assert len([c for c in bridge.calls if c[1].get('op') == 'start']) == 1


@pytest.mark.asyncio
async def test_heartbeat_racing_native_danger_delivers_event_once():
    bridge = NativeClock(); clock = PlayClock(bridge)
    await clock.change('Normal')
    bridge.events = [dict(cursor=1, epoch=clock.epoch, kind='hostile', detail='Nearby hostile')]
    bridge.race = True
    events = await clock.poll()
    assert events[0]['kind'] == 'hostile' and clock.hold is None
    assert await clock.poll() == []
    assert clock.state['pauseVerified']


@pytest.mark.asyncio
async def test_other_clock_owner_is_not_overridden():
    bridge = NativeClock(); first = PlayClock(bridge); second = PlayClock(bridge)
    await first.change('Normal')
    with pytest.raises(ValueError, match='Another controller'):
        await second.change('Normal')
    assert bridge.state['owner'] == first.owner


@pytest.mark.asyncio
async def test_live_clock_renewal_uses_native_lease_not_turn_budget():
    bridge = NativeClock(); clock = PlayClock(bridge)
    await clock.change('Fast', mode='combat', ignored_hostiles='Thing_Megaspider99')
    await clock.poll()
    start = next(c[1] for c in bridge.calls if c[1].get('op') == 'start')
    assert start['hostileWithin'] == 40 and start['ignoredHostileIds'] == 'Thing_Megaspider99'
    assert start['mode'] == 'combat'
    assert any(c[1].get('op') == 'heartbeat' and c[1]['epoch'] == clock.epoch for c in bridge.calls)


@pytest.mark.asyncio
async def test_bounded_clock_requires_native_support_before_start():
    bridge = NativeClock(); clock = PlayClock(bridge)
    bridge.state.pop('nativeTickBoundary')
    with pytest.raises(ValueError, match='lacks tick boundaries'):
        await clock.change('Superfast', max_ticks=600)
    assert not any(args.get('op') == 'start' for _, args in bridge.calls)


@pytest.mark.asyncio
@pytest.mark.parametrize('budget', [0, -1, 1800001, True, 1.5])
async def test_invalid_tick_budget_cannot_start(budget):
    bridge = NativeClock(); clock = PlayClock(bridge)
    with pytest.raises(ValueError, match='budget'):
        await clock.change('Fast', max_ticks=budget)
    assert not bridge.calls


@pytest.mark.asyncio
async def test_heartbeat_does_not_extend_tick_budget_and_budget_stop_is_not_player_hold():
    bridge = NativeClock(); clock = PlayClock(bridge)
    started = await clock.change('Superfast', max_ticks=600)
    await clock.poll()
    assert started['tickDeadline'] == clock.state['tickDeadline'] == 700
    heartbeat = next(args for _, args in bridge.calls if args.get('op') == 'heartbeat')
    assert 'maxTicks' not in heartbeat
    bridge.state.update(active=False, stopReason='tick_budget', paused=True, pauseVerified=True)
    bridge.events = [dict(cursor=1, epoch=clock.epoch, kind='tick_budget', detail='Budget reached')]
    assert (await clock.poll())[0]['kind'] == 'tick_budget'
    assert clock.hold is None and await clock.poll() == []
    assert (await clock.change('Normal', max_ticks=20))['active']


def test_native_tick_boundary_wakes_review_without_switching_to_manual(tmp_path):
    store = Store(tmp_path/'budget.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.mode = 'automate'; rt.execution_window_end = 700; rt.execution_wait_explicit = True
    rt.clock_events = [dict(kind='tick_budget', detail='Budget reached', epoch=1, tick=700)]
    rt.receive_clock_events()
    assert rt.mode == 'automate' and rt.wake.is_set()
    assert rt.execution_window_end is None and not rt.execution_wait_explicit
    assert rt.strategic_state.pending[0]['kind'] == 'native.tick_budget'
    store.close()


@pytest.mark.asyncio
async def test_raw_clock_route_cannot_bypass_manual_or_stale_direction(tmp_path):
    store = Store(tmp_path/'clock.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.sync_identity = AsyncMock(return_value=False)
    rt.supervisor = SimpleNamespace(change=AsyncMock())
    with pytest.raises(ValueError, match='Automation is off'):
        await rt.native('rimworld/set_time_speed', {'speed': 'Normal'})
    rt.mode = 'automate'; rt.chat_revision = 2
    with pytest.raises(ValueError, match='New direction'):
        await rt.control_clock('Normal', expected_revision=1)
    rt.supervisor.change.assert_not_awaited()
    store.close()


def test_native_events_are_evidence_and_external_pause_prevents_auto_resume(tmp_path):
    store = Store(tmp_path/'events.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.mode, rt.resume_after_review = 'automate', True
    rt.clock_events = [dict(kind='external_pause', detail='Clock paused outside controller', epoch=1)]
    rt.receive_clock_events()
    assert rt.mode == 'manual' and not rt.resume_after_review
    assert rt.chat[-1]['kind'] == 'clock_event' and rt.chat[-1]['id']
    assert rt.chat_revision == 1 and rt.wake.is_set()
    store.close()


@pytest.mark.asyncio
async def test_heartbeat_runs_while_writer_lock_is_busy(tmp_path):
    import asyncio
    store = Store(tmp_path/'independent.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    called = asyncio.Event()
    async def poll():
        called.set()
        return []
    rt.supervisor = SimpleNamespace(poll=poll)
    await rt.lock.acquire()
    watcher = asyncio.create_task(rt.watch_clock())
    try:
        await asyncio.wait_for(called.wait(), 1)
        assert rt.lock.locked()
    finally:
        rt.stopped=True; rt.shutdown.set(); rt.lock.release()
        await watcher
        store.close()


@pytest.mark.asyncio
async def test_player_resume_is_not_undone_by_buffered_old_pause(tmp_path):
    store = Store(tmp_path/'resume.sqlite')
    bridge = NativeClock()
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.bridge = bridge; rt.supervisor = PlayClock(bridge); rt.connected = True
    rt.identity = {'colonyId':'test','mapId':1,'loadToken':'load'}
    rt.game = SimpleNamespace(invoke=AsyncMock(return_value={'success':True,'floors':{},'commitments':{},'stopped':[]}))
    rt.sync_identity = AsyncMock(return_value=False)
    await rt.supervisor.change('Normal')
    bridge.state.update(active=False, stopReason='external_pause', paused=True)
    rt.clock_events = [dict(kind='external_pause', detail='Outside pause', epoch=rt.supervisor.epoch)]
    await rt.set_mode('automate')
    rt.receive_clock_events()
    assert rt.mode == 'automate' and rt.resume_after_review
    assert (await rt.control_clock('Normal'))['active']
    store.close()


@pytest.mark.asyncio
async def test_combat_injury_wakes_strategist_and_invalidates_prior_orders(tmp_path):
    store=Store(tmp_path/'injury.sqlite')
    rt=BridgeRuntime(store,tmp_path,model_factory=lambda _:SimpleNamespace())
    rt.mode='automate';rt.game=SimpleNamespace(invoke=AsyncMock())
    rt.clock_events=[dict(kind='colonist_injury',detail='Sam was injured. Game paused for review.',
        epoch=1,event={'pawnId':275,'newWound':True,'healthNow':.89})]
    prior=rt.chat_revision
    rt.receive_clock_events()
    assert rt.mode=='automate' and rt.wake.is_set() and not rt.resume_after_review
    assert rt.strategic_state.pending[0]['kind']=='native.colonist_injury'
    assert rt.chat[-1]['kind']=='clock_event'
    with pytest.raises(ValueError,match='New player direction'):
        await rt.native('home/order',{'action':'attack'},expected_revision=prior)
    rt.game.invoke.assert_not_awaited()
    store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('buffered', [True, False])
async def test_native_dispatch_ingests_interruption_before_background_loop(tmp_path, buffered):
    store = Store(tmp_path/'dispatch-interruption.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.mode = 'automate'
    rt.sync_identity = AsyncMock(return_value=False)
    rt.game = SimpleNamespace(invoke=AsyncMock())
    event = dict(kind='external_pause', detail='Real native pause before dispatch', epoch=1)
    rt.clock_events = [event] if buffered else []
    rt.supervisor = SimpleNamespace(poll=AsyncMock(return_value=[] if buffered else [event]), acknowledged_stop=None)
    try:
        with pytest.raises(ValueError, match='direction'):
            await rt.native('home/order', {'action': 'draft', 'pawn': 'Thing_Human1', 'dryRun': False}, expected_revision=0)
        assert rt.mode == 'manual' and rt.chat_revision == 1
        assert rt.current_plan.control['player_direction'] == 1
        rt.game.invoke.assert_not_awaited()
    finally:
        store.close()


@pytest.mark.asyncio
async def test_old_clock_order_cannot_resume_after_unconsumed_injury(tmp_path):
    store = Store(tmp_path/'clock-interruption.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.mode = 'automate'
    rt.sync_identity = AsyncMock(return_value=False)
    rt.supervisor = SimpleNamespace(poll=AsyncMock(return_value=[
        dict(kind='colonist_injury', detail='Native injury before clock dispatch', epoch=1)]), change=AsyncMock())
    try:
        with pytest.raises(ValueError, match='New direction'):
            await rt.control_clock('Normal', expected_revision=0)
        rt.supervisor.change.assert_not_awaited()
        assert rt.wake.is_set() and not rt.resume_after_review
    finally:
        store.close()


@pytest.mark.asyncio
async def test_pause_during_policy_preparation_prevents_clock_start(tmp_path, monkeypatch):
    store = Store(tmp_path/'policy-interruption.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.mode = 'automate'
    rt.sync_identity = AsyncMock(return_value=False)
    monkeypatch.setattr('rimbot.production_policy.sync_production_policy', AsyncMock())
    rt.supervisor = SimpleNamespace(poll=AsyncMock(side_effect=[[], [
        dict(kind='external_pause', detail='Pause during policy preparation', epoch=1)]]),
        change=AsyncMock(), acknowledged_stop=None)
    try:
        with pytest.raises(InterruptedError, match='preparing the clock'):
            await rt.control_clock('Normal', expected_revision=0)
        rt.supervisor.change.assert_not_awaited()
        assert rt.mode == 'manual'
    finally:
        store.close()
