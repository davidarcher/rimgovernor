import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store

@pytest.mark.asyncio
async def test_direction_rechecked_after_lock(tmp_path):
    store = Store(tmp_path/'test.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.game = SimpleNamespace(invoke=AsyncMock())
    await rt.lock.acquire()
    pending = asyncio.create_task(rt.native('home/order', {}, expected_revision=0))
    await asyncio.sleep(0)
    await rt.steer('Stop and reconsider')
    rt.lock.release()
    with pytest.raises(ValueError, match='New player direction'):
        await pending
    rt.game.invoke.assert_not_awaited()
    store.close()

@pytest.mark.asyncio
async def test_failed_review_does_not_retry_forever(tmp_path):
    store = Store(tmp_path/'test.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.game = SimpleNamespace(query=AsyncMock(side_effect=RuntimeError('offline')))
    rt.chat_revision = 1
    await rt.review()
    assert rt.mode == 'manual'
    assert rt.handled_revision == 1
    assert not rt.wake.is_set()
    store.close()
