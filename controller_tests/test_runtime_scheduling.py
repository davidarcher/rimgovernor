"""Scheduling latency, serialization and backoff without a native game."""
import asyncio
from contextlib import asynccontextmanager
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store


@asynccontextmanager
async def scheduler(tmp_path):
    store = Store(tmp_path/'schedule.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.review = AsyncMock()
    rt.advance_execution = AsyncMock(return_value=None)
    task = asyncio.create_task(rt.schedule_work())
    try:
        yield rt
    finally:
        rt.stopped = True
        rt.shutdown.set()
        await asyncio.wait_for(task, .5)
        for worker in (rt.review_task, rt.execution_task):
            if worker:
                worker.cancel()
                await asyncio.gather(worker, return_exceptions=True)
        store.close()


@pytest.mark.asyncio
async def test_direction_and_review_completion_do_not_wait_for_poll(tmp_path):
    async with scheduler(tmp_path) as rt:
        await asyncio.sleep(0)  # Scheduler is already waiting in Manual.
        executed = asyncio.Event()
        async def execute():
            executed.set()
        rt.advance_execution.side_effect = execute
        rt.mode = 'automate'
        rt.wake.set()
        await asyncio.wait_for(executed.wait(), .5)
        rt.review.assert_awaited_once()


@pytest.mark.asyncio
async def test_budget_completion_continues_then_idle_backs_off(tmp_path):
    async with scheduler(tmp_path) as rt:
        finished = asyncio.Event()
        calls = 0
        async def execute():
            nonlocal calls
            calls += 1
            if calls == 3:
                finished.set()
                return None
            return True
        rt.advance_execution.side_effect = execute
        rt.mode = 'automate'
        rt.work_changed.set()
        await asyncio.wait_for(finished.wait(), .5)
        await asyncio.sleep(.1)
        assert calls == 3


@pytest.mark.asyncio
async def test_direction_during_execution_waits_for_single_writer(tmp_path):
    async with scheduler(tmp_path) as rt:
        entered, release, reviewed = asyncio.Event(), asyncio.Event(), asyncio.Event()
        async def execute():
            entered.set()
            await release.wait()
        async def review():
            assert rt.execution_task.done()
            reviewed.set()
        rt.advance_execution.side_effect = execute
        rt.review.side_effect = review
        rt.mode = 'automate'
        rt.work_changed.set()
        await asyncio.wait_for(entered.wait(), .5)
        rt.mode = 'manual'
        rt.chat_revision += 1
        rt.wake.set()
        await asyncio.sleep(.05)
        rt.review.assert_not_awaited()
        release.set()
        await asyncio.wait_for(reviewed.wait(), .5)
        assert rt.advance_execution.await_count == 1


@pytest.mark.asyncio
async def test_review_requeues_direction_arriving_during_review(tmp_path):
    async with scheduler(tmp_path) as rt:
        reviewed = asyncio.Event()
        async def review():
            if rt.review.await_count == 1:
                rt.wake.set()
            else:
                reviewed.set()
        rt.review.side_effect = review
        rt.wake.set()
        await asyncio.wait_for(reviewed.wait(), .5)
        assert rt.review.await_count == 2
        rt.advance_execution.assert_not_awaited()


@pytest.mark.asyncio
async def test_long_event_notifications_do_not_create_retry_spin(tmp_path):
    async with scheduler(tmp_path) as rt:
        entered = asyncio.Event()
        async def review():
            rt.defer_long_event(ValueError('A long event (autosave, map generation) is running or queued'))
            entered.set()
        rt.review.side_effect = review
        rt.wake.set()
        await asyncio.wait_for(entered.wait(), .5)
        for _ in range(5):
            rt.work_changed.set()
            await asyncio.sleep(.01)
        assert rt.review.await_count == 1
        assert rt.wake.is_set()


@pytest.mark.asyncio
async def test_buffered_native_hold_is_ingested_before_work(tmp_path):
    async with scheduler(tmp_path) as rt:
        ingested = asyncio.Event()
        def receive():
            rt.clock_events.clear()
            rt.mode = 'manual'
            ingested.set()
        rt.receive_clock_events = receive
        rt.mode = 'automate'
        rt.clock_events.append({'kind': 'external_pause'})
        rt.work_changed.set()
        await asyncio.wait_for(ingested.wait(), .5)
        rt.advance_execution.assert_not_awaited()


@pytest.mark.asyncio
async def test_native_player_input_defers_work_without_losing_direction(tmp_path):
    async with scheduler(tmp_path) as rt:
        reviewed = asyncio.Event()
        rt.review.side_effect = lambda: reviewed.set()
        rt.player_input = SimpleNamespace(ready=True, native=True, live=lambda: True)
        rt.wake.set()
        await asyncio.sleep(.05)
        rt.review.assert_not_awaited()
        rt.player_input = None
        await asyncio.wait_for(reviewed.wait(), .5)
