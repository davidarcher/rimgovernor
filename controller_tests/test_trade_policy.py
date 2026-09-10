from copy import deepcopy
from unittest.mock import AsyncMock

import pytest

from rimbot.colony_plan import ColonyPlan, TradeAction
from rimbot.trade_policy import economic_reserves, select_trade
from rimbot.trading import execute_trade


def fixture():
    action = TradeAction(trader_id='trader', negotiator='pawn', max_silver_spend=40,
        policy={'silver_reserve': 60, 'targets': [
            {'item': 'Steel', 'stock': 50, 'max_buy': 20, 'max_buy_price': 3},
            {'item': 'WoodLog', 'stock': 80, 'max_sell': 30, 'min_sell_price': 1}]})
    sheet = dict(sessionId='session', sessionActive=True, giftMode=False, traderId='trader', negotiator='pawn',
        omittedByRowCap=0, omittedByFilter=0, balance={'colonySilverNow': 100, 'traderSilverNow': 15}, rows=[
            dict(defName='Steel', colonyCount=30, traderCount=100, buyPrice=2, sellPrice=1,
                 traderWillTrade=True, isPawn=False, isCurrency=False, protectedExport=False),
            dict(defName='WoodLog', colonyCount=120, traderCount=20, buyPrice=2, sellPrice=1,
                 traderWillTrade=True, isPawn=False, isCurrency=False, protectedExport=False)])
    return action, sheet


def test_demand_affordability_reserves_and_buyer_capacity():
    action, sheet = fixture()
    lines, evidence = select_trade(action, sheet, {'Steel': 60, 'WoodLog': 110})
    assert [(l.item, l.count) for l in lines] == [('Steel', 20), ('WoodLog', -10)]
    assert evidence[-1]['export_capacity'] == 10
    # Promised export revenue cannot finance the purchase.
    sheet['balance']['colonySilverNow'] = 60
    lines, _ = select_trade(action, sheet)
    assert [(l.item, l.count) for l in lines] == [('WoodLog', -15)]


@pytest.mark.parametrize('field,value', [('protectedExport', True), ('protectedExport', None),
    ('isPawn', True), ('isCurrency', True), ('traderWillTrade', False)])
def test_protected_exports_never_selected(field, value):
    action, sheet = fixture()
    sheet['rows'][1][field] = value
    assert all(l.count > 0 for l in select_trade(action, sheet)[0])


def test_stops_prices_ambiguous_stock_and_maintained_targets():
    action, sheet = fixture()
    assert select_trade(action, sheet, stopped=['Silver', 'WoodLog'])[0] == []
    sheet['rows'][0]['buyPrice'] = 4
    sheet['rows'][1]['sellPrice'] = .5
    assert select_trade(action, sheet)[0] == []
    action, sheet = fixture()
    sheet['rows'] += deepcopy(sheet['rows'])
    assert select_trade(action, sheet)[0] == []
    assert select_trade(*fixture(), floors={'WoodLog': 120, 'Silver': 100})[0] == []


@pytest.mark.parametrize('value', [None, True, -1, float('nan'), float('inf')])
def test_unknown_economics_fail_closed(value):
    action, sheet = fixture()
    sheet['balance']['colonySilverNow'] = value
    with pytest.raises(ValueError):
        select_trade(action, sheet)


def test_partial_inventory_refused():
    action, sheet = fixture()
    sheet['omittedByRowCap'] = 1
    with pytest.raises(ValueError, match='complete'):
        select_trade(action, sheet)


def test_shared_native_commitments_and_maintained_reserves():
    plan = ColonyPlan.model_validate({'control': {'resource_policy': {
        'WoodLog': {'reserve': 25}, 'Steel': {'spending': 'stop'}}},
        'colony_goals': {'MaintainResource-WoodLog': {'priority_class': 3, 'target': {'resource': 'WoodLog', 'quantity': 80}}}})
    floors, stopped = economic_reserves(plan, {'resourceDeficit': [{'defName': 'WoodLog', 'stillNeeded': 70}]})
    assert floors['WoodLog'] == 150 and stopped == ['Steel']
    with pytest.raises(ValueError, match='unknown'):
        economic_reserves(plan, {})


