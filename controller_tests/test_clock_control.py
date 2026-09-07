from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.clock_control import PlayClock
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store


class NativeClock:
    def __init__(self):
        self.state = {'success': True, 'active': False, 'epoch': 0, 'newestCursor': 0}
        self.calls = []
        self.events = []
        self.race = False

    async def call(self, name, **args):
        self.calls.append((name, args))
        op = args.get('op')
        if name == 'home/status':
            return SimpleNamespace(structuredContent={'time': {'paused': True}})
        if op == 'start':
            self.state = dict(success=True, active=True, owner=args['owner'], epoch=self.state['epoch']+1, newestCursor=len(self.events))
        elif op == 'pause':
            self.state.update(active=False, stopReason='requested_pause', paused=True, pauseVerified=True)
        elif op == 'heartbeat' and self.race:
            self.state.update(active=False, stopReason='hostile', paused=True, pauseVerified=True)
            raise ValueError('Owner/epoch mismatch or no active supervisor')
        elif op == 'events':
            rows = [r for r in self.events if r['cursor'] > args['afterCursor']]
            return SimpleNamespace(structuredContent=dict(success=True, events=rows, nextCursor=len(self.events), gap=False))
        return SimpleNamespace(structuredContent=dict(self.state))


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
    rt.sync_identity = AsyncMock(return_value=False)
    await rt.supervisor.change('Normal')
    bridge.state.update(active=False, stopReason='external_pause', paused=True)
    rt.clock_events = [dict(kind='external_pause', detail='Outside pause', epoch=rt.supervisor.epoch)]
    await rt.set_mode('automate')
    rt.receive_clock_events()
    assert rt.mode == 'automate' and rt.resume_after_review
    assert (await rt.control_clock('Normal'))['active']
    store.close()
