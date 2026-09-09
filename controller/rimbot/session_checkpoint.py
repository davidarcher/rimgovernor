"""Matched native-save/SQLite checkpoints for owned game restarts."""
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
    if game.get('launchMode') != 'DirectPath': raise ValueError('Checkpoint requires an owned DirectPath game')
    folders = [a.split('=', 1)[1] for a in game.get('args', []) if a.startswith('-savedatafolder=')]
    if len(folders) != 1: raise ValueError('Cannot identify the private native save folder')
    folder = Path(folders[0]).resolve()
    if not folder.is_relative_to(root): raise ValueError('Save folder is outside the owned bridge root')
    return folder


async def create_checkpoint(rt, session_id):
    async with rt.lock:
        if not rt.connected or not rt.fresh: raise ValueError('Checkpoint requires a connected owned game')
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
            colony_id=identity['colonyId'], map_id=identity['mapId'], tick=tick, direction_revision=direction,
            game_sha256=digest(destination/'game.rws'), database_sha256=digest(destination/'bridge.sqlite'))
        path = destination / 'checkpoint.json'
        path.write_text(json.dumps(manifest, indent=2))  # Publish only after both durable artifacts exist.
        rt.note('session_checkpoint', 'Colony and controller state saved; session remains in Manual.', tick=tick)
        return dict(manifest_path=str(path), tick=tick, save_name=name)


async def stop_for_restart(rt, session_id, manifest_path):
    async with rt.lock:
        data = read_checkpoint(manifest_path)
        if not rt.fresh or Path(data['root']).resolve() != rt.root:
            raise ValueError('Checkpoint belongs to another owned session')
        await rt.sync_identity()
        status = await rt.game.query('home/status', colonists=False, threats=False)
        if (rt.context_token != session_id or rt.mode != 'manual' or rt.chat_revision != data['direction_revision']
                or rt.identity['colonyId'] != data['colony_id'] or rt.identity['mapId'] != data['map_id']
                or status.get('time', {}).get('ticksGame') != data['tick'] or status.get('time', {}).get('paused') is not True):
            raise ValueError('Session changed since checkpoint; game was not stopped')
        rt.session_closing = True
        try:
            await rt.bridge.core('games_stop', gameId=rt.bridge.game_id)
        except Exception:
            rt.session_closing = False
            raise
        rt.owned_game_stopped = True
        rt.stopped = True
        rt.connected = False
        rt.shutdown.set()
        return {'stopped': True}


def read_checkpoint(path):
    path = Path(path).resolve()
    data = json.loads(path.read_text())
    if data.get('version') != 1 or type(data.get('headless')) is not bool:
        raise ValueError('Unsupported checkpoint format')
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


def install_saved_game(path):
    path = Path(path).resolve()
    data = read_checkpoint(path)
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
