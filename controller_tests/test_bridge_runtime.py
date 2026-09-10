import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.store import Store


@pytest.mark.asyncio
async def test_direction_during_dialog_pause_prevents_open(tmp_path):
    store = Store(tmp_path/'dialog-direction.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.mode = 'automate'
    rt.sync_identity = AsyncMock()
    rt.game = SimpleNamespace(invoke=AsyncMock(return_value={'success': True, 'windows': []}))
    rt.supervisor = SimpleNamespace(pause_for_dialog=AsyncMock(side_effect=lambda: None))
    async def pause():
        await rt.steer('Stop opening dialogs')
    rt.supervisor.pause_for_dialog.side_effect = pause
    try:
        with pytest.raises(ValueError, match='New player direction'):
            await rt.native('rimworld/open_letter', {'letterId': 'Letter1'}, expected_revision=0)
        rt.game.invoke.assert_awaited_once_with('rimworld/get_ui_state', {})
    finally:
        store.close()

@pytest.mark.asyncio
async def test_direction_rechecked_after_lock(tmp_path):
    store = Store(tmp_path/'test.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.game = SimpleNamespace(invoke=AsyncMock())
    await rt.lock.acquire()
    pending = asyncio.create_task(rt.native('home/order', {}, expected_revision=0))
    await asyncio.sleep(0)
    await rt.steer('Stop and reconsider')
    rt.lock.release()
    with pytest.raises(ValueError, match='New player direction'):
        await pending
    rt.game.invoke.assert_not_awaited()
    store.close()

@pytest.mark.asyncio
async def test_failed_review_does_not_retry_forever(tmp_path):
    store = Store(tmp_path/'test.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.game = SimpleNamespace(query=AsyncMock(side_effect=RuntimeError('offline')))
    rt.chat_revision = 1
    await rt.review()
    assert rt.mode == 'manual'
    assert rt.handled_revision == 1
    assert not rt.wake.is_set()
    store.close()

@pytest.mark.asyncio
async def test_cleanup_preserves_player_drafts_and_retains_failures(tmp_path):
    store = Store(tmp_path/'drafts.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.context_token = 'load-a'
    rt.draft_owners = {'AI1': 'load-a', 'AI2': 'load-a'}
    async def invoke(name, args, **kwargs):
        if args['pawn'] == 'AI2':
            raise RuntimeError('unreachable bridge')
        return {'pawn': {'thingId': 'AI1', 'drafted': False}}
    rt.game = SimpleNamespace(invoke=AsyncMock(side_effect=invoke))
    await rt.release_drafts()
    assert rt.draft_owners == {'AI2': 'load-a'}
    assert all(c.args[1]['pawn'] != 'Player' for c in rt.game.invoke.await_args_list)
    assert store.get('bridge:'+rt.colony)['draft_owners'] == {'AI2': 'load-a'}
    store.close()

@pytest.mark.asyncio
async def test_halt_pauses_and_releases_even_if_pause_fails(tmp_path):
    store = Store(tmp_path/'halt.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.mode, rt.resume_after_review = 'automate', True
    rt.bridge = SimpleNamespace(call=AsyncMock(side_effect=RuntimeError('lost reply')))
    rt.release_drafts = AsyncMock()
    await rt.halt()
    assert rt.mode == 'manual' and not rt.resume_after_review
    rt.release_drafts.assert_awaited_once()
    assert rt.bridge.call.await_args.kwargs['speed'] == 'Paused'
    store.close()

@pytest.mark.asyncio
async def test_draft_intent_committed_before_uncertain_write(tmp_path):
    store = Store(tmp_path/'intent.sqlite')
    rt = BridgeRuntime(store, tmp_path, model_factory=lambda _: SimpleNamespace())
    rt.mode, rt.context_token = 'automate', 'load-a'
    rt.sync_identity = AsyncMock(return_value=False)
    async def invoke(name, args, **kwargs):
        if args['action'] == 'resolve':
            return {'pawn': {'thingId': 'Pawn1', 'drafted': False}}
        assert store.get('bridge:'+rt.colony)['draft_owners'] == {'Pawn1': 'load-a'}
        raise RuntimeError('write receipt lost')
    rt.game = SimpleNamespace(invoke=AsyncMock(side_effect=invoke))
    with pytest.raises(RuntimeError, match='receipt lost'):
        await rt.native('home/order', {'action': 'draft', 'pawn': 'Pawn1', 'dryRun': False})
    assert rt.draft_owners == {'Pawn1': 'load-a'}
    store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('drafted,explicit,expected', [(False,None,True),(False,False,False),(True,None,None)])
async def test_tend_cleanup_handshake_only_for_owned_drafts(tmp_path,drafted,explicit,expected):
    store=Store(tmp_path/'tend.sqlite')
    rt=BridgeRuntime(store,tmp_path,model_factory=lambda _:SimpleNamespace())
    rt.mode,rt.context_token='automate','load-a'
    rt.sync_identity=AsyncMock(return_value=False)
    async def invoke(name,args,**kwargs):
        if args['action']=='resolve':
            return {'pawn':{'thingId':'Doctor','drafted':drafted}}
        assert args.get('allowPersistentDraft') is expected
        if not drafted:
            assert store.get('bridge:'+rt.colony)['draft_owners']=={'Doctor':'load-a'}
        else:
            assert not rt.draft_owners
        raise RuntimeError('receipt lost')
    rt.game=SimpleNamespace(invoke=AsyncMock(side_effect=invoke))
    args={'action':'tend','pawn':'Doctor','target':'Patient','dryRun':False}
    if explicit is not None:args['allowPersistentDraft']=explicit
    original=dict(args)
    with pytest.raises(RuntimeError,match='receipt lost'):
        await rt.native('home/order',args)
    assert args==original
    assert rt.draft_owners==({} if drafted else {'Doctor':'load-a'})
    store.close()
