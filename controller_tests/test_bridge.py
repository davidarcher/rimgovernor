from unittest.mock import AsyncMock
import asyncio

import pytest
pytest.importorskip("mcp")
from mcp.types import CallToolResult, TextContent
from rimbot.bridge import BridgeClient, BridgeError


async def test_connect_waits_for_readiness_without_replaying_start_or_load(monkeypatch):
    ready = CallToolResult(content=[])
    pending = CallToolResult(content=[], isError=True,
                            structuredContent={'message': "Game is not connected via GABP"})
    session = AsyncMock()
    session.call_tool.side_effect = [ready, pending, ready]
    monkeypatch.setattr('rimbot.bridge.asyncio.sleep', AsyncMock())
    assert await BridgeClient(session).connect() is ready
    assert [call.args[0] for call in session.call_tool.await_args_list] == [
        'games_connect', 'games_tool_names', 'games_tool_names']


async def test_native_result_and_error_are_preserved_without_retry():
    session = AsyncMock()
    result = CallToolResult(content=[TextContent(type="text", text="queued")],
                            structuredContent={"operationId": "op1", "status": "queued"})
    session.call_tool.return_value = result
    bridge = BridgeClient(session)
    assert await bridge.call("rimworld/apply_architect_designator", x=3, z=4) is result
    session.call_tool.assert_awaited_once_with("games_call_tool", {
        "gameId": "rimbot-trial", "tool": "rimworld/apply_architect_designator",
        "arguments": {"x": 3, "z": 4}})
    session.call_tool.reset_mock()
    failure = CallToolResult(isError=True, content=[TextContent(type="text", text="blocked")])
    session.call_tool.return_value = failure
    with pytest.raises(BridgeError) as caught:
        await bridge.call("rimworld/execute_gizmo", gizmoId="old")
    assert caught.value.result is failure
    assert session.call_tool.await_count == 1


async def test_discovery_requests_one_schema_not_entire_catalog():
    session = AsyncMock()
    session.call_tool.return_value = CallToolResult(content=[])
    bridge = BridgeClient(session)
    await bridge.detail("rimworld/get_cell_info")
    session.call_tool.assert_awaited_once_with("games_tool_detail", {
        "gameId": "rimbot-trial", "tool": "rimworld/get_cell_info"})


async def test_native_failure_inside_successful_transport_is_failure():
    session = AsyncMock()
    session.call_tool.return_value = CallToolResult(content=[], isError=False,
        structuredContent={"success": False, "message": "No active map"})
    with pytest.raises(BridgeError):
        await BridgeClient(session).call("rimworld/list_architect_categories")


async def test_clock_reads_and_mutations_share_one_native_request_queue():
    entered, release = asyncio.Event(), asyncio.Event()
    calls = []
    async def call(name, arguments):
        calls.append(arguments['tool'])
        if len(calls) == 1:
            entered.set()
            await release.wait()
        return CallToolResult(content=[])
    bridge = BridgeClient(AsyncMock(call_tool=AsyncMock(side_effect=call)))
    write = asyncio.create_task(bridge.call('home/place_building', dryRun=False))
    await entered.wait()
    read = asyncio.create_task(bridge.call('home/supervised_play', op='status'))
    await asyncio.sleep(0)
    assert calls == ['home/place_building']
    release.set()
    await asyncio.gather(write, read)
    assert calls == ['home/place_building', 'home/supervised_play']


async def test_cancelled_queued_request_never_reaches_native_session():
    bridge = BridgeClient(AsyncMock())
    timings = []
    bridge.timing_callback = timings.append
    async with bridge.request_lock:
        task = asyncio.create_task(bridge.call('home/place_building', dryRun=False))
        await asyncio.sleep(0)
        task.cancel()
        with pytest.raises(asyncio.CancelledError): await task
    bridge.session.call_tool.assert_not_awaited()
    assert timings[0]['error_type'] == 'CancelledError'
    assert 'session_seconds' not in timings[0]
    assert timings[0]['queue_seconds'] >= 0


async def test_timing_distinguishes_queue_and_session_and_cannot_replace_receipt():
    async def call(*args):
        await asyncio.sleep(.01)
        return CallToolResult(content=[], structuredContent={'operation': {'DurationMs': 2}})
    bridge = BridgeClient(AsyncMock(call_tool=AsyncMock(side_effect=call)))
    timings = []
    bridge.timing_callback = timings.append
    async with bridge.request_lock:
        task = asyncio.create_task(bridge.call('home/status'))
        await asyncio.sleep(.02)
    result = await task
    assert timings[0]['queue_seconds'] >= .015
    assert timings[0]['session_seconds'] >= .005
    assert timings[0]['native_ms'] == 2
    assert timings[0]['success']
    def broken(_): raise RuntimeError('observer failure')
    bridge.timing_callback = broken
    assert (await bridge.call('home/status')).structuredContent == result.structuredContent


async def test_failed_durable_request_is_timed_without_native_dispatch(monkeypatch):
    class Recorder:
        context = {}
        def event(self, *args, **kwargs): raise OSError('disk full')
    monkeypatch.setattr('rimbot.flight_recorder.recorder', lambda: Recorder())
    bridge = BridgeClient(AsyncMock())
    timings = []
    bridge.timing_callback = timings.append
    with pytest.raises(OSError, match='disk full'):
        await bridge.call('home/order', action='goto')
    bridge.session.call_tool.assert_not_awaited()
    assert timings[0]['error_type'] == 'OSError'
    assert 'session_seconds' not in timings[0]


async def test_startup_waits_for_gabs_background_connector_without_superseding_it():
    def result(**data): return CallToolResult(content=[], structuredContent=data)
    session = AsyncMock(call_tool=AsyncMock(side_effect=[result(backgroundConnect=True, gabpConnected=False),
        result(status='running', toolCount=0), result(status='running', toolCount=156)]))
    bridge = BridgeClient(session)
    await bridge.core('games_start', gameId=bridge.game_id)
    assert (await bridge.connect()).structuredContent['toolCount']==156
    assert [call.args[0] for call in session.call_tool.await_args_list]==['games_start','games_status','games_status']


async def test_startup_exit_is_retained_without_relaunch_or_reconnect():
    session = AsyncMock(call_tool=AsyncMock(side_effect=[
        CallToolResult(content=[], structuredContent={'backgroundConnect':True}),
        CallToolResult(content=[], structuredContent={'status':'stopped','toolCount':0})]))
    bridge = BridgeClient(session)
    await bridge.core('games_start', gameId=bridge.game_id)
    with pytest.raises(RuntimeError, match='startup stopped'):
        await bridge.connect()
    assert session.call_tool.await_count==2


async def test_connected_start_does_not_create_another_connection():
    session = AsyncMock(call_tool=AsyncMock(return_value=CallToolResult(content=[],
        structuredContent={'gabpConnected':True})))
    bridge = BridgeClient(session)
    started = await bridge.core('games_start', gameId=bridge.game_id)
    assert await bridge.connect() is started
    assert session.call_tool.await_count==1

