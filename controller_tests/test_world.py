from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.bridge_game import BridgeGame, is_write
from rimbot.bridge_observation import ObservationGateway
from rimbot.scout import SCOUT_READS


@pytest.mark.asyncio
@pytest.mark.parametrize('gateway,method',[(BridgeGame,'invoke'),(ObservationGateway,'query')])
async def test_world_view_never_changed(gateway,method):
    bridge=SimpleNamespace(call=AsyncMock())
    g=gateway(bridge)
    with pytest.raises(ValueError,match='player view'):
        if method=='invoke':await g.invoke('home/world',{'show':True,'dryRun':False},allow_write=True)
        else:await g.query('home/world',show=True,dryRun=False)
    bridge.call.assert_not_awaited()


def test_world_is_available_to_scout_and_read_only_by_default():
    assert 'home/world' in SCOUT_READS
    assert not is_write('home/world',{})
