import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store

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