@pytest.mark.asyncio
async def test_policy_exchange_rechecks_prices_and_never_accepts_changed_selection():
    action, sheet = fixture()
    changed = deepcopy(sheet)
    changed['rows'][0]['buyPrice'] = 5
    write = AsyncMock(side_effect=[sheet,
        {'linesApplied': 1, 'linesRejected': 0, 'staged': [1]},
        {'linesApplied': 1, 'linesRejected': 0, 'staged': [1]}])
    with pytest.raises(ValueError, match='changed'):
        await execute_trade(action, AsyncMock(side_effect=[{'sessionActive': False}, changed]), write)
    assert [c.args[0]['action'] for c in write.call_args_list] == ['open', 'set', 'set']


@pytest.mark.asyncio
@pytest.mark.parametrize('confirmed', [False, True])
async def test_empty_policy_cancels_only_owned_session_without_accepting_quest(confirmed):
    action, sheet = fixture()
    sheet['rows'] = []
    write = AsyncMock(side_effect=[sheet, {'sessionActive': False} if confirmed else {}])
    if confirmed:
        result = await execute_trade(action, AsyncMock(return_value={'sessionActive': False}), write)
        assert result['moved'] == []
    else:
        with pytest.raises(ValueError, match='unconfirmed'):
            await execute_trade(action, AsyncMock(return_value={'sessionActive': False}), write)
    assert write.call_args_list[-1].args[0] == {'action': 'cancel', 'sessionId': 'session', 'receiveQuest': False}


@pytest.mark.asyncio
@pytest.mark.parametrize('changed_quantity', [False, True])
async def test_selected_quantities_and_native_atomic_floors(changed_quantity):
    action, sheet = fixture()
    rows = [dict(defName='Steel', count=20, isCurrency=False),
            dict(defName='WoodLog', count=-16 if changed_quantity else -15, isCurrency=False),
            dict(defName='Silver', count=-25, isCurrency=True)]
    preview = dict(sheet, staged=rows, wouldSucceed=True, dealSignature='signature',
        balance=dict(sheet['balance'], netSilverToColony=-25, colonyCanAfford=True, traderHasEnoughSilver=True))
    write = AsyncMock(side_effect=[sheet,
        {'linesApplied': 1, 'linesRejected': 0, 'staged': rows},
        {'linesApplied': 1, 'linesRejected': 0, 'staged': rows},
        {'executed': True, 'actuallyTraded': True, 'moved': rows}])
    read = AsyncMock(side_effect=[{'sessionActive': False}, sheet, preview])
    if changed_quantity:
        with pytest.raises(ValueError, match='quantities'):
            await execute_trade(action, read, write)
        assert write.await_count == 3
    else:
        result = await execute_trade(action, read, write)
        assert result['net_silver'] == -25
        assert write.call_args_list[-1].args[0]['economicFloors'] == 'Silver=60;Steel=50;WoodLog=80'


@pytest.mark.asyncio
async def test_economic_command_enters_shared_plan_without_native_writes(tmp_path):
    from test_strategic_architecture import runtime, batch
    from rimbot.player_commands import apply_command
    rt = runtime(tmp_path)
    try:
        await rt.sync_identity()
        rt.batch = batch()
        rt.inspect_native = AsyncMock(return_value={'traders': [{'id': 'trader', 'canTradeNow': True}]})
        query = rt.game.query
        async def observed(name, **args):
            if name == 'home/list_pawns':
                return {'pawns': [{'thingId': 'pawn', 'name': 'Negotiator'}]}
            return await query(name, **args)
        rt.game.query = observed
        action, _ = fixture()
        result = await apply_command(rt, {'kind': 'TradeEconomy', 'trader_id': 'trader',
            'negotiator': 'Negotiator', 'policy': action.policy.model_dump(), 'max_silver_spend': 40},
            token=rt.context_token, revision=rt.chat_revision)
        step = next(s for s in rt.current_plan.spec.steps if s.id == result['step'])
        assert step.source == 'PLAYER' and step.action.policy == action.policy
        assert step.action.negotiator == 'pawn' and not rt.current_plan.progress[step.id].issued
    finally:
        rt.store.close()
