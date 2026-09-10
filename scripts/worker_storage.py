"""Durable Linux worker storage with stopped-container evidence export."""
import sqlite3
from contextlib import closing


def storage_options(command, root, name, kind):
    state = dict(kind=kind)
    if kind == 'bind':
        return ['--mount', f'type=bind,source={root},target=/worker'], state
    volume = name+'-state'
    command('volume', 'create', volume, capture_output=True, text=True, check=True, timeout=30)
    state.update(volume=volume, retained=True)
    return ['--mount', f'type=volume,source={volume},target=/worker'], state


def export_storage(command, name, root, state):
    if state['kind'] == 'bind':
        return True
    try:
        # Never copy a live SQLite database and its changing WAL independently.
        running = command('inspect', '--format', '{{.State.Running}}', name,
                          capture_output=True, text=True, check=True, timeout=30).stdout.strip()
        if running == 'true':
            command('stop', '--time', '15', name, capture_output=True, text=True, check=True, timeout=30)
        elif running != 'false':
            raise ValueError('Unknown worker running state; evidence export refused')
        command('cp', name+':/worker/.', str(root), capture_output=True, text=True, check=True, timeout=180)
        databases = sorted(set(root.rglob('*.sqlite')) | set(root.rglob('*.db')))
        state['databases'] = []
        for database in databases:
            with closing(sqlite3.connect(database.as_uri()+'?mode=ro', uri=True)) as db:
                result = db.execute('PRAGMA integrity_check').fetchall()
            if result != [('ok',)]:
                raise ValueError('Exported controller database failed integrity_check')
            state['database_integrity'] = 'ok'
            state['databases'].append(str(database.relative_to(root)))
        state['exported'] = True
        return True
    except Exception as error:
        state.update(exported=False, export_error=repr(error))
        return False


def release_storage(command, state, passed):
    if state['kind'] != 'volume' or not passed:
        return
    try:
        result = command('volume', 'rm', state['volume'], capture_output=True, text=True, timeout=30)
        state['retained'] = result.returncode != 0
        if result.returncode:
            state['cleanup_error'] = result.stderr
    except Exception as error:
        state['cleanup_error'] = repr(error)
