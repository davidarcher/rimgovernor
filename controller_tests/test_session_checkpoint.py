import asyncio
import json
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
import pytest
from rimbot.store import Store
from rimbot.session_checkpoint import create_checkpoint, read_checkpoint, prepare_resume, install_saved_game, stop_for_restart
from rimbot.session_checkpoint import list_checkpoints, delete_checkpoint


def fixture(tmp_path):
    profile=tmp_path/'profile'
    (profile/'Saves').mkdir(parents=True)
    (profile/'Config').mkdir()
    (profile/'Config/Prefs.xml').write_text('<Prefs><pauseOnLoad>False</pauseOnLoad></Prefs>')
    (tmp_path/'config').mkdir()
    (tmp_path/'config/config.json').write_text(json.dumps({'games':{'rimbot-trial':{
        'launchMode':'DirectPath','args':['-savedatafolder='+str(profile)]}}}))
    store=Store(tmp_path/'original.sqlite')
    store.set('goal',{'source':'PLAYER','food_days':20})
    rt=SimpleNamespace(lock=asyncio.Lock(),connected=True,fresh=True,root=tmp_path,headless=False,
        sync_identity=AsyncMock(),context_token='colony:map:load',identity={'colonyId':'colony','mapId':7},
        chat_revision=1,handled_revision=1,wake=asyncio.Event(),draft_owners={},halt=AsyncMock(),
        store=store,persist=Mock(),note=Mock(),mode='manual',shutdown=asyncio.Event(),game=SimpleNamespace(query=AsyncMock(return_value={'time':{'ticksGame':500,'paused':True}})))
    async def save(name,**args):
        (profile/'Saves'/(args['saveName']+'.rws')).write_text('<savegame><game>native</game></savegame>')
        return {'success':True}
    rt.bridge=SimpleNamespace(call=AsyncMock(side_effect=save),core=AsyncMock(),game_id='rimbot-trial')
    return rt


@pytest.mark.asyncio
async def test_explicit_deletion_preserves_other_pairs_and_native_saves(tmp_path):
    rt = fixture(tmp_path)
    first = Path((await create_checkpoint(rt, rt.context_token))['manifest_path'])
    second = Path((await create_checkpoint(rt, rt.context_token))['manifest_path'])
    assert len(list_checkpoints(tmp_path)) == 2
    native = tmp_path/'profile/Saves'/(first.parent.name+'.rws')
    assert (await delete_checkpoint(rt, rt.context_token, first))['deleted']
    assert native.is_file() and not first.parent.exists()
    assert len(list_checkpoints(tmp_path)) == 1
    assert read_checkpoint(second)['tick'] == 500
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('change', ['session', 'resume', 'closing', 'extra', 'tampered'])
async def test_checkpoint_delete_refusals_preserve_pair(tmp_path, change):
    rt = fixture(tmp_path)
    path = Path((await create_checkpoint(rt, rt.context_token))['manifest_path'])
    token = rt.context_token
    if change == 'session': token = 'old'
    elif change == 'resume': rt.resume = path
    elif change == 'closing': rt.session_closing = True
    elif change == 'extra': (path.parent/'unexpected').write_text('preserve')
    elif change == 'tampered': (path.parent/'game.rws').write_text('changed')
    with pytest.raises(ValueError): await delete_checkpoint(rt, token, path)
    assert path.exists() and (path.parent/'bridge.sqlite').is_file()
    rt.store.close()


@pytest.mark.asyncio
async def test_paired_checkpoint_restores_exact_state_into_a_new_database(tmp_path):
    rt=fixture(tmp_path)
    result=await create_checkpoint(rt,rt.context_token)
    manifest=Path(result['manifest_path'])
    assert read_checkpoint(manifest)['tick']==500
    rt.store.set('goal',{'food_days':1})
    data,state=prepare_resume(manifest)
    restored=Store(state/'bridge.sqlite')
    assert restored.get('goal')=={'source':'PLAYER','food_days':20}
    assert data['root']==str(tmp_path)
    restored.set('goal',{})
    read_checkpoint(manifest) # Restored writes never alter the immutable snapshot.
    install_saved_game(manifest)
    assert (tmp_path/'profile/Saves'/(data['save_name']+'.rws')).read_bytes()==(manifest.parent/'game.rws').read_bytes()
    restored.close();rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('failure',['stale','unowned','pause','drafts','save','clock_change','load_change','partial','new_chat'])
