"""Matched native-save/SQLite checkpoints with explicit game ownership."""
import hashlib
import json
import shutil
import sqlite3
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def profile_path(root, headless):
    root = Path(root).resolve()
    config = root / ('config-headless' if headless else 'config') / 'config.json'
    game = json.loads(config.read_text())['games']['rimbot-trial']
    if game.get('launchMode') != 'DirectPath': raise ValueError('Checkpoint requires a DirectPath private profile')
    folders = [a.split('=', 1)[1] for a in game.get('args', []) if a.startswith('-savedatafolder=')]
    if len(folders) != 1: raise ValueError('Cannot identify the private native save folder')
    folder = Path(folders[0]).resolve()
    if not folder.is_relative_to(root): raise ValueError('Save folder is outside the owned bridge root')
    return folder


async def create_checkpoint(rt, session_id):
    async with rt.lock:
        if not rt.connected: raise ValueError('Checkpoint requires a connected game with a known private save profile')
        await rt.sync_identity()
        if session_id != rt.context_token: raise ValueError('Colony changed; refresh before saving')
        if any(e.get('kind') == 'human' and e.get('revision', 0) > rt.handled_revision for e in getattr(rt, 'chat', [])):
            raise ValueError('Wait for the current chat request to finish before checkpointing')
        folder = profile_path(rt.root, rt.headless)
        rt.chat_revision += 1
        direction = rt.chat_revision
        rt.handled_revision = rt.chat_revision
        rt.wake.clear()
        await rt.halt()
        if rt.draft_owners: raise ValueError('Owned drafts remain unresolved; checkpoint was not published')
        before = await rt.game.query('home/status', colonists=False, threats=False)
        tick = before.get('time', {}).get('ticksGame')
        if before.get('time', {}).get('paused') is not True or type(tick) is not int:
            raise ValueError('Native pause could not be verified; checkpoint was not published')
        identity = dict(rt.identity)
        name = 'RimBot-checkpoint-' + uuid.uuid4().hex
        await rt.bridge.call('rimworld/save_game', saveName=name)  # BridgeClient rejects native error receipts.
        await rt.sync_identity()
        after = await rt.game.query('home/status', colonists=False, threats=False)
        if (rt.context_token != session_id or rt.chat_revision != direction
                or after.get('time', {}).get('ticksGame') != tick or after.get('time', {}).get('paused') is not True):
            raise ValueError('Colony, player direction or clock changed during save; checkpoint was not published')
        native = folder / 'Saves' / (name + '.rws')
        if not native.is_file() or native.stat().st_size == 0: raise ValueError('Native save file was not produced')
        try:
            ET.parse(native)  # A partial native write cannot become a published checkpoint.
        except ET.ParseError as error:
            raise ValueError('Native save is incomplete; checkpoint was not published') from error
        rt.clock = dict(after['time'])
        destination = rt.root / 'checkpoints' / name
        destination.mkdir(parents=True, exist_ok=False)
        shutil.copy2(native, destination / 'game.rws')
        rt.persist()
        with sqlite3.connect(destination / 'bridge.sqlite') as backup:
            rt.store.db.backup(backup)
        manifest = dict(version=1, root=str(rt.root), headless=rt.headless, save_name=name,
            owned=rt.fresh, load_token=identity.get('loadToken', session_id.rsplit(':', 1)[-1]),
            colony_id=identity['colonyId'], map_id=identity['mapId'], tick=tick, direction_revision=direction,
            game_sha256=digest(destination/'game.rws'), database_sha256=digest(destination/'bridge.sqlite'))
        path = destination / 'checkpoint.json'
        path.write_text(json.dumps(manifest, indent=2))  # Publish only after both durable artifacts exist.
        rt.note('session_checkpoint', 'Colony and controller state saved; session remains in Manual.', tick=tick)
        return dict(manifest_path=str(path), tick=tick, save_name=name)


async def stop_for_restart(rt, session_id, manifest_path):
    async with rt.lock:
        data = read_checkpoint(manifest_path)
        if data.get('owned', True) != rt.fresh or Path(data['root']).resolve() != rt.root:
            raise ValueError('Checkpoint belongs to another owned session')
        await rt.sync_identity()
        status = await rt.game.query('home/status', colonists=False, threats=False)
        if (rt.context_token != session_id or rt.mode != 'manual' or rt.chat_revision != data['direction_revision']
                or rt.identity['colonyId'] != data['colony_id'] or rt.identity['mapId'] != data['map_id']
                or status.get('time', {}).get('ticksGame') != data['tick'] or status.get('time', {}).get('paused') is not True):
            raise ValueError('Session changed since checkpoint; game was not stopped')
        rt.session_closing = True
        try:
            if rt.fresh:
                await rt.bridge.core('games_stop', gameId=rt.bridge.game_id)
        except Exception:
            rt.session_closing = False
            raise
        rt.owned_game_stopped = rt.fresh
        rt.attached_game_detached = not rt.fresh
        rt.stopped = True
        rt.connected = False
        rt.shutdown.set()
        return {'stopped': True, 'game_stopped': rt.fresh}


