from unittest.mock import AsyncMock
import pytest
from pydantic import ValidationError
from rimbot.colony_plan import TradeAction
from rimbot.trading import execute_trade


def action():
    return TradeAction(trader_id='trader',negotiator='Thing_Pawn',lines=[{'item':'WoodLog','count':10}],max_silver_spend=20)


def setup():
    rows=[{'defName':'WoodLog','count':10,'isCurrency':False},{'defName':'Silver','count':-15,'isCurrency':True}]
    preview={'traderId':'trader','negotiator':'Pawn','giftMode':False,'staged':rows,'wouldSucceed':True,
        'balance':{'netSilverToColony':-15,'colonyCanAfford':True,'traderHasEnoughSilver':True}}
    receipts=[{'sessionActive':True,'giftMode':False,'traderId':'trader','negotiator':'Pawn'},
        {'linesApplied':1,'linesRejected':0,'staged':rows},
        {'executed':True,'actuallyTraded':True,'moved':rows}]
    return preview,receipts


@pytest.mark.asyncio
async def test_trade_stages_and_accepts_once_with_normal_adjacency():
    preview,receipts=setup()
    read=AsyncMock(side_effect=[{'sessionActive':False},preview]);write=AsyncMock(side_effect=receipts)
    result=await execute_trade(action(),read,write)
    assert result['net_silver']==-15
    assert [c.args[0]['action'] for c in write.call_args_list]==['open','set','accept']
    assert write.call_args_list[0].args[0]['requireAdjacent'] is True


@pytest.mark.asyncio
@pytest.mark.parametrize('failure',['budget','trader_funds','staging','participant','gift','missing_balance'])
async def test_bad_preview_never_accepts(failure):
    preview,receipts=setup()
    if failure=='budget':preview['balance']['netSilverToColony']=-21
    if failure=='trader_funds':preview['balance']['traderHasEnoughSilver']=False
    if failure=='staging':preview['staged']=[]
    if failure=='participant':preview['traderId']='other'
    if failure=='gift':preview['giftMode']=True
    if failure=='missing_balance':preview['balance']={}
    write=AsyncMock(side_effect=receipts)
    with pytest.raises(ValueError):
        await execute_trade(action(),AsyncMock(side_effect=[{'sessionActive':False},preview]),write)
    assert all(c.args[0]['action']!='accept' for c in write.call_args_list)


@pytest.mark.asyncio
async def test_existing_session_is_not_replaced():
    write=AsyncMock()
    with pytest.raises(ValueError,match='active'):
        await execute_trade(action(),AsyncMock(return_value={'sessionActive':True}),write)
    write.assert_not_awaited()


@pytest.mark.asyncio
async def test_lost_accept_receipt_is_not_retried():
    preview,receipts=setup();receipts[-1]=RuntimeError('lost reply')
    write=AsyncMock(side_effect=receipts)
    with pytest.raises(RuntimeError):
        await execute_trade(action(),AsyncMock(side_effect=[{'sessionActive':False},preview]),write)
    assert sum(c.args[0]['action']=='accept' for c in write.call_args_list)==1


def test_duplicate_names_and_stale_row_indices_refused():
    for lines in ([{'item':'#1','count':1}],[{'item':'WoodLog','count':1},{'item':'woodlog','count':2}]):
        with pytest.raises(ValidationError):
            TradeAction(trader_id='trader',negotiator='pawn',lines=lines,max_silver_spend=10)
