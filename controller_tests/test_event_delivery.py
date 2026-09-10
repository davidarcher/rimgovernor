"""Crash boundaries use reopened SQLite, never successful receipts as evidence."""
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.bridge_runtime import BridgeRuntime
from rimbot.clock_control import PlayClock
from rimbot.store import Store


def runtime(path):
    store = Store(path)
    rt = BridgeRuntime(store, path.parent, model_factory=lambda _: SimpleNamespace())
    rt.colony = 'colony:1'
    rt.context_token = 'colony:1:load'
    return rt


@pytest.mark.asyncio
async def test_lost_chat_acknowledgment_is_deduplicated_after_reopen(tmp_path):
    path = tmp_path/'state.sqlite'
    rt = runtime(path)
    first = await rt.steer('Protect food', request_id='request-1', session_id=rt.context_token)
    rt.store.close()
    rt = runtime(path)
    assert await rt.steer('Protect food', request_id='request-1') == first
    assert len(rt.store.history(rt.colony)) == 1
    with pytest.raises(ValueError, match='different content'):
        await rt.steer('Use food', request_id='request-1')
    with pytest.raises(ValueError, match='Colony changed'):
        await rt.steer('Protect food', request_id='request-1', session_id='old')
    rt.store.close()


@pytest.mark.asyncio
async def test_chat_snapshot_failure_rolls_back_history_and_retry_identity(tmp_path, monkeypatch):
    rt = runtime(tmp_path/'state.sqlite')
    original = rt.persist
    def fail():
        original()
        raise OSError('disk failure before acknowledgment')
    monkeypatch.setattr(rt, 'persist', fail)
    with pytest.raises(OSError):
        await rt.steer('Protect food', request_id='request-1')
    assert rt.chat_revision == 0 and rt.chat == []
    assert rt.store.history(rt.colony) == []
    assert rt.store.get('bridge:'+rt.colony) is None
    monkeypatch.setattr(rt, 'persist', original)
    assert (await rt.steer('Protect food', request_id='request-1'))['revision'] == 1
    rt.store.close()


@pytest.mark.asyncio
async def test_crash_after_fetch_before_delivery_reopens_inbox_once(tmp_path):
    path = tmp_path/'state.sqlite'
    rt = runtime(path)
    event = dict(cursor=1, epoch=7, kind='external_pause', detail='Player paused')
    bridge = SimpleNamespace(call=AsyncMock(side_effect=[
        SimpleNamespace(structuredContent={'success': True, 'active': False}),
        SimpleNamespace(structuredContent={'success': True, 'events': [event], 'nextCursor': 1})]))
    clock = PlayClock(bridge, rt.store, rt.context_token)
    clock.epoch = 7
    assert await clock.poll() == [event]
    rt.store.close()  # No receive_clock_events or runtime persist occurred.
    rt = runtime(path)
    clock = PlayClock(bridge, rt.store, rt.context_token)
    assert clock.cursor == 1 and clock.epoch == 7
    rt.mode = 'automate'
    rt.receive_clock_events()
    assert rt.mode == 'manual' and rt.chat_revision == 1
    rt.receive_clock_events()
    assert len(rt.store.history(rt.colony)) == 1
    rt.store.close()


def test_nested_transaction_failure_keeps_previous_snapshot(tmp_path):
    store = Store(tmp_path/'state.sqlite')
    store.set('value', 1)
    with pytest.raises(RuntimeError):
        with store.transaction():
            store.set('value', 2)
            store.event('colony', 'human', text='test')
            raise RuntimeError('crash before commit')
    store.close()
    store = Store(tmp_path/'state.sqlite')
    assert store.get('value') == 1
    assert store.history('colony') == []
    store.close()
