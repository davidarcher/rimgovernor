"""RimBridgeServer trial transport through GABS's standard MCP session.

No RIMAPI response emulation or implicit retries of game mutations. The caller
retains native content, error flags and operation receipts for reconciliation.
This transport is not exposed to the model until capability policy is applied.
"""
from contextlib import asynccontextmanager
from copy import deepcopy
import asyncio
import logging
import os
from datetime import timedelta
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.types import CallToolResult


class BridgeError(RuntimeError):
    def __init__(self, tool: str, result: CallToolResult):
        payload = result.structuredContent or {}
        detail = payload.get('message') or payload.get('error') or next((getattr(c, 'text', '') for c in result.content), '')
        super().__init__(f"Bridge tool failed: {tool}: {str(detail)[:1200]}")
        self.tool = tool
        self.result = result
        self.detail = str(detail)


async def runtime_file_read(operation, *args, **kwargs):
    """Retry only reads after the observed GABS publication/launch-claim faults.

    Callers must establish read-only policy before entering this helper. A lost
    mutation receipt is ambiguous even when its error mentions a runtime file.
    """
    for attempt in range(3):
        try:
            return await operation(*args, **kwargs)
        except BridgeError as error:
            detail = error.detail.casefold()
            transient = all(part in detail for part in (
                'failed to claim runtime ownership', 'failed to publish runtime state: rename ',
                'runtime.json', 'access is denied'))
            owner = getattr(operation, '__self__', None)
            claim_changed = isinstance(owner, BridgeClient) and all(part in detail for part in (
                'failed to claim runtime ownership', 'a launch claim for',
                'was published while preparing this operation', 're-check games_status and retry'))
            if not (transient or claim_changed) or attempt == 2:
                raise
            if claim_changed:
                await owner.core('games_status', gameId=owner.game_id)
            logging.getLogger(__name__).warning(
                'Retrying read after GABS runtime publication failure (%s/2): %s', attempt + 1, error)
            await asyncio.sleep(0.05 * (attempt + 1))


class BridgeClient:
    def __init__(self, session: ClientSession, game_id: str = "rimbot-trial"):
        self.session = session
        self.game_id = game_id
        self.request_lock = asyncio.Lock()
        self.recording_context = None
        self.start_result = None

    async def core(self, name: str, **arguments) -> CallToolResult:
        from .flight_recorder import recorder
        recording = recorder()
        if recording and self.recording_context:
            recording.context = self.recording_context()
        context = deepcopy(recording.context) if recording else None
        request = recording.event('native_request', context=context, tool=name, arguments=arguments) if recording else None
        try:
            # Serialize GABS ownership preparation without replaying uncertain writes.
            async with self.request_lock:
                result = await self.session.call_tool(name, arguments)
            if recording:
                recording.event('native_response', context=context, durable=False, request=request, tool=name, result=result.model_dump(mode='json'))
            if result.isError or (result.structuredContent or {}).get("success") is False:
                raise BridgeError(name, result)
            if name == 'games_start':
                self.start_result = result
            return result
        except BaseException as error:
            if recording:
                recording.event('native_error', context=context, request=request, tool=name, error=repr(error))
            raise

    async def connect(self) -> CallToolResult:
        started, self.start_result = self.start_result, None
        startup = started.structuredContent or {} if started else {}
        if startup.get('gabpConnected') is True:
            return started
        if startup.get('backgroundConnect') is True:
            # GABS already owns a pending connector. A second connect can supersede
            # its authenticated client while tools are being published.
            async with asyncio.timeout(120):
                while True:
                    status = await self.core('games_status', gameId=self.game_id)
                    state = status.structuredContent or {}
                    if state.get('status') in ('stopped', 'stale-runtime-cleaned', 'disconnected'):
                        raise RuntimeError('Native startup stopped before GABS published tools: '+str(state.get('status')))
                    if state.get('status') in ('running', 'connected') and state.get('toolCount', 0) > 0:
                        return status
                    await asyncio.sleep(.5)
        result = await self.core("games_connect", gameId=self.game_id, timeout=60)
        # Process startup and GABP readiness are separate. Only repeat discovery;
        # never retry a load or another game mutation after an uncertain result.
        deadline = asyncio.get_running_loop().time() + 120
        while True:
            try:
                await self.names(query='rimworld/load_game_ready')
                return result
            except BridgeError as error:
                if 'not connected via GABP' not in error.detail or asyncio.get_running_loop().time() >= deadline:
                    raise
                await asyncio.sleep(.25)

    async def names(self, *, cursor: str = "", query: str = "") -> CallToolResult:
        return await self.core("games_tool_names", gameId=self.game_id, cursor=cursor, query=query)

    async def detail(self, name: str) -> CallToolResult:
        return await self.core("games_tool_detail", gameId=self.game_id, tool=name)

    async def call(self, name: str, **arguments) -> CallToolResult:
        return await self.core("games_call_tool", gameId=self.game_id,
                               tool=name, arguments=arguments)


@asynccontextmanager
async def bridge_session(executable: Path, config_dir: Path, game_id="rimbot-trial"):
    log_level = os.environ.get('RIMBOT_GABS_LOG_LEVEL', 'error')
    if log_level not in ('debug', 'info', 'warn', 'error'):
        raise ValueError('RIMBOT_GABS_LOG_LEVEL must be debug, info, warn or error')
    parameters = StdioServerParameters(command=str(executable.resolve()), args=[
        "server", "stdio", "--configDir", str(config_dir.resolve()),
        "--log-level", log_level], env={key: os.environ[key] for key in (
            'DISPLAY', 'XAUTHORITY', 'XDG_RUNTIME_DIR', 'LIBGL_ALWAYS_SOFTWARE',
            'GALLIUM_DRIVER', 'LP_NUM_THREADS', 'RIMBOT_PRIVATE_DISPLAY', 'RIMBOT_VIDEO_READBACK',
            'LD_LIBRARY_PATH', 'MESA_D3D12_DEFAULT_ADAPTER_NAME') if key in os.environ})
    async with stdio_client(parameters) as (reader, writer):
        async with ClientSession(reader, writer, read_timeout_seconds=timedelta(seconds=120)) as session:
            await session.initialize()
            yield BridgeClient(session, game_id)



def gabs_executable(root, configuration=None):
    """Resolve an explicit worker binary, retaining the legacy Windows default."""
    import json
    root = Path(root).resolve()
    directory = Path(configuration) if configuration is not None else root/'config'
    config = json.loads((directory/'config.json').read_text(encoding='utf8'))
    configured = config.get('rimbot', {}).get('gabsExecutable')
    path = Path(configured) if configured else Path('gabs/gabs-v1.1.1-windows-amd64/gabs.exe')
    return path if path.is_absolute() else root/path