def read_checkpoint(path):
    path = Path(path).resolve()
    data = json.loads(path.read_text())
    if data.get('version') != 1 or type(data.get('headless')) is not bool:
        raise ValueError('Unsupported checkpoint format')
    if type(data.get('owned', True)) is not bool or (data.get('owned') is False and not data.get('load_token')):
        raise ValueError('Checkpoint ownership is unavailable')
    root = Path(data['root']).resolve()
    if path.parent.parent != root/'checkpoints' or data.get('save_name') != path.parent.name:
        raise ValueError('Checkpoint does not belong to the named bridge root')
    for file, key in [('game.rws','game_sha256'), ('bridge.sqlite','database_sha256')]:
        if digest(path.parent/file) != data.get(key): raise ValueError('Checkpoint artifact changed: '+file)
    return data


def prepare_resume(path):
    path = Path(path).resolve()
    data = read_checkpoint(path)
    # Resume into a new database; the paired checkpoint remains immutable and reusable.
    state = Path(data['root'])/'resumed'/uuid.uuid4().hex
    state.mkdir(parents=True)
    shutil.copy2(path.parent/'bridge.sqlite',state/'bridge.sqlite')
    return data, state


def list_checkpoints(root):
    """Retain all pairs until explicitly deleted; corrupt pairs remain visible."""
    root = Path(root).resolve()
    rows = []
    for path in sorted((root/'checkpoints').glob('*/checkpoint.json')):
        row = {'manifest_path': str(path), 'save_name': path.parent.name, 'valid': False}
        try:
            data = read_checkpoint(path)
            if Path(data['root']).resolve() != root:
                raise ValueError('Checkpoint belongs to another session')
            row.update(valid=True, tick=data['tick'], colony_id=data['colony_id'], map_id=data['map_id'])
        except (ValueError, OSError, KeyError) as error:
            row['error'] = str(error)
        rows.append(row)
    return rows


async def delete_checkpoint(rt, session_id, manifest_path):
    """Delete only a verified pair in this session; preserve native profile saves."""
    async with rt.lock:
        if session_id != rt.context_token:
            raise ValueError('Colony changed; refresh before deleting a checkpoint')
        if getattr(rt, 'session_closing', False):
            raise ValueError('Session is restarting; checkpoint retained')
        path = Path(manifest_path).resolve()
        data = read_checkpoint(path)
        if Path(data['root']).resolve() != rt.root:
            raise ValueError('Checkpoint belongs to another session')
        if getattr(rt, 'resume', None) and Path(rt.resume).resolve() == path:
            raise ValueError('The active resume checkpoint must be retained')
        expected = {'checkpoint.json', 'game.rws', 'bridge.sqlite'}
        if {p.name for p in path.parent.iterdir()} != expected or any(
                p.is_symlink() or not p.is_file() for p in path.parent.iterdir()):
            raise ValueError('Checkpoint contains unexpected files; nothing deleted')
        # Unpublish first. An interrupted deletion cannot leave a resumable
        # manifest pointing at a partially removed pair.
        path.unlink()
        for name in ('game.rws', 'bridge.sqlite'):
            (path.parent/name).unlink()
        path.parent.rmdir()
        return {'deleted': True, 'save_name': data['save_name']}


def install_saved_game(path):
    path = Path(path).resolve()
    data = read_checkpoint(path)
    if data.get('owned') is False:
        raise ValueError('Attached checkpoints reconnect to the existing game; native reload is forbidden')
    profile = profile_path(data['root'], data['headless'])
    prefs = profile/'Config/Prefs.xml'
    if not prefs.is_file(): raise ValueError('Private profile preferences are unavailable')
    tree = ET.parse(prefs)
    pause = tree.getroot().find('pauseOnLoad')
    if pause is None: pause = ET.SubElement(tree.getroot(), 'pauseOnLoad')
    pause.text = 'True'
    tree.write(prefs, encoding='utf-8', xml_declaration=True)
    target = profile/'Saves'/(data['save_name']+'.rws')
    shutil.copy2(path.parent/'game.rws',target)
    return data
