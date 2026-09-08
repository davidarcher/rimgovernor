import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.bridge_runtime import BridgeRuntime


def runtime(schema):
    return SimpleNamespace(lock=asyncio.Lock(), game=SimpleNamespace(
        describe=AsyncMock(return_value=schema), invoke=AsyncMock(return_value={'dryRun':True})))


@pytest.mark.asyncio
async def test_omitted_preview_flag_uses_discovered_contract_without_mutating_request():
    rt=runtime({'properties':{'dryRun':{'type':'boolean'}}})
    args={'pawn':'Thing_Human1'}
    await BridgeRuntime.inspect_native(rt, 'home/pawn_config', args)
    assert args == {'pawn':'Thing_Human1'}
    rt.game.invoke.assert_awaited_once_with('home/pawn_config',
        {'pawn':'Thing_Human1','dryRun':True}, allow_write=False)


@pytest.mark.asyncio
async def test_explicit_write_is_never_reinterpreted_as_preview():
    rt=runtime({'properties':{'dryRun':{'type':'boolean'}}})
    with pytest.raises(PermissionError, match='Inspection cannot execute writes'):
        await BridgeRuntime.inspect_native(rt,'home/pawn_config',{'dryRun':False})
    rt.game.describe.assert_not_awaited()
    rt.game.invoke.assert_not_awaited()


@pytest.mark.asyncio
async def test_write_without_preview_contract_is_not_called():
    rt=runtime({'properties':{}})
    with pytest.raises(PermissionError):
        await BridgeRuntime.inspect_native(rt,'rimworld/dismiss_letter',{})
    rt.game.invoke.assert_not_awaited()


@pytest.mark.asyncio
async def test_read_only_query_passes_through_unchanged():
    rt=runtime({})
    await BridgeRuntime.inspect_native(rt,'home/list_pawns',{'colonistsOnly':True})
    rt.game.describe.assert_not_awaited()
    rt.game.invoke.assert_awaited_once_with('home/list_pawns',{'colonistsOnly':True},allow_write=False)
