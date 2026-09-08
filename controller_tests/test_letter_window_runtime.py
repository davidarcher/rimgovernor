from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store


@pytest.mark.asyncio
@pytest.mark.parametrize('windows', [None, [{'id': 1, 'type': 'ExternalDialog'}]])
async def test_existing_or_unknown_windows_prevent_letter_and_clock_changes(tmp_path, windows):
    store=Store(tmp_path/'existing.sqlite')
    rt=BridgeRuntime(store,tmp_path,model_factory=lambda _:SimpleNamespace())
    rt.mode='automate'
    rt.resume_after_review=True
    rt.sync_identity=AsyncMock()
    rt.supervisor=SimpleNamespace(pause_for_dialog=AsyncMock(),hold='external_pause')
    rt.game=SimpleNamespace(invoke=AsyncMock(return_value={'success':True,'windows':windows}))
    try:
        with pytest.raises(ValueError,match='window'):
            await rt.native('rimworld/open_letter',{'letterId':'Letter1'})
        rt.game.invoke.assert_awaited_once_with('rimworld/get_ui_state',{})
        rt.supervisor.pause_for_dialog.assert_not_awaited()
        assert rt.supervisor.hold=='external_pause'
        assert not rt.resume_after_review
    finally:
        store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('after', [[], [{'id': 1, 'type': 'LetterDialog'}]])
async def test_letter_requires_fresh_window_after_successful_receipt(tmp_path, after):
    store=Store(tmp_path/'opening.sqlite')
    rt=BridgeRuntime(store,tmp_path,model_factory=lambda _:SimpleNamespace())
    rt.mode='automate'
    rt.resume_after_review=True
    rt.sync_identity=AsyncMock()
    rt.supervisor=SimpleNamespace(pause_for_dialog=AsyncMock())
    rt.game=SimpleNamespace(invoke=AsyncMock(side_effect=[
        {'success':True,'windows':[]}, {'success':True}, {'success':True,'windows':after}]))
    try:
        if after:
            result=await rt.native('rimworld/open_letter',{'letterId':'Letter1'},reconcile=False)
            assert result['observed_after']['windows']==after
        else:
            with pytest.raises(ValueError,match='opening is unverified'):
                await rt.native('rimworld/open_letter',{'letterId':'Letter1'},reconcile=False)
        assert [call.args[0] for call in rt.game.invoke.await_args_list]==[
            'rimworld/get_ui_state','rimworld/open_letter','rimworld/get_ui_state']
        rt.supervisor.pause_for_dialog.assert_awaited_once()
        assert not rt.resume_after_review
    finally:
        store.close()
