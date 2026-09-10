import importlib.util
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from mcp.types import CallToolResult
from rimbot.bridge import BridgeError
from rimbot.clock_control import PlayClock

spec = importlib.util.spec_from_file_location('research_acceptance', Path(__file__).resolve().parents[1]/'scripts/research_acceptance.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


def claim_error():
    return BridgeError('games_call_tool', CallToolResult(isError=True, content=[], structuredContent={'error':
        "Failed to claim runtime ownership: a launch claim for 'trial' was published while preparing this operation; re-check games_status and retry"}))


@pytest.mark.asyncio
@pytest.mark.parametrize('change', [None, 'owner', 'epoch', 'active', 'leaseRemainingMs'])
async def test_polling_fault_requires_observed_same_live_lease(change):
    bridge = SimpleNamespace(core=AsyncMock(), game_id='trial')
    clock = PlayClock(bridge); clock.epoch = 7
    latest = dict(success=True, active=True, owner=clock.owner, epoch=7, leaseRemainingMs=14000)
    if change: latest[change] = {'owner':'other','epoch':8,'active':False,'leaseRemainingMs':0}[change]
    clock.poll = AsyncMock(side_effect=claim_error())
    clock.call = AsyncMock(return_value=latest)
    report = {}
    if change:
        with pytest.raises(BridgeError): await probe.poll_research_clock(clock, report)
        assert report == {}
    else:
        await probe.poll_research_clock(clock, report)
        assert report['clock_claim_refusals'][0]['observed'] == latest
    clock.poll.assert_awaited_once()
    clock.call.assert_awaited_once_with(op='status')
    bridge.core.assert_awaited_once_with('games_status', gameId='trial')


@pytest.mark.asyncio
async def test_polling_does_not_tolerate_ambiguous_transport_errors():
    clock = PlayClock(SimpleNamespace(core=AsyncMock(), game_id='trial'))
    clock.poll = AsyncMock(side_effect=BridgeError('games_call_tool', CallToolResult(isError=True,
        content=[], structuredContent={'error':'Response lost after execution'})))
    clock.call = AsyncMock()
    with pytest.raises(BridgeError): await probe.poll_research_clock(clock, {})
    clock.call.assert_not_awaited()
