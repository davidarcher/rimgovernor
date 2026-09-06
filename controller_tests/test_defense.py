from unittest.mock import AsyncMock
from rimbot.contracts import Action
from rimbot.catalog import Catalog
from rimbot.routine import is_routine

async def test_fight_reads_policy_not_order_receipt(colony):
    rt,_=colony
    action=Action(title='Defend yourself',endpoint='post_pawn_edit_status',arguments={'pawn_id':11,'hostility_response':'Attack'})
    rt.api.call=AsyncMock(return_value={'policies_info':{'hostility_response':2}})
    assert not await rt.action_complete(action)
    rt.api.call.return_value={'policies_info':{'hostility_response':1}}
    assert await rt.action_complete(action)
    assert is_routine(action)
    action.arguments['is_drafted']=True
    assert not is_routine(action)

async def test_equip_requires_actual_equipment(colony):
    rt,_=colony
    action=Action(title='Equip weapon',endpoint='post_jobs_make_equip',arguments={'map_id':7,'pawn_id':11,'item_id':101})
    rt.api.call=AsyncMock(return_value={'items':[{'thing_id':101}],'equipment':[],'apparels':[]})
    assert not await rt.action_complete(action)
    rt.api.call.return_value={'equipment':[{'thing_id':101}],'apparels':[]}
    assert await rt.action_complete(action)

def test_fight_schema_does_not_require_drafting():
    c=Catalog();c.discovered=True;c.available.add("post_pawn_edit_status")
    c.validate('post_pawn_edit_status',{'pawn_id':11,'hostility_response':'Attack'},True)
