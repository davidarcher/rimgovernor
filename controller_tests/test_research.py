from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimgovernor.bridge_game import BridgeGame, is_write


@pytest.mark.parametrize('args,expected',[({},False),({'set':'Project'},True),
    ({'set':'Project','dryRun':True},False),({'set':'Project','dryRun':False},True)])
def test_research_write_classification(args,expected):
    assert is_write('home/research',args)==expected


def game(payload):
    schema={'type':'object','properties':{'set':{'type':'string'},'dryRun':{'type':'boolean'},'watch':{'type':'boolean'}}}
    bridge=SimpleNamespace(detail=AsyncMock(return_value=SimpleNamespace(structuredContent={'inputSchema':schema})),
        call=AsyncMock(return_value=SimpleNamespace(structuredContent=payload)))
    return BridgeGame(bridge)


@pytest.mark.asyncio
async def test_read_without_flags_and_nested_refusal():
    g=game({'current':None})
    assert await g.invoke('home/research',{})=={'current':None}
    g.bridge.call.return_value=SimpleNamespace(structuredContent={'success':True,'write':{'refused':True,'reason':'Prerequisites missing'}})
    with pytest.raises(ValueError,match='Prerequisites missing'):
        await g.invoke('home/research',{'set':'Project','dryRun':True})


@pytest.mark.asyncio
async def test_manual_cannot_select_and_automate_suppresses_ui():
    g=game({'write':{'refused':False}})
    with pytest.raises(ValueError,match='Automation is off'):
        await g.invoke('home/research',{'set':'Project','dryRun':False})
    g.bridge.call.assert_not_awaited()
    await g.invoke('home/research',{'set':'Project','dryRun':False},allow_write=True)
    assert g.bridge.call.call_args.kwargs['watch'] is False
