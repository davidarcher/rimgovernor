from types import SimpleNamespace
from unittest.mock import AsyncMock,Mock
import pytest
from rimbot.colony_plan import ColonyPlan,Placement,StepProgress
from rimbot.hands import Hands


@pytest.mark.asyncio
async def test_native_legal_blueprint_can_be_queued_with_forbidden_materials():
    rt=SimpleNamespace(current_plan=ColonyPlan(),context_token='load',chat_revision=0,
        handled_revision=0,mode='automate',persist=Mock(),
        game=SimpleNamespace(query=AsyncMock(side_effect=[{'buildings':[]},{'zones':[]} ])),
        inspect_native=AsyncMock(return_value={'canPlace':True,'madeFromStuff':True,
            'materials':{'canBuildNow':False,'missing':'5 wood, all forbidden'},
            'rotations':[{'rotation':'north','occupiedCells':[{'x':10,'z':10}]}]}),
        native=AsyncMock(return_value={'receipt':{'outcome':'placed'}}))
    result=await Hands().place(rt,Placement(x=10,z=10,def_name='Wall',materials=['WoodLog']),
        StepProgress(),'0',0,'load',0)
    assert result['outcome']=='placed'
    assert rt.native.await_args.args[1]['dryRun'] is False
