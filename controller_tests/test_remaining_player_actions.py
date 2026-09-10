from copy import deepcopy
from unittest.mock import AsyncMock
from types import SimpleNamespace
import pytest
from rimbot.player_commands import COMMAND, apply_command
from rimbot.player_action_verification import verify_zone_edit, selection_identity, verify_bill_whitelist, verify_main_tab_closed
from test_strategic_architecture import runtime, batch


@pytest.mark.parametrize('payload', [
    dict(kind='EditZone', zone_id=1, operation='delete', cells=[dict(x=1,z=2)]),
    dict(kind='EditZone', zone_id=1, operation='crop'),
    dict(kind='EditZone', zone_id=1, operation='filter', allow=['']),
    dict(kind='CreateBill', bench='CraftingSpot1', recipe='X', target_count=1, ingredients=[]),
    dict(kind='CreateBill', bench='CraftingSpot1', recipe='X', target_count=1, ingredients=['WoodLog,Steel']),
])
def test_invalid_player_settings_refuse_before_admission(payload):
    with pytest.raises(ValueError): COMMAND.validate_python(payload)


@pytest.mark.asyncio
@pytest.mark.parametrize('payload,expected', [
    (dict(kind='EditZone',zone_id=7,operation='filter',disallow=['special:AllowRotten']),
     dict(op='filter',zone='7',disallow='special:AllowRotten',watch=False,dryRun=False)),
    (dict(kind='CreateBill',bench='CraftingSpot1',recipe='Make_WarMask',target_count=2,ingredients=['WoodLog']),
     dict(action='add',bench='CraftingSpot1',recipe='Make_WarMask',repeatMode='TargetCount',targetCount=2,
          unpauseWhenYouHave=1,pauseWhenSatisfied='on',watch=False,dryRun=False,only='WoodLog')),
])
async def test_player_settings_use_shared_manual_queue_and_preview(tmp_path, payload, expected):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    rt.game.describe=AsyncMock(return_value={'type':'object'})
    rt.inspect_native=AsyncMock(return_value={'success':True,'write':{'refused':False}})
    result=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
    step=next(s for s in rt.current_plan.spec.steps if s.id==result['step'])
    assert step.action.arguments==expected and step.source=='PLAYER'
    assert rt.manual_requests==[(step.id,rt.context_token,rt.chat_revision)]
    assert rt.inspect_native.await_args.args[1]['dryRun'] is True
    rt.store.close()


@pytest.mark.asyncio
async def test_partial_zone_preview_preserves_plan(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch()
    rt.inspect_native=AsyncMock(return_value={'success':True,'cells':[{'accepted':False}]})
    before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError,match='refused'):
        await apply_command(rt,dict(kind='EditZone',zone_id=7,operation='add',cells=[dict(x=1,z=2)]),
            token=rt.context_token,revision=rt.chat_revision)
    assert rt.current_plan.model_dump()==before
    rt.store.close()


@pytest.mark.asyncio
async def test_bill_semantic_refusal_is_not_transport_success(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch()
    rt.inspect_native=AsyncMock(return_value={'success':True,'write':{'refused':True,'reason':'Illegal ingredient'}})
    before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError,match='whitelist refused'):
        await apply_command(rt,dict(kind='CreateBill',bench='CraftingSpot1',recipe='Make_WarMask',target_count=1,ingredients=['Steel']),
            token=rt.context_token,revision=rt.chat_revision)
    assert rt.current_plan.model_dump()==before
    rt.store.close()


def test_bill_readback_rejects_replacement_at_same_index_and_changed_filter():
    bill={'billId':'Bill1','index':0,'filter':{'allowedDefNames':['WoodLog']}}
    receipt={'applied':True,'write':{'refused':False,'after':{'bill':bill}}}
    observed={'success':True,'benches':[{'bills':[deepcopy(bill)]}]}
    verify_bill_whitelist(receipt,observed)
    observed['benches'][0]['bills'][0]['billId']='Bill2'
    with pytest.raises(ValueError): verify_bill_whitelist(receipt,observed)
    observed['benches'][0]['bills'][0]['billId']='Bill1'
    observed['benches'][0]['bills'][0]['filter']['allowedDefNames']=['Steel']
    with pytest.raises(ValueError): verify_bill_whitelist(receipt,observed)


def test_main_tab_closure_allows_native_inspect_fallback():
    verify_main_tab_closed({'mainTabId':'Work'},{'mainTabOpen':True,'openMainTabId':'main-tab:Inspect'})
    verify_main_tab_closed({'mainTabId':'Work'},{'mainTabOpen':False,'openMainTabId':None})
    with pytest.raises(ValueError):
        verify_main_tab_closed({'mainTabId':'Work'},{'mainTabOpen':True,'openMainTabId':'main-tab:Work'})
    with pytest.raises(ValueError): verify_main_tab_closed({'mainTabId':'Work'},{'mainTabOpen':True})


def test_zone_readback_requires_actual_geometry_and_filter():
    receipt=dict(success=True,zone={'id':7},cells=[dict(x=1,z=2,accepted=True)])
    row=dict(id=7,gridCellCount=1,gridCells=[dict(x=1,z=2)])
    observed=dict(success=True,zones=[row])
    verify_zone_edit({'op':'add'},receipt,observed)
    row['gridCells']=[dict(x=2,z=2)]
    with pytest.raises(ValueError,match='geometry'): verify_zone_edit({'op':'add'},receipt,observed)
    row['gridCellsNotListed']=1
    with pytest.raises(ValueError,match='incomplete'): verify_zone_edit({'op':'add'},receipt,observed)
    with pytest.raises(ValueError,match='still exists'): verify_zone_edit({'op':'delete'},receipt,observed)
    receipt['after']={'priority':'Critical'};row['filter']={'priority':'Normal'}
    with pytest.raises(ValueError,match='filter'): verify_zone_edit({'op':'filter'},receipt,observed)


def test_selection_requires_complete_native_identity():
    assert selection_identity({'success':True,'selectedCount':1,'selectedObjects':[{'id':'Pawn1'}]})==['Pawn1']
    with pytest.raises(ValueError): selection_identity({'selectedCount':2,'selectedObjects':[{'id':'Pawn1'}]})
    with pytest.raises(ValueError): selection_identity({'selectedCount':1,'selectedObjects':[{}]})


@pytest.mark.asyncio
async def test_changed_selection_invalidates_cached_ui_without_click(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.headless=False
    rt.ui_targets={'control':dict(load_token=rt.context_token,actionable=True,disabled=False,
        direction_revision=rt.chat_revision,selection_identity=['Pawn1'])}
    rt.game.invoke=AsyncMock(return_value={'success':True,'selectedCount':1,'selectedObjects':[{'id':'Pawn2'}]})
    rt.bridge=SimpleNamespace(call=AsyncMock())
    with pytest.raises(ValueError,match='selection changed'):
        await rt.native('rimworld/click_ui_target',{'targetId':'control'})
    assert not rt.ui_targets
    assert all(call.args[0]!='rimworld/click_ui_target' for call in rt.game.invoke.await_args_list)
    rt.bridge.call.assert_not_awaited()
    rt.store.close()
