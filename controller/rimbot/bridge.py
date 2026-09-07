"""RimBridgeServer trial transport through GABS's standard MCP session.

No RIMAPI response emulation or implicit retries of game mutations. The caller
retains native content, error flags and operation receipts for reconciliation.
This transport is not exposed to the model until capability policy is applied.
"""
from contextlib import asynccontextmanager
from datetime import timedelta
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.types import CallToolResult


class BridgeError(RuntimeError):
    def __init__(self, tool: str, result: CallToolResult):
        super().__init__(f"Bridge tool failed: {tool}")
        self.tool = tool
        self.result = result


class BridgeClient:
    def __init__(self, session: ClientSession, game_id: str = "rimbot-trial"):
        self.session = session
        self.game_id = game_id

    async def core(self, name: str, **arguments) -> CallToolResult:
        result = await self.session.call_tool(name, arguments)
        if result.isError or (result.structuredContent or {}).get("success") is False:
            raise BridgeError(name, result)
        return result

    async def connect(self) -> CallToolResult:
        return await self.core("games_connect", gameId=self.game_id)

    async def names(self, *, cursor: str = "", query: str = "") -> CallToolResult:
        return await self.core("games_tool_names", gameId=self.game_id, cursor=cursor, query=query)

    async def detail(self, name: str) -> CallToolResult:
        return await self.core("games_tool_detail", gameId=self.game_id, tool=name)

    async def call(self, name: str, **arguments) -> CallToolResult:
        return await self.core("games_call_tool", gameId=self.game_id,
                               tool=name, arguments=arguments)


@asynccontextmanager
async def bridge_session(executable: Path, config_dir: Path, game_id="rimbot-trial"):
    parameters = StdioServerParameters(command=str(executable.resolve()), args=[
        "server", "stdio", "--configDir", str(config_dir.resolve()),
        "--log-level", "error"])
    async with stdio_client(parameters) as (reader, writer):
        async with ClientSession(reader, writer, read_timeout_seconds=timedelta(seconds=120)) as session:
            await session.initialize()
            yield BridgeClient(session, game_id)

