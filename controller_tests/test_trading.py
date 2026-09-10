from unittest.mock import AsyncMock, Mock
from types import SimpleNamespace
import pytest
from pydantic import ValidationError
from rimgovernor.colony_plan import TradeAction
from rimgovernor.trading import execute_trade
from rimgovernor.colony_plan import ColonyPlan, PlanSpec, StepProgress
from rimgovernor.hands import Hands


def action():
    return TradeAction(trader_id='trader',negotiator='Thing_Pawn',lines=[{'item':'WoodLog','count':10}],max_silver_spend=20)


def setup():
    rows=[{'defName':'WoodLog','count':10,'isCurrency':False},{'defName':'Silver','count':-15,'isCurrency':True}]
    preview={'sessionId':'session','dealSignature':'exact-preview','traderId':'trader','negotiator':'Pawn','giftMode':False,'staged':rows,'wouldSucceed':True,
        'balance':{'netSilverToColony':-15,'colonyCanAfford':True,'traderHasEnoughSilver':True}}
    receipts=[{'sessionId':'session','sessionActive':True,'giftMode':False,'traderId':'trader','negotiator':'Pawn'},
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
    assert write.call_args_list[-1].args[0]['sessionId'] == 'session'
    assert write.call_args_list[-1].args[0]['dealSignature'] == 'exact-preview'


@pytest.mark.asyncio
@pytest.mark.parametrize('failure',['budget','trader_funds','colony_funds','staging','participant','gift','missing_balance','session','signature','nan','infinity'])
async def test_bad_preview_never_accepts(failure):
    preview,receipts=setup()
    if failure=='budget':preview['balance']['netSilverToColony']=-21
    if failure=='trader_funds':preview['balance']['traderHasEnoughSilver']=False
    if failure=='staging':preview['staged']=[]
    if failure=='participant':preview['traderId']='other'
    if failure=='gift':preview['giftMode']=True
    if failure=='missing_balance':preview['balance']={}
    if failure=='colony_funds':preview['balance']['colonyCanAfford']=False
    if failure=='session':preview['sessionId']='replacement'
    if failure=='signature':preview.pop('dealSignature')
    if failure=='nan':preview['balance']['netSilverToColony']=float('nan')
    if failure=='infinity':preview['balance']['netSilverToColony']=float('inf')
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


@pytest.mark.asyncio
async def test_lost_accept_receipt_survives_restore_without_replaying():
    preview, receipts = setup()
    receipts[-1] = RuntimeError('Native acceptance completed; reply lost')
    plan = ColonyPlan(spec=PlanSpec(steps=[dict(id='trade', title='Trade',
        completion_criteria='Native exchange', action=action().model_dump())]),
        progress={'trade': StepProgress()})
    persisted = []
    async def native(tool, args, **kwargs):
        assert persisted[-1]['progress']['trade']['issued']['0']['confirmed'] is False
        value = receipts.pop(0)
        if isinstance(value, Exception):
            raise value
        return {'receipt': value}
    rt = SimpleNamespace(current_plan=plan, mode='automate', context_token='load',
        chat_revision=0, handled_revision=0, inspect_native=AsyncMock(side_effect=[{'sessionActive':False},preview]),
        native=AsyncMock(side_effect=native), note=Mock(), signal=Mock())
    rt.persist = lambda: persisted.append(rt.current_plan.model_dump(mode='json'))
    await Hands().advance(rt)
    assert rt.current_plan.progress['trade'].state == 'blocked'
    assert rt.native.await_count == 3
    rt.current_plan = ColonyPlan.model_validate(persisted[-1])
    # Even an explicit retry/reset of the state cannot erase the durable uncertain write.
    rt.current_plan.progress['trade'].state = 'pending'
    await Hands().advance(rt)
    assert rt.current_plan.progress['trade'].failure.code == 'uncertain_write'
    assert rt.native.await_count == 3
