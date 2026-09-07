from unittest.mock import AsyncMock

import pytest
pytest.importorskip("mcp")
from mcp.types import CallToolResult, TextContent
from rimbot.bridge import BridgeClient, BridgeError


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