async def test_failed_save_never_publishes_a_restart_manifest(tmp_path,failure):
    rt=fixture(tmp_path)
    session=rt.context_token
    if failure=='stale': session='old-load'
    elif failure=='unowned':
        (tmp_path/'config/config.json').write_text(json.dumps({'games':{'rimbot-trial':{'launchMode':'SteamAppId'}}}))
    elif failure=='pause': rt.game.query.return_value={'time':{'ticksGame':500,'paused':False}}
    elif failure=='drafts': rt.draft_owners={'doctor':'load'}
    elif failure=='save': rt.bridge.call.side_effect=ValueError('Native save refused')
    elif failure=='clock_change': rt.game.query.side_effect=[{'time':{'ticksGame':500,'paused':True}},{'time':{'ticksGame':501,'paused':True}}]
    elif failure=='load_change':
        async def identity():
            if rt.bridge.call.await_count: rt.context_token='different'
        rt.sync_identity.side_effect=identity
    elif failure=='new_chat':
        async def direction():
            if rt.bridge.call.await_count: rt.chat_revision+=1
        rt.sync_identity.side_effect=direction
    elif failure=='partial':
        async def partial(name,**args):
            (tmp_path/'profile/Saves'/(args['saveName']+'.rws')).write_text('<savegame>')
            return {'success':True}
        rt.bridge.call.side_effect=partial
    with pytest.raises(Exception): await create_checkpoint(rt,session)
    assert not list(tmp_path.glob('checkpoints/*/checkpoint.json'))
    rt.store.close()


@pytest.mark.asyncio
async def test_endpoint_requires_local_header_and_current_colony(tmp_path):
    import httpx
    from rimbot.bridge_server import create_app
    rt=fixture(tmp_path)
    app=create_app(rt);app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver') as client:
        assert (await client.post('/api/session/checkpoint',json={'session_id':rt.context_token})).status_code==403
        assert (await client.post('/api/session/checkpoint',headers={'X-RimBot':'1'},json={'session_id':'old'})).status_code==400
        assert (await client.post('/api/session/checkpoint',headers={'X-RimBot':'1'},json={'session_id':rt.context_token})).status_code==200
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('change',[None,'chat','clock','mode','pending_chat'])
async def test_restart_never_stops_a_session_changed_since_its_checkpoint(tmp_path,change):
    rt=fixture(tmp_path)
    if change=='pending_chat':
        rt.chat=[{'kind':'human','revision':rt.handled_revision+1}]
        with pytest.raises(ValueError,match='chat request'): await create_checkpoint(rt,rt.context_token)
    else:
        checkpoint=await create_checkpoint(rt,rt.context_token)
        if change=='chat': rt.chat_revision+=1
        elif change=='clock': rt.game.query.return_value={'time':{'ticksGame':501,'paused':True}}
        elif change=='mode': rt.mode='automate'
        if change is None:
            await stop_for_restart(rt,rt.context_token,checkpoint['manifest_path'])
            rt.bridge.core.assert_awaited_once_with('games_stop',gameId='rimbot-trial')
            assert rt.shutdown.is_set() and rt.session_closing and not rt.connected
        else:
            with pytest.raises(ValueError,match='changed'): await stop_for_restart(rt,rt.context_token,checkpoint['manifest_path'])
    if change: rt.bridge.core.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('artifact',['game.rws','bridge.sqlite'])
async def test_tampered_checkpoint_is_rejected_before_resume(tmp_path,artifact):
    rt=fixture(tmp_path)
    result=await create_checkpoint(rt,rt.context_token)
    manifest=Path(result['manifest_path'])
    (manifest.parent/artifact).write_bytes(b'changed')
    with pytest.raises(ValueError,match='artifact changed'): prepare_resume(manifest)
    assert not (tmp_path/'resumed').exists()
    rt.store.close()


@pytest.mark.asyncio
async def test_attached_checkpoint_detaches_without_stopping_or_reloading_game(tmp_path):
    rt = fixture(tmp_path)
    rt.fresh = False
    result = await create_checkpoint(rt, rt.context_token)
    path = result['manifest_path']
    assert read_checkpoint(path)['owned'] is False
    with pytest.raises(ValueError, match='reload is forbidden'):
        install_saved_game(path)
    stopped = await stop_for_restart(rt, rt.context_token, path)
    assert stopped == {'stopped': True, 'game_stopped': False}
    rt.bridge.core.assert_not_awaited()
    assert rt.attached_game_detached and not rt.owned_game_stopped
    rt.store.close()
